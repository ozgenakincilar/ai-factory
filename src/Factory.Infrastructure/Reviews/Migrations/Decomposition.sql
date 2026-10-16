-- Versioned M8.5b/c migration. Never edit after release. Adds workflow decomposition-v1: Analysis →
-- Planning → human plan approval → parallel Part steps → merge (Architecture) → Publication.
-- analysis-architecture-v1 runs keep their exact semantics. Ordinals 0–2 keep their kinds in every
-- version, so publication and review artifacts are unchanged. Owner provisioning is separate
-- (scripts/orchestration-runtime-grants.sql).

ALTER TABLE orchestration."WorkflowRuns" DROP CONSTRAINT "WorkflowRuns_WorkflowVersion_check";
ALTER TABLE orchestration."WorkflowRuns" ADD CONSTRAINT "WorkflowRuns_WorkflowVersion_check"
    CHECK ("WorkflowVersion" IN ('analysis-architecture-v1','decomposition-v1'));

ALTER TABLE orchestration."WorkflowSteps" DROP CONSTRAINT "WorkflowSteps_check";
ALTER TABLE orchestration."WorkflowSteps" ADD CONSTRAINT step_kind_ordinal CHECK (
    ("Kind"='Analysis' AND "Ordinal"=0) OR ("Kind"='Architecture' AND "Ordinal"=1) OR ("Kind"='Publication' AND "Ordinal"=2)
    OR ("Kind"='Planning' AND "Ordinal"=3) OR ("Kind"='PlanApproval' AND "Ordinal"=4) OR ("Kind"='Part' AND "Ordinal" BETWEEN 5 AND 16));
-- The approved part definition (copied from the plan at approval), immutable afterwards.
ALTER TABLE orchestration."WorkflowSteps" ADD COLUMN "Part" jsonb;
ALTER TABLE orchestration."WorkflowSteps" ADD CONSTRAINT step_part
    CHECK ((("Part" IS NOT NULL) = ("Kind"='Part')) AND ("Part" IS NULL OR pg_catalog.jsonb_typeof("Part")='object'));

-- Explicit edges for decomposition-v1; analysis-architecture-v1 runs have none (linear order).
CREATE TABLE orchestration."StepDependencies" (
    "RunId" uuid NOT NULL, "Ordinal" integer NOT NULL, "DependsOn" integer NOT NULL,
    PRIMARY KEY ("RunId","Ordinal","DependsOn"),
    FOREIGN KEY ("RunId","Ordinal") REFERENCES orchestration."WorkflowSteps"("RunId","Ordinal"),
    FOREIGN KEY ("RunId","DependsOn") REFERENCES orchestration."WorkflowSteps"("RunId","Ordinal"),
    CHECK ("Ordinal"<>"DependsOn")
);
CREATE TRIGGER step_dependencies_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON orchestration."StepDependencies"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();

-- Parallel parts: one running attempt and one pending decision per step (the per-run bounds of
-- analysis-architecture-v1 remain enforced by the run check).
DROP INDEX orchestration.one_running_attempt_per_run;
CREATE UNIQUE INDEX one_running_attempt_per_step ON orchestration."StepAttempts"("RunId","Ordinal") WHERE "Outcome"='Running';
DROP INDEX orchestration.one_pending_decision_per_run;
CREATE UNIQUE INDEX one_pending_decision_per_step ON orchestration."StepDecisions"("RunId","Ordinal") WHERE "Status"='Pending';

CREATE FUNCTION orchestration.dependencies_succeeded(p_run uuid,p_ordinal integer,p_linear boolean) RETURNS boolean
LANGUAGE sql STABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT CASE WHEN p_linear THEN NOT EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" p
            WHERE p."RunId"=p_run AND p."Ordinal"<p_ordinal AND p."Status"<>'Succeeded')
        ELSE NOT EXISTS (SELECT 1 FROM orchestration."StepDependencies" d JOIN orchestration."WorkflowSteps" p
            ON p."RunId"=d."RunId" AND p."Ordinal"=d."DependsOn" WHERE d."RunId"=p_run AND d."Ordinal"=p_ordinal AND p."Status"<>'Succeeded') END;
$$;

