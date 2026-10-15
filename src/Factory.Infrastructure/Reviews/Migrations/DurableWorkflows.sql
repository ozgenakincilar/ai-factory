-- Versioned M7.1 migration. Never edit after release. Owner provisioning is separate
-- (scripts/orchestration-runtime-grants.sql). M6 tables, functions and guards are unchanged.
CREATE SCHEMA orchestration;
REVOKE ALL ON SCHEMA orchestration FROM PUBLIC;

CREATE TABLE orchestration."WorkflowRuns" (
    "Id" uuid PRIMARY KEY,
    "ProjectId" varchar(200) NOT NULL REFERENCES access."Projects"("Id"),
    "RequesterId" uuid NOT NULL REFERENCES access."Principals"("Id"),
    "Key" uuid NOT NULL,
    "WorkflowVersion" text NOT NULL CHECK ("WorkflowVersion"='analysis-architecture-v1'),
    "Fingerprint" varchar(64) NOT NULL, "Input" jsonb NOT NULL, "InputHash" varchar(64) NOT NULL,
    "CaseId" uuid NOT NULL UNIQUE,
    "Status" text NOT NULL CHECK ("Status" IN ('Queued','Running','Succeeded','Failed')),
    "FailureCode" text, "WorkItemId" uuid,
    "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL,
    UNIQUE ("ProjectId","RequesterId","Key"),
    CHECK (("Status"='Failed') = ("FailureCode" IS NOT NULL)),
    CHECK (("Status"='Succeeded') = ("WorkItemId" IS NOT NULL)),
    FOREIGN KEY ("WorkItemId","ProjectId") REFERENCES lifecycle."WorkItems"("Id","ProjectId") DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX runnable_runs ON orchestration."WorkflowRuns"("CreatedAt","Id") WHERE "Status" IN ('Queued','Running');

CREATE TABLE orchestration."WorkflowSteps" (
    "RunId" uuid NOT NULL REFERENCES orchestration."WorkflowRuns"("Id"),
    "Ordinal" integer NOT NULL,
    "Kind" text NOT NULL,
    "Status" text NOT NULL CHECK ("Status" IN ('Pending','Running','Succeeded','Failed','Cancelled')),
    "Attempts" integer NOT NULL CHECK ("Attempts">=0),
    "Output" jsonb,
    PRIMARY KEY ("RunId","Ordinal"),
    CHECK (("Kind"='Analysis' AND "Ordinal"=0) OR ("Kind"='Architecture' AND "Ordinal"=1) OR ("Kind"='Publication' AND "Ordinal"=2)),
    -- Checkpoints exist exactly for succeeded model steps; publication output lives in the review tables.
    CHECK (("Output" IS NOT NULL) = ("Status"='Succeeded' AND "Kind"<>'Publication')),
    CHECK ("Output" IS NULL OR pg_catalog.jsonb_typeof("Output")='object')
);

CREATE TABLE orchestration."StepAttempts" (
    "Id" uuid PRIMARY KEY,
    "RunId" uuid NOT NULL, "Ordinal" integer NOT NULL, "Number" integer NOT NULL CHECK ("Number">0),
    "WorkerId" varchar(200) NOT NULL CHECK (pg_catalog.length("WorkerId")>0),
    "TokenHash" bytea NOT NULL CHECK (pg_catalog.octet_length("TokenHash")=32),
    "Outcome" text NOT NULL CHECK ("Outcome" IN ('Running','Succeeded','Failed','Abandoned')),
    "FailureCode" text,
    "StartedAt" timestamptz NOT NULL, "LeaseExpiresAt" timestamptz NOT NULL, "FinishedAt" timestamptz,
    FOREIGN KEY ("RunId","Ordinal") REFERENCES orchestration."WorkflowSteps"("RunId","Ordinal"),
    UNIQUE ("RunId","Ordinal","Number"),
    CHECK (("Outcome"='Running') = ("FinishedAt" IS NULL)),
    CHECK (("Outcome"='Failed') = ("FailureCode" IS NOT NULL))
);
-- M7.1 executes one step of a run at a time.
CREATE UNIQUE INDEX one_running_attempt_per_run ON orchestration."StepAttempts"("RunId") WHERE "Outcome"='Running';
CREATE INDEX expired_leases ON orchestration."StepAttempts"("LeaseExpiresAt") WHERE "Outcome"='Running';

CREATE TRIGGER runs_no_delete BEFORE DELETE OR TRUNCATE ON orchestration."WorkflowRuns"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();
CREATE TRIGGER steps_no_delete BEFORE DELETE OR TRUNCATE ON orchestration."WorkflowSteps"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();
CREATE TRIGGER attempts_no_delete BEFORE DELETE OR TRUNCATE ON orchestration."StepAttempts"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();

CREATE FUNCTION orchestration.guard_run() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    IF TG_OP='INSERT' THEN
        IF NEW."Status"<>'Queued' THEN RAISE EXCEPTION 'invalid_run_transition' USING ERRCODE='23514'; END IF;
        RETURN NEW;
    END IF;
    IF OLD."Status" IN ('Succeeded','Failed') OR NOT (
            (OLD."Status"='Queued' AND NEW."Status" IN ('Queued','Running','Failed')) OR
            (OLD."Status"='Running' AND NEW."Status" IN ('Running','Succeeded','Failed')))
        OR (pg_catalog.to_jsonb(NEW)-ARRAY['Status','FailureCode','WorkItemId','UpdatedAt']) IS DISTINCT FROM
           (pg_catalog.to_jsonb(OLD)-ARRAY['Status','FailureCode','WorkItemId','UpdatedAt'])
        THEN RAISE EXCEPTION 'invalid_run_transition' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER run_transition BEFORE INSERT OR UPDATE ON orchestration."WorkflowRuns" FOR EACH ROW EXECUTE FUNCTION orchestration.guard_run();

CREATE FUNCTION orchestration.guard_step() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    IF TG_OP='INSERT' THEN
        IF NEW."Status"<>'Pending' OR NEW."Attempts"<>0 THEN RAISE EXCEPTION 'invalid_step_transition' USING ERRCODE='23514'; END IF;
        RETURN NEW;
    END IF;
    IF OLD."Status" IN ('Succeeded','Failed','Cancelled') OR NEW."RunId"<>OLD."RunId" OR NEW."Ordinal"<>OLD."Ordinal" OR NEW."Kind"<>OLD."Kind"
        OR NOT ((OLD."Status"='Pending' AND NEW."Status"='Running' AND NEW."Attempts"=OLD."Attempts"+1)
             OR (OLD."Status"='Pending' AND NEW."Status"='Cancelled' AND NEW."Attempts"=OLD."Attempts")
             OR (OLD."Status"='Running' AND NEW."Status" IN ('Succeeded','Failed','Pending') AND NEW."Attempts"=OLD."Attempts"))
        THEN RAISE EXCEPTION 'invalid_step_transition' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER step_transition BEFORE INSERT OR UPDATE ON orchestration."WorkflowSteps" FOR EACH ROW EXECUTE FUNCTION orchestration.guard_step();

CREATE FUNCTION orchestration.guard_attempt() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    IF TG_OP='INSERT' THEN
        IF NEW."Outcome"<>'Running' THEN RAISE EXCEPTION 'invalid_attempt_transition' USING ERRCODE='23514'; END IF;
        RETURN NEW;
    END IF;
    IF OLD."Outcome"<>'Running'
        OR (pg_catalog.to_jsonb(NEW)-ARRAY['Outcome','FailureCode','FinishedAt','LeaseExpiresAt']) IS DISTINCT FROM
           (pg_catalog.to_jsonb(OLD)-ARRAY['Outcome','FailureCode','FinishedAt','LeaseExpiresAt'])
        -- Lease time changes only while Running; shortening only reduces ownership.
        OR (NEW."Outcome"<>'Running' AND NEW."LeaseExpiresAt"<>OLD."LeaseExpiresAt")
        THEN RAISE EXCEPTION 'invalid_attempt_transition' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER attempt_transition BEFORE INSERT OR UPDATE ON orchestration."StepAttempts" FOR EACH ROW EXECUTE FUNCTION orchestration.guard_attempt();

-- Whole-run consistency at COMMIT, independent of the function that changed it.
CREATE FUNCTION orchestration.check_run() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE rid uuid; r orchestration."WorkflowRuns"%ROWTYPE;
BEGIN
    IF TG_TABLE_NAME='WorkflowRuns' THEN rid:=NEW."Id"; ELSE rid:=NEW."RunId"; END IF;
    SELECT * INTO STRICT r FROM orchestration."WorkflowRuns" WHERE "Id"=rid;
    IF (SELECT pg_catalog.count(*) FROM orchestration."WorkflowSteps" WHERE "RunId"=rid)<>3
        OR EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" s WHERE s."RunId"=rid AND (
            (s."Status"='Running') <> EXISTS (SELECT 1 FROM orchestration."StepAttempts" a
                WHERE a."RunId"=rid AND a."Ordinal"=s."Ordinal" AND a."Outcome"='Running')
            OR s."Attempts"<>(SELECT pg_catalog.count(*) FROM orchestration."StepAttempts" a WHERE a."RunId"=rid AND a."Ordinal"=s."Ordinal")
            OR (s."Status" IN ('Running','Succeeded','Failed') AND EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" p
                WHERE p."RunId"=rid AND p."Ordinal"<s."Ordinal" AND p."Status"<>'Succeeded'))))
        OR (r."Status"='Queued' AND EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" WHERE "RunId"=rid AND ("Status"<>'Pending' OR "Attempts"<>0)))
        OR (r."Status"='Running' AND EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" WHERE "RunId"=rid AND "Status" IN ('Failed','Cancelled')))
        OR (r."Status"='Failed' AND (EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" WHERE "RunId"=rid AND "Status" IN ('Pending','Running'))
            OR (SELECT pg_catalog.count(*) FROM orchestration."WorkflowSteps" WHERE "RunId"=rid AND "Status"='Failed')<>1))
        OR (r."Status"='Succeeded' AND (EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" WHERE "RunId"=rid AND "Status"<>'Succeeded')
            OR NOT EXISTS (SELECT 1 FROM lifecycle."GenerationCommands" g WHERE g."RunId"=rid AND g."Status"='Published'
                AND g."WorkItemId"=r."WorkItemId" AND g."ProjectId"=r."ProjectId" AND g."CaseId"=r."CaseId"
                AND g."PrincipalId"=r."RequesterId" AND g."InputHash"=r."InputHash")))
        THEN RAISE EXCEPTION 'orchestration_integrity' USING ERRCODE='23514'; END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER orchestration_run AFTER INSERT OR UPDATE ON orchestration."WorkflowRuns" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION orchestration.check_run();
CREATE CONSTRAINT TRIGGER orchestration_step AFTER INSERT OR UPDATE ON orchestration."WorkflowSteps" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION orchestration.check_run();
CREATE CONSTRAINT TRIGGER orchestration_attempt AFTER INSERT OR UPDATE ON orchestration."StepAttempts" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION orchestration.check_run();

CREATE FUNCTION orchestration.fingerprint(p_project text,p_version text,p jsonb) RETURNS text
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to('UAF-M7-RUN','UTF8') || pg_catalog.int4send(1)
        || lifecycle.text_bytes(p_version) || lifecycle.text_bytes(p_project) || lifecycle.input_bytes(p)),'hex');
