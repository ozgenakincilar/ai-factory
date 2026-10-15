-- Versioned M7.2 migration. Never edit after release. Adds human-decision waiting and cancellation
-- to the M7.1 schema. Existing rows stay valid; no history is rewritten. Owner provisioning is
-- separate (scripts/orchestration-runtime-grants.sql).
ALTER TABLE orchestration."WorkflowRuns" DROP CONSTRAINT "WorkflowRuns_Status_check";
ALTER TABLE orchestration."WorkflowRuns" ADD CONSTRAINT "WorkflowRuns_Status_check"
    CHECK ("Status" IN ('Queued','Running','Waiting','Succeeded','Failed','Cancelled'));
ALTER TABLE orchestration."WorkflowRuns" ADD COLUMN "CancelledBy" uuid REFERENCES access."Principals"("Id");
ALTER TABLE orchestration."WorkflowRuns" ADD COLUMN "CancelledAt" timestamptz;
ALTER TABLE orchestration."WorkflowRuns" ADD CONSTRAINT run_cancellation
    CHECK ((("Status"='Cancelled') = ("CancelledAt" IS NOT NULL)) AND (("CancelledAt" IS NULL) = ("CancelledBy" IS NULL)));

ALTER TABLE orchestration."WorkflowSteps" DROP CONSTRAINT "WorkflowSteps_Status_check";
ALTER TABLE orchestration."WorkflowSteps" ADD CONSTRAINT "WorkflowSteps_Status_check"
    CHECK ("Status" IN ('Pending','Running','Waiting','Succeeded','Failed','Cancelled'));

ALTER TABLE orchestration."StepAttempts" DROP CONSTRAINT "StepAttempts_Outcome_check";
ALTER TABLE orchestration."StepAttempts" ADD CONSTRAINT "StepAttempts_Outcome_check"
    CHECK ("Outcome" IN ('Running','Succeeded','Failed','Abandoned','Suspended','Cancelled'));

-- Reasons and options are static Factory codes; every decision offers "stop"; no timeout.
CREATE TABLE orchestration."StepDecisions" (
    "Id" uuid PRIMARY KEY,
    "RunId" uuid NOT NULL, "Ordinal" integer NOT NULL,
    "AttemptId" uuid NOT NULL UNIQUE REFERENCES orchestration."StepAttempts"("Id"),
    "Reason" varchar(64) NOT NULL,
    "Options" text[] NOT NULL CHECK (pg_catalog.cardinality("Options") BETWEEN 2 AND 8 AND 'stop'=ANY("Options")),
    "Status" text NOT NULL CHECK ("Status" IN ('Pending','Decided','Cancelled')),
    "Choice" varchar(64), "DecidedBy" uuid REFERENCES access."Principals"("Id"),
    "CreatedAt" timestamptz NOT NULL, "DecidedAt" timestamptz,
    FOREIGN KEY ("RunId","Ordinal") REFERENCES orchestration."WorkflowSteps"("RunId","Ordinal"),
    CHECK (("Status"='Decided') = ("Choice" IS NOT NULL)),
    CHECK (("Status"='Decided') = ("DecidedBy" IS NOT NULL)),
    CHECK (("Status"='Pending') = ("DecidedAt" IS NULL)),
    CHECK ("Choice" IS NULL OR "Choice"=ANY("Options"))
);
CREATE UNIQUE INDEX one_pending_decision_per_run ON orchestration."StepDecisions"("RunId") WHERE "Status"='Pending';
CREATE TRIGGER decisions_no_delete BEFORE DELETE OR TRUNCATE ON orchestration."StepDecisions"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();

CREATE FUNCTION orchestration.valid_code(p text) RETURNS boolean LANGUAGE sql IMMUTABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT p IS NOT NULL AND p ~ '^[a-z_]{1,64}$';
$$;