-- Same rules as DecompositionPlanValidator: 1–12 parts keyed P1..Pn, bounded text, known and
-- complete requirement coverage, classifications from fixed sets, dependencies on earlier parts only.
CREATE FUNCTION orchestration.valid_plan(p jsonb,p_analysis jsonb) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog,pg_temp AS $$
DECLARE n integer; i integer; part jsonb; known text[]; covered text[]:='{}'; ids text[]; deps text[];
BEGIN
    IF pg_catalog.jsonb_typeof(p) IS DISTINCT FROM 'object' OR pg_catalog.jsonb_typeof(p->'parts') IS DISTINCT FROM 'array'
        OR pg_catalog.jsonb_typeof(p->'summary') IS DISTINCT FROM 'string' OR pg_catalog.length(pg_catalog.btrim(p->>'summary')) NOT BETWEEN 1 AND 2000
        OR pg_catalog.jsonb_typeof(p_analysis->'functionalRequirements') IS DISTINCT FROM 'array' THEN RETURN false; END IF;
    n:=pg_catalog.jsonb_array_length(p->'parts');
    IF n NOT BETWEEN 1 AND 12 THEN RETURN false; END IF;
    SELECT pg_catalog.array_agg(r->>'id') INTO known FROM pg_catalog.jsonb_array_elements(p_analysis->'functionalRequirements') r;
    IF known IS NULL THEN RETURN false; END IF;
    FOR i IN 0..n-1 LOOP
        part:=p->'parts'->i;
        IF pg_catalog.jsonb_typeof(part) IS DISTINCT FROM 'object' OR part->>'key' IS DISTINCT FROM 'P' || (i+1)
            OR pg_catalog.jsonb_typeof(part->'title') IS DISTINCT FROM 'string' OR pg_catalog.length(pg_catalog.btrim(part->>'title')) NOT BETWEEN 1 AND 200
            OR pg_catalog.jsonb_typeof(part->'scope') IS DISTINCT FROM 'string' OR pg_catalog.length(pg_catalog.btrim(part->>'scope')) NOT BETWEEN 1 AND 2000
            OR part->>'complexity' IS NULL OR part->>'complexity' NOT IN ('low','medium','high')
            OR part->>'importance' IS NULL OR part->>'importance' NOT IN ('normal','high')
            OR pg_catalog.jsonb_typeof(part->'requirementIds') IS DISTINCT FROM 'array' OR pg_catalog.jsonb_array_length(part->'requirementIds')=0
            OR pg_catalog.jsonb_typeof(part->'dependsOn') IS DISTINCT FROM 'array'
            OR EXISTS (SELECT 1 FROM pg_catalog.jsonb_array_elements(part->'requirementIds') x WHERE pg_catalog.jsonb_typeof(x)<>'string')
            OR EXISTS (SELECT 1 FROM pg_catalog.jsonb_array_elements(part->'dependsOn') x WHERE pg_catalog.jsonb_typeof(x)<>'string')
            THEN RETURN false; END IF;
        SELECT pg_catalog.array_agg(x) INTO ids FROM pg_catalog.jsonb_array_elements_text(part->'requirementIds') x;
        SELECT COALESCE(pg_catalog.array_agg(x),'{}') INTO deps FROM pg_catalog.jsonb_array_elements_text(part->'dependsOn') x;
        IF (SELECT pg_catalog.count(DISTINCT x) FROM pg_catalog.unnest(ids) x)<>pg_catalog.cardinality(ids) OR NOT (ids <@ known)
            OR (SELECT pg_catalog.count(DISTINCT x) FROM pg_catalog.unnest(deps) x)<>pg_catalog.cardinality(deps)
            OR EXISTS (SELECT 1 FROM pg_catalog.unnest(deps) x WHERE NOT (x ~ '^P([1-9]|1[0-2])$') OR pg_catalog.substr(x,2)::integer>i)
            THEN RETURN false; END IF;
        covered:=covered || ids;
    END LOOP;
    RETURN known <@ covered;
END;
$$;

CREATE OR REPLACE FUNCTION orchestration.guard_run() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    IF TG_OP='INSERT' THEN
        IF NEW."Status"<>'Queued' OR NEW."CancelledAt" IS NOT NULL THEN RAISE EXCEPTION 'invalid_run_transition' USING ERRCODE='23514'; END IF;
        RETURN NEW;
    END IF;
    -- Waiting runs may still have running parts (decomposition-v1): those can finish or fail it.
    IF OLD."Status" IN ('Succeeded','Failed','Cancelled') OR NOT (
            (OLD."Status"='Queued' AND NEW."Status" IN ('Queued','Running','Failed','Cancelled')) OR
            (OLD."Status"='Running' AND NEW."Status" IN ('Running','Waiting','Succeeded','Failed','Cancelled')) OR
            (OLD."Status"='Waiting' AND NEW."Status" IN ('Waiting','Running','Failed','Cancelled')))
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
        OR NEW."Part" IS DISTINCT FROM OLD."Part"
        OR NOT ((OLD."Status"='Pending' AND NEW."Status"='Running' AND NEW."Attempts"=OLD."Attempts"+1)
             OR (OLD."Status"='Pending' AND NEW."Status"='Cancelled' AND NEW."Attempts"=OLD."Attempts")
             OR (OLD."Status"='Running' AND NEW."Status" IN ('Succeeded','Failed','Pending','Waiting','Cancelled') AND NEW."Attempts"=OLD."Attempts")
             OR (OLD."Status"='Waiting' AND NEW."Status" IN ('Pending','Cancelled') AND NEW."Attempts"=OLD."Attempts"))
        THEN RAISE EXCEPTION 'invalid_step_transition' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END;