$$;

CREATE FUNCTION orchestration.view(p orchestration."WorkflowRuns") RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT pg_catalog.jsonb_build_object('runId',p."Id",'projectId',p."ProjectId",'status',p."Status",'failureCode',p."FailureCode",
        'steps',(SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object('ordinal',s."Ordinal",'kind',s."Kind",'status',s."Status",'attempts',s."Attempts") ORDER BY s."Ordinal")
            FROM orchestration."WorkflowSteps" s WHERE s."RunId"=p."Id"),
        'publication',(SELECT pg_catalog.jsonb_build_object('workItemId',g."WorkItemId",'caseId',g."CaseId",'version',g."NextVersion",'contentHash',g."ContentHash")
            FROM lifecycle."GenerationCommands" g WHERE g."RunId"=p."Id" AND g."Status"='Published' AND p."Status"='Succeeded'));
$$;

-- Locks the run, then the attempt; every orchestration write uses this order.
CREATE FUNCTION orchestration.lock_attempt(p_attempt uuid,p_token bytea) RETURNS orchestration."StepAttempts"
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE a orchestration."StepAttempts"%ROWTYPE; rid uuid;
BEGIN
    SELECT "RunId" INTO rid FROM orchestration."StepAttempts" WHERE "Id"=p_attempt;
    IF rid IS NULL THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    PERFORM 1 FROM orchestration."WorkflowRuns" WHERE "Id"=rid FOR UPDATE;
    SELECT * INTO STRICT a FROM orchestration."StepAttempts" WHERE "Id"=p_attempt FOR UPDATE;
    IF p_token IS NULL OR a."TokenHash" IS DISTINCT FROM pg_catalog.sha256(p_token) THEN RAISE EXCEPTION 'forbidden' USING ERRCODE='42501'; END IF;
    IF a."Outcome"<>'Running' OR a."LeaseExpiresAt"<=pg_catalog.clock_timestamp() THEN RAISE EXCEPTION 'lease_lost' USING ERRCODE='P7010'; END IF;
    RETURN a;