CREATE FUNCTION orchestration.guard_decision() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    IF TG_OP='INSERT' THEN
        IF NEW."Status"<>'Pending' OR NEW."Choice" IS NOT NULL OR NEW."DecidedBy" IS NOT NULL OR NOT orchestration.valid_code(NEW."Reason")
            OR EXISTS (SELECT 1 FROM pg_catalog.unnest(NEW."Options") o WHERE NOT orchestration.valid_code(o))
            OR (SELECT pg_catalog.count(DISTINCT o) FROM pg_catalog.unnest(NEW."Options") o)<>pg_catalog.cardinality(NEW."Options")
            THEN RAISE EXCEPTION 'invalid_decision_transition' USING ERRCODE='23514'; END IF;
        RETURN NEW;
    END IF;
    IF OLD."Status"<>'Pending' OR NEW."Status" NOT IN ('Decided','Cancelled')
        OR (pg_catalog.to_jsonb(NEW)-ARRAY['Status','Choice','DecidedBy','DecidedAt']) IS DISTINCT FROM
           (pg_catalog.to_jsonb(OLD)-ARRAY['Status','Choice','DecidedBy','DecidedAt'])
        THEN RAISE EXCEPTION 'invalid_decision_transition' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER decision_transition BEFORE INSERT OR UPDATE ON orchestration."StepDecisions" FOR EACH ROW EXECUTE FUNCTION orchestration.guard_decision();

CREATE OR REPLACE FUNCTION orchestration.guard_run() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    IF TG_OP='INSERT' THEN
        IF NEW."Status"<>'Queued' OR NEW."CancelledAt" IS NOT NULL THEN RAISE EXCEPTION 'invalid_run_transition' USING ERRCODE='23514'; END IF;
        RETURN NEW;
    END IF;
    IF OLD."Status" IN ('Succeeded','Failed','Cancelled') OR NOT (
            (OLD."Status"='Queued' AND NEW."Status" IN ('Queued','Running','Failed','Cancelled')) OR
            (OLD."Status"='Running' AND NEW."Status" IN ('Running','Waiting','Succeeded','Failed','Cancelled')) OR
            (OLD."Status"='Waiting' AND NEW."Status" IN ('Running','Cancelled')))
        OR (pg_catalog.to_jsonb(NEW)-ARRAY['Status','FailureCode','WorkItemId','UpdatedAt','CancelledBy','CancelledAt']) IS DISTINCT FROM
           (pg_catalog.to_jsonb(OLD)-ARRAY['Status','FailureCode','WorkItemId','UpdatedAt','CancelledBy','CancelledAt'])
        THEN RAISE EXCEPTION 'invalid_run_transition' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION orchestration.guard_step() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    IF TG_OP='INSERT' THEN
        IF NEW."Status"<>'Pending' OR NEW."Attempts"<>0 THEN RAISE EXCEPTION 'invalid_step_transition' USING ERRCODE='23514'; END IF;
        RETURN NEW;
    END IF;
    IF OLD."Status" IN ('Succeeded','Failed','Cancelled') OR NEW."RunId"<>OLD."RunId" OR NEW."Ordinal"<>OLD."Ordinal" OR NEW."Kind"<>OLD."Kind"
        OR NOT ((OLD."Status"='Pending' AND NEW."Status"='Running' AND NEW."Attempts"=OLD."Attempts"+1)
             OR (OLD."Status"='Pending' AND NEW."Status"='Cancelled' AND NEW."Attempts"=OLD."Attempts")
             OR (OLD."Status"='Running' AND NEW."Status" IN ('Succeeded','Failed','Pending','Waiting','Cancelled') AND NEW."Attempts"=OLD."Attempts")
             OR (OLD."Status"='Waiting' AND NEW."Status" IN ('Pending','Cancelled') AND NEW."Attempts"=OLD."Attempts"))
        THEN RAISE EXCEPTION 'invalid_step_transition' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION orchestration.check_run() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE rid uuid; r orchestration."WorkflowRuns"%ROWTYPE;