$$;

-- Whole-run consistency at COMMIT for both versions.
CREATE OR REPLACE FUNCTION orchestration.check_run() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE rid uuid; r orchestration."WorkflowRuns"%ROWTYPE; linear boolean; n integer; parts integer; running integer; waiting integer;
BEGIN
    IF TG_TABLE_NAME='WorkflowRuns' THEN rid:=NEW."Id"; ELSE rid:=NEW."RunId"; END IF;
    SELECT * INTO STRICT r FROM orchestration."WorkflowRuns" WHERE "Id"=rid;
    linear:=r."WorkflowVersion"='analysis-architecture-v1';
    SELECT pg_catalog.count(*), pg_catalog.count(*) FILTER (WHERE "Kind"='Part'), pg_catalog.count(*) FILTER (WHERE "Status"='Running'),
        pg_catalog.count(*) FILTER (WHERE "Status"='Waiting')
        INTO n, parts, running, waiting FROM orchestration."WorkflowSteps" WHERE "RunId"=rid;
    IF (linear AND (n<>3 OR running+waiting>1 OR EXISTS (SELECT 1 FROM orchestration."StepDependencies" WHERE "RunId"=rid)))
        OR (NOT linear AND (n<>5+parts OR (SELECT pg_catalog.max("Ordinal") FROM orchestration."WorkflowSteps" WHERE "RunId"=rid)<>n-1
            OR running>4
            OR (parts>0) <> EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" WHERE "RunId"=rid AND "Ordinal"=4 AND "Status"='Succeeded')
            OR (SELECT pg_catalog.count(*) FROM orchestration."StepDependencies" WHERE "RunId"=rid
                AND ("Ordinal","DependsOn") IN ((3,0),(4,3),(1,4),(2,1)))<>4
            OR EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" p WHERE p."RunId"=rid AND p."Kind"='Part' AND (
                NOT EXISTS (SELECT 1 FROM orchestration."StepDependencies" WHERE "RunId"=rid AND "Ordinal"=p."Ordinal" AND "DependsOn"=4)
                OR NOT EXISTS (SELECT 1 FROM orchestration."StepDependencies" WHERE "RunId"=rid AND "Ordinal"=1 AND "DependsOn"=p."Ordinal")))
            OR EXISTS (SELECT 1 FROM orchestration."StepDependencies" d WHERE d."RunId"=rid AND NOT (
                (d."Ordinal",d."DependsOn") IN ((3,0),(4,3),(1,4),(2,1)) OR (d."Ordinal"=1 AND d."DependsOn">=5)
                OR (d."Ordinal">=5 AND (d."DependsOn"=4 OR (d."DependsOn">=5 AND d."DependsOn"<d."Ordinal")))))))
        OR EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" s WHERE s."RunId"=rid AND (
            (s."Status"='Running') <> EXISTS (SELECT 1 FROM orchestration."StepAttempts" a
                WHERE a."RunId"=rid AND a."Ordinal"=s."Ordinal" AND a."Outcome"='Running')
            OR (s."Status"='Waiting') <> EXISTS (SELECT 1 FROM orchestration."StepDecisions" d
                WHERE d."RunId"=rid AND d."Ordinal"=s."Ordinal" AND d."Status"='Pending')
            OR s."Attempts"<>(SELECT pg_catalog.count(*) FROM orchestration."StepAttempts" a WHERE a."RunId"=rid AND a."Ordinal"=s."Ordinal")
            OR (s."Status" IN ('Running','Waiting','Succeeded','Failed') AND NOT orchestration.dependencies_succeeded(rid,s."Ordinal",linear))))
        OR EXISTS (SELECT 1 FROM orchestration."StepDecisions" d JOIN orchestration."StepAttempts" a ON a."Id"=d."AttemptId"
            WHERE d."RunId"=rid AND (a."RunId"<>d."RunId" OR a."Ordinal"<>d."Ordinal" OR a."Outcome"<>'Suspended'))
        OR (r."Status"='Queued' AND EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" WHERE "RunId"=rid AND ("Status"<>'Pending' OR "Attempts"<>0)))
        OR (r."Status"='Running' AND EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" WHERE "RunId"=rid AND "Status" IN ('Waiting','Failed','Cancelled')))
        OR (r."Status"='Waiting' AND (waiting<1 OR (linear AND running>0)
            OR EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" WHERE "RunId"=rid AND "Status" IN ('Failed','Cancelled'))))
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
CREATE CONSTRAINT TRIGGER orchestration_dependency AFTER INSERT ON orchestration."StepDependencies" DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION orchestration.check_run();

-- Ends a run on a failed step: running siblings lose their lease, pending decisions end, every other
-- active step is cancelled. For analysis-architecture-v1 this equals the previous behaviour.
CREATE OR REPLACE FUNCTION orchestration.fail_run(p_run uuid,p_ordinal integer,p_failure text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    UPDATE orchestration."WorkflowSteps" SET "Status"='Failed' WHERE "RunId"=p_run AND "Ordinal"=p_ordinal;
    UPDATE orchestration."StepAttempts" SET "Outcome"='Cancelled',"FinishedAt"=now WHERE "RunId"=p_run AND "Ordinal"<>p_ordinal AND "Outcome"='Running';
    UPDATE orchestration."StepDecisions" SET "Status"='Cancelled',"DecidedAt"=now WHERE "RunId"=p_run AND "Status"='Pending';
    UPDATE orchestration."WorkflowSteps" SET "Status"='Cancelled' WHERE "RunId"=p_run AND "Ordinal"<>p_ordinal AND "Status" IN ('Pending','Running','Waiting');
    UPDATE orchestration."WorkflowRuns" SET "Status"='Failed',"FailureCode"=p_failure,"UpdatedAt"=now WHERE "Id"=p_run;
END;
$$;

CREATE FUNCTION orchestration.create_steps(p_run uuid,p_version text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    INSERT INTO orchestration."WorkflowSteps" ("RunId","Ordinal","Kind","Status","Attempts") VALUES
        (p_run,0,'Analysis','Pending',0),(p_run,1,'Architecture','Pending',0),(p_run,2,'Publication','Pending',0);
    IF p_version='decomposition-v1' THEN
        INSERT INTO orchestration."WorkflowSteps" ("RunId","Ordinal","Kind","Status","Attempts") VALUES
            (p_run,3,'Planning','Pending',0),(p_run,4,'PlanApproval','Pending',0);
        INSERT INTO orchestration."StepDependencies" VALUES (p_run,3,0),(p_run,4,3),(p_run,1,4),(p_run,2,1);
    END IF;
END;
$$;

CREATE OR REPLACE FUNCTION orchestration.submit(p_project text,p_issuer text,p_subject text,p_kind text,p_key uuid,p_version text,p_input jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE actor uuid; fp text; r orchestration."WorkflowRuns"%ROWTYPE; replay boolean:=false; now timestamptz:=pg_catalog.clock_timestamp(); rid uuid;
BEGIN
    actor:=lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind);
    IF p_key IS NULL OR p_key='00000000-0000-0000-0000-000000000000'::uuid OR p_version IS NULL
        OR p_version NOT IN ('analysis-architecture-v1','decomposition-v1')
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
        PERFORM orchestration.create_steps(rid,p_version);
    END IF;
    RETURN pg_catalog.jsonb_build_object('runId',r."Id",'status',r."Status",'replayed',replay);
END;
$$;

CREATE OR REPLACE FUNCTION orchestration.submit_regeneration(p_project text,p_issuer text,p_subject text,p_kind text,p_key uuid,p_work uuid,
    p_tag text,p_version text,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE actor uuid; fp text; r orchestration."WorkflowRuns"%ROWTYPE; replay boolean:=false; now timestamptz:=pg_catalog.clock_timestamp();
    rid uuid; w lifecycle."WorkItems"%ROWTYPE; snap jsonb; cid uuid;
BEGIN
    actor:=lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind);
    IF p_key IS NULL OR p_key='00000000-0000-0000-0000-000000000000'::uuid OR p_work IS NULL OR p_tag IS NULL
        OR p_version IS NULL OR p_version NOT IN ('analysis-architecture-v1','decomposition-v1') OR p_input->>'projectId' IS DISTINCT FROM p_project
        OR pg_catalog.length(pg_catalog.btrim(p_input->>'task'))=0 OR pg_catalog.octet_length(lifecycle.input_bytes(p_input))>65536
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    fp:=pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to('UAF-M8-REGENERATE','UTF8') || pg_catalog.int4send(1)
        || lifecycle.text_bytes(pg_catalog.replace(p_work::text,'-','')) || lifecycle.text_bytes(p_tag)
        || pg_catalog.convert_to(orchestration.fingerprint(p_project,p_version,p_input),'UTF8')),'hex');
    PERFORM pg_catalog.pg_advisory_xact_lock(6043001,pg_catalog.hashtext(p_project));
    SELECT * INTO r FROM orchestration."WorkflowRuns" WHERE "ProjectId"=p_project AND "RequesterId"=actor AND "Key"=p_key;
    IF FOUND THEN
        IF r."Fingerprint"<>fp THEN RAISE EXCEPTION 'command_conflict' USING ERRCODE='P6009'; END IF;
        replay:=true;
    ELSE
        SELECT * INTO w FROM lifecycle."WorkItems" WHERE "Id"=p_work AND "ProjectId"=p_project FOR UPDATE;
        IF NOT FOUND THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
        SELECT l."CaseId" INTO cid FROM review."ReviewCaseLineage" l WHERE l."WorkItemId"=p_work;
        IF cid IS NOT NULL THEN PERFORM 1 FROM review."ReviewCases" WHERE "Id"=cid FOR SHARE; END IF;
        snap:=lifecycle.snapshot(p_project,p_work);
        IF p_tag IS DISTINCT FROM lifecycle.etag(snap) THEN RAISE EXCEPTION 'stale_revision' USING ERRCODE='P6012'; END IF;
        IF w."ActiveRunId" IS NOT NULL OR EXISTS (SELECT 1 FROM orchestration."WorkflowRuns" WHERE "TargetWorkItemId"=p_work
            AND "Status" IN ('Queued','Running','Waiting')) THEN RAISE EXCEPTION 'active_command' USING ERRCODE='P6009'; END IF;
        IF (SELECT pg_catalog.count(*) FROM orchestration."WorkflowRuns" WHERE "ProjectId"=p_project AND "Status" IN ('Queued','Running','Waiting'))>=20
            THEN RAISE EXCEPTION 'capacity' USING ERRCODE='P6029'; END IF;
        rid:=pg_catalog.gen_random_uuid();
        INSERT INTO orchestration."WorkflowRuns" ("Id","ProjectId","RequesterId","Key","WorkflowVersion","Fingerprint","Input","InputHash","CaseId",
                "Status","CreatedAt","UpdatedAt","TargetWorkItemId","ExpectedETag","NextVersion","ExpectedRevision")
            VALUES (rid,p_project,actor,p_key,p_version,fp,p_input,lifecycle.input_hash(p_project,p_input),COALESCE(cid,pg_catalog.gen_random_uuid()),
                'Queued',now,now,p_work,p_tag,COALESCE((snap->>'artifactVersion')::integer,0)+1,(snap->>'caseRevision')::bigint) RETURNING * INTO r;
        PERFORM orchestration.create_steps(rid,p_version);
    END IF;
    RETURN pg_catalog.jsonb_build_object('runId',r."Id",'status',r."Status",'replayed',replay);
END;
$$;

-- A run is claimable while below its concurrency bound (1 or 4) and some pending step has all its
-- dependencies succeeded. The run row stays the serialization point for every write.
CREATE OR REPLACE FUNCTION orchestration.claim(p_worker text,p_token bytea) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE r orchestration."WorkflowRuns"%ROWTYPE; s orchestration."WorkflowSteps"%ROWTYPE; x record; bound integer; lost bigint;
    now timestamptz:=pg_catalog.clock_timestamp(); aid uuid:=pg_catalog.gen_random_uuid();
BEGIN
    IF p_worker IS NULL OR pg_catalog.length(p_worker) NOT BETWEEN 1 AND 200 OR p_token IS NULL OR pg_catalog.octet_length(p_token)<>32
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    FOR x IN SELECT a."Id",a."RunId",a."Ordinal" FROM orchestration."StepAttempts" a JOIN orchestration."WorkflowRuns" w ON w."Id"=a."RunId"
        WHERE a."Outcome"='Running' AND a."LeaseExpiresAt"<=now ORDER BY a."LeaseExpiresAt" LIMIT 10 FOR UPDATE OF w, a SKIP LOCKED LOOP
        -- A sibling's failure may already have ended this attempt in the same loop.
        CONTINUE WHEN (SELECT "Outcome" FROM orchestration."StepAttempts" WHERE "Id"=x."Id")<>'Running';
        UPDATE orchestration."StepAttempts" SET "Outcome"='Abandoned',"FinishedAt"=now WHERE "Id"=x."Id";
        SELECT * INTO STRICT s FROM orchestration."WorkflowSteps" WHERE "RunId"=x."RunId" AND "Ordinal"=x."Ordinal" FOR UPDATE;
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
            AND (SELECT pg_catalog.count(*) FROM orchestration."StepAttempts" a WHERE a."RunId"=w."Id" AND a."Outcome"='Running')
                < CASE WHEN w."WorkflowVersion"='decomposition-v1' THEN 4 ELSE 1 END
            AND EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" t WHERE t."RunId"=w."Id" AND t."Status"='Pending'
                AND orchestration.dependencies_succeeded(w."Id",t."Ordinal",w."WorkflowVersion"='analysis-architecture-v1'))
        ORDER BY w."CreatedAt",w."Id" LIMIT 1 FOR UPDATE OF w SKIP LOCKED;
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT * INTO STRICT s FROM orchestration."WorkflowSteps" WHERE "RunId"=r."Id" AND "Status"='Pending'
        AND orchestration.dependencies_succeeded(r."Id","Ordinal",r."WorkflowVersion"='analysis-architecture-v1') ORDER BY "Ordinal" LIMIT 1 FOR UPDATE;
    UPDATE orchestration."WorkflowSteps" SET "Status"='Running',"Attempts"="Attempts"+1 WHERE "RunId"=r."Id" AND "Ordinal"=s."Ordinal" RETURNING * INTO s;
    INSERT INTO orchestration."StepAttempts" ("Id","RunId","Ordinal","Number","WorkerId","TokenHash","Outcome","StartedAt","LeaseExpiresAt")
        VALUES (aid,r."Id",s."Ordinal",s."Attempts",p_worker,pg_catalog.sha256(p_token),'Running',now,now+interval '60 seconds');
    UPDATE orchestration."WorkflowRuns" SET "Status"='Running',"UpdatedAt"=now WHERE "Id"=r."Id" RETURNING * INTO r;
    RETURN pg_catalog.jsonb_build_object('runId',r."Id",'attemptId',aid,'ordinal',s."Ordinal",'kind',s."Kind",'caseId',r."CaseId",
        'nextVersion',r."NextVersion",'request',r."Input",'workflowVersion',r."WorkflowVersion",
        'steps',orchestration.view(r)->'steps',
        'dependencies',(SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object('ordinal',d."Ordinal",'dependsOn',d."DependsOn") ORDER BY d."Ordinal",d."DependsOn")
            FROM orchestration."StepDependencies" d WHERE d."RunId"=r."Id"),
        'analysis',(SELECT "Output" FROM orchestration."WorkflowSteps" WHERE "RunId"=r."Id" AND "Ordinal"=0),
        'architecture',(SELECT "Output" FROM orchestration."WorkflowSteps" WHERE "RunId"=r."Id" AND "Ordinal"=1),
        'plan',(SELECT "Output" FROM orchestration."WorkflowSteps" WHERE "RunId"=r."Id" AND "Ordinal"=3),
        'part',s."Part",
        'partOutputs',(SELECT pg_catalog.jsonb_object_agg(p."Part"->>'key',p."Output") FROM orchestration."WorkflowSteps" p
            WHERE p."RunId"=r."Id" AND p."Kind"='Part' AND p."Status"='Succeeded'),
        'decisions',(SELECT pg_catalog.jsonb_agg(orchestration.decision_view(d) ORDER BY d."CreatedAt",d."Id")
            FROM orchestration."StepDecisions" d WHERE d."RunId"=r."Id" AND d."Ordinal"=s."Ordinal" AND d."Status"='Decided'));
END;
$$;

-- Model checkpoints: Analysis, Architecture (proposal or merge), Planning (validated plan), Part.
-- A completion while the run waits on another step's decision leaves the run Waiting.
CREATE OR REPLACE FUNCTION orchestration.complete(p_attempt uuid,p_token bytea,p_output jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE a orchestration."StepAttempts"%ROWTYPE; s orchestration."WorkflowSteps"%ROWTYPE; now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    a:=orchestration.lock_attempt(p_attempt,p_token);
    SELECT * INTO STRICT s FROM orchestration."WorkflowSteps" WHERE "RunId"=a."RunId" AND "Ordinal"=a."Ordinal";
    IF pg_catalog.jsonb_typeof(p_output) IS DISTINCT FROM 'object' OR s."Kind" NOT IN ('Analysis','Architecture','Planning','Part')
        OR (s."Kind"='Planning' AND NOT orchestration.valid_plan(p_output,
            (SELECT "Output" FROM orchestration."WorkflowSteps" WHERE "RunId"=a."RunId" AND "Ordinal"=0 AND "Status"='Succeeded')))
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    UPDATE orchestration."StepAttempts" SET "Outcome"='Succeeded',"FinishedAt"=now WHERE "Id"=a."Id";
    UPDATE orchestration."WorkflowSteps" SET "Status"='Succeeded',"Output"=p_output WHERE "RunId"=a."RunId" AND "Ordinal"=a."Ordinal";
    UPDATE orchestration."WorkflowRuns" SET "UpdatedAt"=now WHERE "Id"=a."RunId";
END;
$$;

-- Completes plan approval after an "approve" decision on this step and creates the part steps and
-- their edges from the stored plan in the same transaction. The plan is revalidated first.
CREATE FUNCTION orchestration.approve_plan(p_attempt uuid,p_token bytea) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE a orchestration."StepAttempts"%ROWTYPE; plan jsonb; part jsonb; dep text; did uuid; i integer; now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    a:=orchestration.lock_attempt(p_attempt,p_token);
    SELECT d."Id" INTO did FROM orchestration."StepDecisions" d JOIN orchestration."WorkflowSteps" s ON s."RunId"=d."RunId" AND s."Ordinal"=d."Ordinal"
        WHERE d."RunId"=a."RunId" AND d."Ordinal"=a."Ordinal" AND s."Kind"='PlanApproval' AND d."Reason"='plan_approval'
            AND d."Status"='Decided' AND d."Choice"='approve' ORDER BY d."DecidedAt" DESC LIMIT 1;
    IF did IS NULL OR EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" WHERE "RunId"=a."RunId" AND "Kind"='Part')
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    SELECT "Output" INTO STRICT plan FROM orchestration."WorkflowSteps" WHERE "RunId"=a."RunId" AND "Ordinal"=3 AND "Status"='Succeeded';
    IF NOT orchestration.valid_plan(plan,(SELECT "Output" FROM orchestration."WorkflowSteps" WHERE "RunId"=a."RunId" AND "Ordinal"=0 AND "Status"='Succeeded'))
        THEN RAISE EXCEPTION 'orchestration_integrity' USING ERRCODE='23514'; END IF;
    UPDATE orchestration."StepAttempts" SET "Outcome"='Succeeded',"FinishedAt"=now WHERE "Id"=a."Id";
    UPDATE orchestration."WorkflowSteps" SET "Status"='Succeeded',"Output"=pg_catalog.jsonb_build_object('approvedDecisionId',did)
        WHERE "RunId"=a."RunId" AND "Ordinal"=a."Ordinal";
    FOR i IN 0..pg_catalog.jsonb_array_length(plan->'parts')-1 LOOP
        part:=plan->'parts'->i;
        INSERT INTO orchestration."WorkflowSteps" ("RunId","Ordinal","Kind","Status","Attempts","Part") VALUES (a."RunId",5+i,'Part','Pending',0,part);
        INSERT INTO orchestration."StepDependencies" VALUES (a."RunId",5+i,4),(a."RunId",1,5+i);
        FOR dep IN SELECT pg_catalog.jsonb_array_elements_text(part->'dependsOn') LOOP
            INSERT INTO orchestration."StepDependencies" VALUES (a."RunId",5+i,4+pg_catalog.substr(dep,2)::integer);
        END LOOP;
    END LOOP;
    UPDATE orchestration."WorkflowRuns" SET "UpdatedAt"=now WHERE "Id"=a."RunId";
END;
$$;

CREATE OR REPLACE FUNCTION orchestration.fail(p_attempt uuid,p_token bytea,p_failure text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE a orchestration."StepAttempts"%ROWTYPE;
BEGIN
    a:=orchestration.lock_attempt(p_attempt,p_token);
    IF p_failure IS NULL OR p_failure NOT IN ('invalid_request','refused','incomplete_response','empty_response','invalid_output',
        'generation_failed','generation_timeout','no_requirements_for_architecture','publication_forbidden','publication_integrity','publication_stale',
        'integration_failed')
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    UPDATE orchestration."StepAttempts" SET "Outcome"='Failed',"FailureCode"=p_failure,"FinishedAt"=pg_catalog.clock_timestamp() WHERE "Id"=a."Id";
    PERFORM orchestration.fail_run(a."RunId",a."Ordinal",p_failure);
END;
$$;

CREATE OR REPLACE FUNCTION orchestration.request_decision(p_attempt uuid,p_token bytea,p_reason text,p_options text[]) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE a orchestration."StepAttempts"%ROWTYPE; did uuid:=pg_catalog.gen_random_uuid(); now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    a:=orchestration.lock_attempt(p_attempt,p_token);
    IF NOT orchestration.valid_code(p_reason) OR p_options IS NULL OR pg_catalog.cardinality(p_options) NOT BETWEEN 2 AND 8
        OR NOT ('stop'=ANY(p_options)) OR EXISTS (SELECT 1 FROM pg_catalog.unnest(p_options) o WHERE NOT orchestration.valid_code(o))
        OR (SELECT pg_catalog.count(DISTINCT o) FROM pg_catalog.unnest(p_options) o)<>pg_catalog.cardinality(p_options)
        OR NOT EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" WHERE "RunId"=a."RunId" AND "Ordinal"=a."Ordinal"
            AND "Kind" IN ('Analysis','Architecture','Planning','PlanApproval','Part'))
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    UPDATE orchestration."StepAttempts" SET "Outcome"='Suspended',"FinishedAt"=now WHERE "Id"=a."Id";
    UPDATE orchestration."WorkflowSteps" SET "Status"='Waiting' WHERE "RunId"=a."RunId" AND "Ordinal"=a."Ordinal";
    UPDATE orchestration."WorkflowRuns" SET "Status"='Waiting',"UpdatedAt"=now WHERE "Id"=a."RunId";
    INSERT INTO orchestration."StepDecisions" ("Id","RunId","Ordinal","AttemptId","Reason","Options","Status","CreatedAt")
        VALUES (did,a."RunId",a."Ordinal",a."Id",p_reason,p_options,'Pending',now);
    RETURN did;
END;
$$;

-- Organization Owner/Admin decides. A non-stop choice resumes that step; the run runs again only
-- when no other step still waits for a decision.
CREATE OR REPLACE FUNCTION orchestration.decide(p_project text,p_issuer text,p_subject text,p_kind text,p_run uuid,p_decision uuid,p_choice text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE actor uuid; r orchestration."WorkflowRuns"%ROWTYPE; d orchestration."StepDecisions"%ROWTYPE; now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    actor:=tenancy.lock_project_admin(p_project,p_issuer,p_subject,p_kind);
    SELECT * INTO r FROM orchestration."WorkflowRuns" WHERE "Id"=p_run AND "ProjectId"=p_project FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    SELECT * INTO d FROM orchestration."StepDecisions" WHERE "Id"=p_decision AND "RunId"=p_run FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    IF NOT orchestration.valid_code(p_choice) OR NOT (p_choice=ANY(d."Options")) THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    IF d."Status"='Decided' AND d."Choice"=p_choice THEN RETURN orchestration.view(r); END IF;
    IF d."Status"<>'Pending' OR r."Status"<>'Waiting' THEN RAISE EXCEPTION 'decision_settled' USING ERRCODE='P6009'; END IF;
    UPDATE orchestration."StepDecisions" SET "Status"='Decided',"Choice"=p_choice,"DecidedBy"=actor,"DecidedAt"=now WHERE "Id"=d."Id";
    IF p_choice='stop' THEN
        PERFORM orchestration.cancel_run(r."Id",actor);
    ELSE
        UPDATE orchestration."WorkflowSteps" SET "Status"='Pending' WHERE "RunId"=r."Id" AND "Ordinal"=d."Ordinal";
        UPDATE orchestration."WorkflowRuns" SET "Status"=CASE WHEN EXISTS (SELECT 1 FROM orchestration."WorkflowSteps"
                WHERE "RunId"=r."Id" AND "Status"='Waiting') THEN 'Waiting' ELSE 'Running' END,"UpdatedAt"=now WHERE "Id"=r."Id";
    END IF;
    SELECT * INTO STRICT r FROM orchestration."WorkflowRuns" WHERE "Id"=p_run;
    RETURN orchestration.view(r);
END;
$$;

-- The plan for the approval popup: same audience as read_run. It is model output derived from the
-- requester's input, returned only to that project's readers; run views never include it.
CREATE FUNCTION orchestration.read_plan(p_project text,p_issuer text,p_subject text,p_kind text,p_run uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE plan jsonb;
BEGIN
    PERFORM lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind,true);
    SELECT s."Output" INTO plan FROM orchestration."WorkflowRuns" r JOIN orchestration."WorkflowSteps" s ON s."RunId"=r."Id" AND s."Ordinal"=3
        WHERE r."Id"=p_run AND r."ProjectId"=p_project AND r."WorkflowVersion"='decomposition-v1' AND s."Status"='Succeeded';
    IF plan IS NULL THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    RETURN pg_catalog.jsonb_build_object('runId',p_run,'plan',plan);
END;
$$;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA orchestration FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA orchestration FROM PUBLIC;