END;
$$;

CREATE FUNCTION orchestration.fail_run(p_run uuid,p_ordinal integer,p_failure text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    UPDATE orchestration."WorkflowSteps" SET "Status"='Failed' WHERE "RunId"=p_run AND "Ordinal"=p_ordinal;
    UPDATE orchestration."WorkflowSteps" SET "Status"='Cancelled' WHERE "RunId"=p_run AND "Ordinal">p_ordinal AND "Status"='Pending';
    UPDATE orchestration."WorkflowRuns" SET "Status"='Failed',"FailureCode"=p_failure,"UpdatedAt"=pg_catalog.clock_timestamp() WHERE "Id"=p_run;
END;
$$;

-- Functions below are the only runtime surface. Ownership/grants are explicit operator work.
CREATE FUNCTION orchestration.submit(p_project text,p_issuer text,p_subject text,p_kind text,p_key uuid,p_version text,p_input jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE actor uuid; fp text; r orchestration."WorkflowRuns"%ROWTYPE; replay boolean:=false; now timestamptz:=pg_catalog.clock_timestamp(); rid uuid;
BEGIN
    actor:=lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind);
    IF p_key IS NULL OR p_key='00000000-0000-0000-0000-000000000000'::uuid OR p_version IS DISTINCT FROM 'analysis-architecture-v1'
        OR p_input->>'projectId' IS DISTINCT FROM p_project OR pg_catalog.length(pg_catalog.btrim(p_input->>'task'))=0
        OR pg_catalog.octet_length(lifecycle.input_bytes(p_input))>65536 THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    fp:=orchestration.fingerprint(p_project,p_version,p_input);
    -- Serializes admission per project; never spans execution.
    PERFORM pg_catalog.pg_advisory_xact_lock(6043001,pg_catalog.hashtext(p_project));
    SELECT * INTO r FROM orchestration."WorkflowRuns" WHERE "ProjectId"=p_project AND "RequesterId"=actor AND "Key"=p_key;
    IF FOUND THEN
        IF r."Fingerprint"<>fp THEN RAISE EXCEPTION 'command_conflict' USING ERRCODE='P6009'; END IF;
        replay:=true;
    ELSE
        IF (SELECT pg_catalog.count(*) FROM orchestration."WorkflowRuns" WHERE "ProjectId"=p_project AND "Status" IN ('Queued','Running'))>=20
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

CREATE FUNCTION orchestration.read_run(p_project text,p_issuer text,p_subject text,p_kind text,p_run uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE result jsonb;
BEGIN
    PERFORM lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind,true);
    SELECT orchestration.view(r) INTO result FROM orchestration."WorkflowRuns" r WHERE r."Id"=p_run AND r."ProjectId"=p_project;
    IF result IS NULL THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    RETURN result;
END;
$$;

CREATE FUNCTION orchestration.claim(p_worker text,p_token bytea) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE r orchestration."WorkflowRuns"%ROWTYPE; s orchestration."WorkflowSteps"%ROWTYPE; x record; bound integer;
    now timestamptz:=pg_catalog.clock_timestamp(); aid uuid:=pg_catalog.gen_random_uuid();
BEGIN
    IF p_worker IS NULL OR pg_catalog.length(p_worker) NOT BETWEEN 1 AND 200 OR p_token IS NULL OR pg_catalog.octet_length(p_token)<>32
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    -- Recover expired leases. Locked runs are skipped and recovered by a later claim.
    FOR x IN SELECT a."Id",a."RunId",a."Ordinal" FROM orchestration."StepAttempts" a JOIN orchestration."WorkflowRuns" w ON w."Id"=a."RunId"
        WHERE a."Outcome"='Running' AND a."LeaseExpiresAt"<=now ORDER BY a."LeaseExpiresAt" LIMIT 10 FOR UPDATE OF w, a SKIP LOCKED LOOP
        UPDATE orchestration."StepAttempts" SET "Outcome"='Abandoned',"FinishedAt"=now WHERE "Id"=x."Id";
        SELECT * INTO STRICT s FROM orchestration."WorkflowSteps" WHERE "RunId"=x."RunId" AND "Ordinal"=x."Ordinal" FOR UPDATE;
        -- Same bounds as WorkflowRun.MaxAttempts.
        bound:=CASE WHEN s."Kind"='Publication' THEN 3 ELSE 2 END;
        IF s."Attempts"<bound THEN
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
        'architecture',(SELECT "Output" FROM orchestration."WorkflowSteps" WHERE "RunId"=r."Id" AND "Ordinal"=1));