BEGIN
    IF TG_TABLE_NAME='WorkflowRuns' THEN rid:=NEW."Id"; ELSE rid:=NEW."RunId"; END IF;
    SELECT * INTO STRICT r FROM orchestration."WorkflowRuns" WHERE "Id"=rid;
    IF (SELECT pg_catalog.count(*) FROM orchestration."WorkflowSteps" WHERE "RunId"=rid)<>3
        OR EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" s WHERE s."RunId"=rid AND (
            (s."Status"='Running') <> EXISTS (SELECT 1 FROM orchestration."StepAttempts" a
                WHERE a."RunId"=rid AND a."Ordinal"=s."Ordinal" AND a."Outcome"='Running')
            OR (s."Status"='Waiting') <> EXISTS (SELECT 1 FROM orchestration."StepDecisions" d
                WHERE d."RunId"=rid AND d."Ordinal"=s."Ordinal" AND d."Status"='Pending')
            OR s."Attempts"<>(SELECT pg_catalog.count(*) FROM orchestration."StepAttempts" a WHERE a."RunId"=rid AND a."Ordinal"=s."Ordinal")
            OR (s."Status" IN ('Running','Waiting','Succeeded','Failed') AND EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" p
                WHERE p."RunId"=rid AND p."Ordinal"<s."Ordinal" AND p."Status"<>'Succeeded'))))
        OR EXISTS (SELECT 1 FROM orchestration."StepDecisions" d JOIN orchestration."StepAttempts" a ON a."Id"=d."AttemptId"
            WHERE d."RunId"=rid AND (a."RunId"<>d."RunId" OR a."Ordinal"<>d."Ordinal" OR a."Outcome"<>'Suspended'))
        OR (r."Status"='Queued' AND EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" WHERE "RunId"=rid AND ("Status"<>'Pending' OR "Attempts"<>0)))
        OR (r."Status"='Running' AND EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" WHERE "RunId"=rid AND "Status" IN ('Waiting','Failed','Cancelled')))
        OR (r."Status"='Waiting' AND ((SELECT pg_catalog.count(*) FROM orchestration."WorkflowSteps" WHERE "RunId"=rid AND "Status"='Waiting')<>1
            OR EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" WHERE "RunId"=rid AND "Status" IN ('Running','Failed','Cancelled'))))
        OR (r."Status"='Failed' AND (EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" WHERE "RunId"=rid AND "Status" IN ('Pending','Running','Waiting'))
            OR (SELECT pg_catalog.count(*) FROM orchestration."WorkflowSteps" WHERE "RunId"=rid AND "Status"='Failed')<>1))
        OR (r."Status"='Cancelled' AND EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" WHERE "RunId"=rid AND "Status" IN ('Pending','Running','Waiting','Failed')))
        OR (r."Status"='Succeeded' AND (EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" WHERE "RunId"=rid AND "Status"<>'Succeeded')
            OR NOT EXISTS (SELECT 1 FROM lifecycle."GenerationCommands" g WHERE g."RunId"=rid AND g."Status"='Published'
                AND g."WorkItemId"=r."WorkItemId" AND g."ProjectId"=r."ProjectId" AND g."CaseId"=r."CaseId"
                AND g."PrincipalId"=r."RequesterId" AND g."InputHash"=r."InputHash")))
        THEN RAISE EXCEPTION 'orchestration_integrity' USING ERRCODE='23514'; END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER orchestration_decision AFTER INSERT OR UPDATE ON orchestration."StepDecisions" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION orchestration.check_run();

-- Safe decision projection: static codes and outcome only; never decider identity.
CREATE FUNCTION orchestration.decision_view(d orchestration."StepDecisions") RETURNS jsonb LANGUAGE sql IMMUTABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT pg_catalog.jsonb_build_object('id',d."Id",'ordinal',d."Ordinal",'reason',d."Reason",'options',pg_catalog.to_jsonb(d."Options"),
        'status',d."Status",'choice',d."Choice");
$$;

CREATE OR REPLACE FUNCTION orchestration.view(p orchestration."WorkflowRuns") RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT pg_catalog.jsonb_build_object('runId',p."Id",'projectId',p."ProjectId",'status',p."Status",'failureCode',p."FailureCode",
        'steps',(SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object('ordinal',s."Ordinal",'kind',s."Kind",'status',s."Status",'attempts',s."Attempts",
                'abandoned',(SELECT pg_catalog.count(*) FROM orchestration."StepAttempts" a WHERE a."RunId"=s."RunId" AND a."Ordinal"=s."Ordinal" AND a."Outcome"='Abandoned'))
                ORDER BY s."Ordinal")
            FROM orchestration."WorkflowSteps" s WHERE s."RunId"=p."Id"),
        'publication',(SELECT pg_catalog.jsonb_build_object('workItemId',g."WorkItemId",'caseId',g."CaseId",'version',g."NextVersion",'contentHash',g."ContentHash")
            FROM lifecycle."GenerationCommands" g WHERE g."RunId"=p."Id" AND g."Status"='Published' AND p."Status"='Succeeded'),
        'decisions',(SELECT pg_catalog.jsonb_agg(orchestration.decision_view(d) ORDER BY d."CreatedAt",d."Id")
            FROM orchestration."StepDecisions" d WHERE d."RunId"=p."Id"));
$$;

-- Waiting runs stay active: they count toward the per-project bound until decided or cancelled.
CREATE OR REPLACE FUNCTION orchestration.submit(p_project text,p_issuer text,p_subject text,p_kind text,p_key uuid,p_version text,p_input jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE actor uuid; fp text; r orchestration."WorkflowRuns"%ROWTYPE; replay boolean:=false; now timestamptz:=pg_catalog.clock_timestamp(); rid uuid;
BEGIN
    actor:=lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind);
    IF p_key IS NULL OR p_key='00000000-0000-0000-0000-000000000000'::uuid OR p_version IS DISTINCT FROM 'analysis-architecture-v1'
        OR p_input->>'projectId' IS DISTINCT FROM p_project OR pg_catalog.length(pg_catalog.btrim(p_input->>'task'))=0
        OR pg_catalog.octet_length(lifecycle.input_bytes(p_input))>65536 THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    fp:=orchestration.fingerprint(p_project,p_version,p_input);
    PERFORM pg_catalog.pg_advisory_xact_lock(6043001,pg_catalog.hashtext(p_project));
    SELECT * INTO r FROM orchestration."WorkflowRuns" WHERE "ProjectId"=p_project AND "RequesterId"=actor AND "Key"=p_key;
    IF FOUND THEN
        IF r."Fingerprint"<>fp THEN RAISE EXCEPTION 'command_conflict' USING ERRCODE='P6009'; END IF;
        replay:=true;
    ELSE
        IF (SELECT pg_catalog.count(*) FROM orchestration."WorkflowRuns" WHERE "ProjectId"=p_project AND "Status" IN ('Queued','Running','Waiting'))>=20
            THEN RAISE EXCEPTION 'capacity' USING ERRCODE='P6029'; END IF;
        rid:=pg_catalog.gen_random_uuid();
        INSERT INTO orchestration."WorkflowRuns" ("Id","ProjectId","RequesterId","Key","WorkflowVersion","Fingerprint","Input","InputHash","CaseId","Status","CreatedAt","UpdatedAt")
            VALUES (rid,p_project,actor,p_key,p_version,fp,p_input,lifecycle.input_hash(p_project,p_input),pg_catalog.gen_random_uuid(),'Queued',now,now) RETURNING * INTO r;
        INSERT INTO orchestration."WorkflowSteps" ("RunId","Ordinal","Kind","Status","Attempts") VALUES
            (rid,0,'Analysis','Pending',0),(rid,1,'Architecture','Pending',0),(rid,2,'Publication','Pending',0);
    END IF;
    RETURN pg_catalog.jsonb_build_object('runId',r."Id",'status',r."Status",'replayed',replay);
END;
$$;

CREATE FUNCTION orchestration.cancel_run(p_run uuid,p_actor uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    -- A worker holding the cancelled attempt loses its lease at its next renew/complete.
    UPDATE orchestration."StepAttempts" SET "Outcome"='Cancelled',"FinishedAt"=now WHERE "RunId"=p_run AND "Outcome"='Running';
    UPDATE orchestration."StepDecisions" SET "Status"='Cancelled',"DecidedAt"=now WHERE "RunId"=p_run AND "Status"='Pending';
    UPDATE orchestration."WorkflowSteps" SET "Status"='Cancelled' WHERE "RunId"=p_run AND "Status" IN ('Pending','Running','Waiting');
    UPDATE orchestration."WorkflowRuns" SET "Status"='Cancelled',"CancelledBy"=p_actor,"CancelledAt"=now,"UpdatedAt"=now WHERE "Id"=p_run;
END;
$$;

CREATE OR REPLACE FUNCTION orchestration.claim(p_worker text,p_token bytea) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE r orchestration."WorkflowRuns"%ROWTYPE; s orchestration."WorkflowSteps"%ROWTYPE; x record; bound integer; lost bigint;
    now timestamptz:=pg_catalog.clock_timestamp(); aid uuid:=pg_catalog.gen_random_uuid();
BEGIN
    IF p_worker IS NULL OR pg_catalog.length(p_worker) NOT BETWEEN 1 AND 200 OR p_token IS NULL OR pg_catalog.octet_length(p_token)<>32
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    -- Recover expired leases. Locked runs are skipped and recovered by a later claim.
    FOR x IN SELECT a."Id",a."RunId",a."Ordinal" FROM orchestration."StepAttempts" a JOIN orchestration."WorkflowRuns" w ON w."Id"=a."RunId"
        WHERE a."Outcome"='Running' AND a."LeaseExpiresAt"<=now ORDER BY a."LeaseExpiresAt" LIMIT 10 FOR UPDATE OF w, a SKIP LOCKED LOOP
        UPDATE orchestration."StepAttempts" SET "Outcome"='Abandoned',"FinishedAt"=now WHERE "Id"=x."Id";
        SELECT * INTO STRICT s FROM orchestration."WorkflowSteps" WHERE "RunId"=x."RunId" AND "Ordinal"=x."Ordinal" FOR UPDATE;
        -- Same bounds as WorkflowRun.MaxAttempts; only lost leases count, not suspensions.
        bound:=CASE WHEN s."Kind"='Publication' THEN 3 ELSE 2 END;
        SELECT pg_catalog.count(*) INTO lost FROM orchestration."StepAttempts"
            WHERE "RunId"=x."RunId" AND "Ordinal"=x."Ordinal" AND "Outcome"='Abandoned';
        IF lost<bound THEN
            UPDATE orchestration."WorkflowSteps" SET "Status"='Pending' WHERE "RunId"=x."RunId" AND "Ordinal"=x."Ordinal";
        ELSE
            PERFORM orchestration.fail_run(x."RunId",x."Ordinal",'step_attempts_exhausted');
        END IF;
    END LOOP;
    SELECT w.* INTO r FROM orchestration."WorkflowRuns" w
        WHERE w."Status" IN ('Queued','Running')
            AND NOT EXISTS (SELECT 1 FROM orchestration."StepAttempts" a WHERE a."RunId"=w."Id" AND a."Outcome"='Running')
            AND EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" t WHERE t."RunId"=w."Id" AND t."Status"='Pending')
        ORDER BY w."CreatedAt",w."Id" LIMIT 1 FOR UPDATE OF w SKIP LOCKED;
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT * INTO STRICT s FROM orchestration."WorkflowSteps" WHERE "RunId"=r."Id" AND "Status"='Pending' ORDER BY "Ordinal" LIMIT 1 FOR UPDATE;
    UPDATE orchestration."WorkflowSteps" SET "Status"='Running',"Attempts"="Attempts"+1 WHERE "RunId"=r."Id" AND "Ordinal"=s."Ordinal" RETURNING * INTO s;
    INSERT INTO orchestration."StepAttempts" ("Id","RunId","Ordinal","Number","WorkerId","TokenHash","Outcome","StartedAt","LeaseExpiresAt")
        VALUES (aid,r."Id",s."Ordinal",s."Attempts",p_worker,pg_catalog.sha256(p_token),'Running',now,now+interval '60 seconds');
    UPDATE orchestration."WorkflowRuns" SET "Status"='Running',"UpdatedAt"=now WHERE "Id"=r."Id";
    RETURN pg_catalog.jsonb_build_object('runId',r."Id",'attemptId',aid,'ordinal',s."Ordinal",'kind',s."Kind",'caseId',r."CaseId",'request',r."Input",
        'steps',orchestration.view(r)->'steps',
        'analysis',(SELECT "Output" FROM orchestration."WorkflowSteps" WHERE "RunId"=r."Id" AND "Ordinal"=0),
        'architecture',(SELECT "Output" FROM orchestration."WorkflowSteps" WHERE "RunId"=r."Id" AND "Ordinal"=1),
        'decisions',(SELECT pg_catalog.jsonb_agg(orchestration.decision_view(d) ORDER BY d."CreatedAt",d."Id")
            FROM orchestration."StepDecisions" d WHERE d."RunId"=r."Id" AND d."Ordinal"=s."Ordinal" AND d."Status"='Decided'));
END;
$$;

CREATE FUNCTION orchestration.request_decision(p_attempt uuid,p_token bytea,p_reason text,p_options text[]) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE a orchestration."StepAttempts"%ROWTYPE; did uuid:=pg_catalog.gen_random_uuid(); now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    a:=orchestration.lock_attempt(p_attempt,p_token);
    IF NOT orchestration.valid_code(p_reason) OR p_options IS NULL OR pg_catalog.cardinality(p_options) NOT BETWEEN 2 AND 8
        OR NOT ('stop'=ANY(p_options)) OR EXISTS (SELECT 1 FROM pg_catalog.unnest(p_options) o WHERE NOT orchestration.valid_code(o))
        OR (SELECT pg_catalog.count(DISTINCT o) FROM pg_catalog.unnest(p_options) o)<>pg_catalog.cardinality(p_options)
        OR NOT EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" WHERE "RunId"=a."RunId" AND "Ordinal"=a."Ordinal" AND "Kind" IN ('Analysis','Architecture'))
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    UPDATE orchestration."StepAttempts" SET "Outcome"='Suspended',"FinishedAt"=now WHERE "Id"=a."Id";
    UPDATE orchestration."WorkflowSteps" SET "Status"='Waiting' WHERE "RunId"=a."RunId" AND "Ordinal"=a."Ordinal";
    UPDATE orchestration."WorkflowRuns" SET "Status"='Waiting',"UpdatedAt"=now WHERE "Id"=a."RunId";
    INSERT INTO orchestration."StepDecisions" ("Id","RunId","Ordinal","AttemptId","Reason","Options","Status","CreatedAt")
        VALUES (did,a."RunId",a."Ordinal",a."Id",p_reason,p_options,'Pending',now);
    RETURN did;
END;
$$;

CREATE FUNCTION orchestration.cancel(p_project text,p_issuer text,p_subject text,p_kind text,p_run uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE actor uuid; r orchestration."WorkflowRuns"%ROWTYPE;
BEGIN
    actor:=lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind);
    SELECT * INTO r FROM orchestration."WorkflowRuns" WHERE "Id"=p_run AND "ProjectId"=p_project FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    IF r."Status" IN ('Succeeded','Failed') THEN RAISE EXCEPTION 'run_finished' USING ERRCODE='P6009'; END IF;
    IF r."Status"<>'Cancelled' THEN PERFORM orchestration.cancel_run(r."Id",actor); END IF;
    SELECT * INTO STRICT r FROM orchestration."WorkflowRuns" WHERE "Id"=p_run;
    RETURN orchestration.view(r);
END;
$$;

CREATE FUNCTION orchestration.decide(p_project text,p_issuer text,p_subject text,p_kind text,p_run uuid,p_decision uuid,p_choice text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE actor uuid; r orchestration."WorkflowRuns"%ROWTYPE; d orchestration."StepDecisions"%ROWTYPE; now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    actor:=lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind,true);
    -- Interim authority until M8 tenant roles: an active Human project Reviewer, locked against revocation.
    PERFORM 1 FROM access."ProjectMemberships" WHERE "ProjectId"=p_project AND "PrincipalId"=actor AND "PrincipalKind"='Human'
        AND "Role"='Reviewer' AND "IsActive" FOR SHARE;
    IF p_kind<>'Human' OR NOT FOUND THEN RAISE EXCEPTION 'forbidden' USING ERRCODE='42501'; END IF;
    SELECT * INTO r FROM orchestration."WorkflowRuns" WHERE "Id"=p_run AND "ProjectId"=p_project FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    SELECT * INTO d FROM orchestration."StepDecisions" WHERE "Id"=p_decision AND "RunId"=p_run FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    IF NOT orchestration.valid_code(p_choice) OR NOT (p_choice=ANY(d."Options")) THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    -- An identical repeated decision is a replay; any other change to a settled decision conflicts.
    IF d."Status"='Decided' AND d."Choice"=p_choice THEN RETURN orchestration.view(r); END IF;
    IF d."Status"<>'Pending' OR r."Status"<>'Waiting' THEN RAISE EXCEPTION 'decision_settled' USING ERRCODE='P6009'; END IF;
    UPDATE orchestration."StepDecisions" SET "Status"='Decided',"Choice"=p_choice,"DecidedBy"=actor,"DecidedAt"=now WHERE "Id"=d."Id";
    IF p_choice='stop' THEN
        PERFORM orchestration.cancel_run(r."Id",actor);
    ELSE
        UPDATE orchestration."WorkflowSteps" SET "Status"='Pending' WHERE "RunId"=r."Id" AND "Ordinal"=d."Ordinal";
        UPDATE orchestration."WorkflowRuns" SET "Status"='Running',"UpdatedAt"=now WHERE "Id"=r."Id";
    END IF;
    SELECT * INTO STRICT r FROM orchestration."WorkflowRuns" WHERE "Id"=p_run;
    RETURN orchestration.view(r);
END;
$$;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA orchestration FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA orchestration FROM PUBLIC;