END;
$$;

CREATE FUNCTION orchestration.renew(p_attempt uuid,p_token bytea) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE a orchestration."StepAttempts"%ROWTYPE;
BEGIN
    a:=orchestration.lock_attempt(p_attempt,p_token);
    UPDATE orchestration."StepAttempts" SET "LeaseExpiresAt"=pg_catalog.clock_timestamp()+interval '60 seconds' WHERE "Id"=a."Id";
END;
$$;

CREATE FUNCTION orchestration.complete(p_attempt uuid,p_token bytea,p_output jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE a orchestration."StepAttempts"%ROWTYPE; now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    a:=orchestration.lock_attempt(p_attempt,p_token);
    IF pg_catalog.jsonb_typeof(p_output) IS DISTINCT FROM 'object' OR NOT EXISTS (SELECT 1 FROM orchestration."WorkflowSteps"
        WHERE "RunId"=a."RunId" AND "Ordinal"=a."Ordinal" AND "Kind" IN ('Analysis','Architecture'))
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    UPDATE orchestration."StepAttempts" SET "Outcome"='Succeeded',"FinishedAt"=now WHERE "Id"=a."Id";
    UPDATE orchestration."WorkflowSteps" SET "Status"='Succeeded',"Output"=p_output WHERE "RunId"=a."RunId" AND "Ordinal"=a."Ordinal";
    UPDATE orchestration."WorkflowRuns" SET "UpdatedAt"=now WHERE "Id"=a."RunId";
END;
$$;

CREATE FUNCTION orchestration.fail(p_attempt uuid,p_token bytea,p_failure text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE a orchestration."StepAttempts"%ROWTYPE;
BEGIN
    a:=orchestration.lock_attempt(p_attempt,p_token);
    IF p_failure IS NULL OR p_failure NOT IN ('invalid_request','refused','incomplete_response','empty_response','invalid_output',
        'generation_failed','generation_timeout','no_requirements_for_architecture','publication_forbidden','publication_integrity')
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    UPDATE orchestration."StepAttempts" SET "Outcome"='Failed',"FailureCode"=p_failure,"FinishedAt"=pg_catalog.clock_timestamp() WHERE "Id"=a."Id";
    PERFORM orchestration.fail_run(a."RunId",a."Ordinal",p_failure);
END;
$$;

-- Creates the M6 work item, publication command, managed case, lineage, artifact and event,
-- and completes the run, in one transaction under the M6 deferred integrity guards.
CREATE FUNCTION orchestration.publish(p_attempt uuid,p_token bytea,p_bytes bytea) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE a orchestration."StepAttempts"%ROWTYPE; r orchestration."WorkflowRuns"%ROWTYPE; p access."Principals"%ROWTYPE;
    actor uuid; e jsonb; hash text; created timestamptz; wid uuid:=pg_catalog.gen_random_uuid(); now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    a:=orchestration.lock_attempt(p_attempt,p_token);
    SELECT * INTO STRICT r FROM orchestration."WorkflowRuns" WHERE "Id"=a."RunId";
    IF NOT EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" WHERE "RunId"=r."Id" AND "Ordinal"=a."Ordinal" AND "Kind"='Publication')
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    -- Reauthorize the original requester under the M6 revocation locks.
    SELECT * INTO STRICT p FROM access."Principals" WHERE "Id"=r."RequesterId";
    actor:=lifecycle.lock_actor(r."ProjectId",p."Issuer",p."Subject",p."Kind");
    IF actor<>r."RequesterId" THEN RAISE EXCEPTION 'forbidden' USING ERRCODE='42501'; END IF;
    e:=pg_catalog.convert_from(p_bytes,'UTF8')::jsonb; hash:=pg_catalog.encode(pg_catalog.sha256(p_bytes),'hex'); created:=(e->>'createdAt')::timestamptz;
    -- The artifact must carry exactly the run's input and checkpointed outputs.
    IF e->>'workflowId' IS DISTINCT FROM r."Id"::text OR e->>'caseId' IS DISTINCT FROM r."CaseId"::text OR (e->>'version')::integer IS DISTINCT FROM 1
        OR (e->>'formatVersion')::integer IS DISTINCT FROM 1 OR e->>'validatorVersion' IS DISTINCT FROM 'milestone3-v1'
        OR e->'request'->>'projectId' IS DISTINCT FROM r."ProjectId" OR lifecycle.input_hash(r."ProjectId",e->'request') IS DISTINCT FROM r."InputHash"
        OR e->'analysis' IS DISTINCT FROM (SELECT "Output" FROM orchestration."WorkflowSteps" WHERE "RunId"=r."Id" AND "Ordinal"=0 AND "Status"='Succeeded')
        OR e->'architecture' IS DISTINCT FROM (SELECT "Output" FROM orchestration."WorkflowSteps" WHERE "RunId"=r."Id" AND "Ordinal"=1 AND "Status"='Succeeded')
        THEN RAISE EXCEPTION 'orchestration_integrity' USING ERRCODE='23514'; END IF;
    INSERT INTO lifecycle."GenerationCommands" ("RunId","WorkItemId","ProjectId","PrincipalId","Kind","Key","Fingerprint","InputHash","TokenHash","AttemptNumber","Deadline","CaseId","NextVersion","ExpectedRevision","Status","HttpStatus")
        VALUES (r."Id",wid,r."ProjectId",actor,'Create',r."Id",lifecycle.fingerprint(r."ProjectId",NULL,NULL,r."WorkflowVersion",r."Input"),r."InputHash",
            pg_catalog.sha256(p_token),1,now+interval '180 seconds',r."CaseId",1,0,'Running',202);
    INSERT INTO lifecycle."WorkItems" VALUES (wid,r."ProjectId",actor,1,1,r."Id",r."Id");
    INSERT INTO review."ReviewCases" ("Id","ProjectId","CurrentVersion","Revision","State") VALUES (r."CaseId",r."ProjectId",1,1,'PendingReview');
    INSERT INTO review."ReviewCaseLineage" VALUES (r."CaseId",wid,r."ProjectId");
    INSERT INTO review."ArtifactVersions" ("CaseId","Version","WorkflowId","Content","ContentHash","CreatedAt") VALUES (r."CaseId",1,r."Id",p_bytes,hash,created);
    INSERT INTO review."PublicationEvents" ("Id","CaseId","Version","ContentHash","CreatedAt") VALUES (pg_catalog.gen_random_uuid(),r."CaseId",1,hash,created);
    UPDATE lifecycle."GenerationCommands" SET "Status"='Published',"HttpStatus"=201,"ContentHash"=hash,"ResultRevision"=1 WHERE "RunId"=r."Id";
    UPDATE lifecycle."WorkItems" SET "ActiveRunId"=NULL,"Revision"="Revision"+1 WHERE "Id"=wid;
    UPDATE orchestration."StepAttempts" SET "Outcome"='Succeeded',"FinishedAt"=now WHERE "Id"=a."Id";
    UPDATE orchestration."WorkflowSteps" SET "Status"='Succeeded' WHERE "RunId"=r."Id" AND "Ordinal"=a."Ordinal";
    UPDATE orchestration."WorkflowRuns" SET "Status"='Succeeded',"WorkItemId"=wid,"UpdatedAt"=now WHERE "Id"=r."Id";
    RETURN pg_catalog.jsonb_build_object('workItemId',wid,'caseId',r."CaseId",'version',1,'contentHash',hash);
END;
$$;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA orchestration FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA orchestration FROM PUBLIC;
