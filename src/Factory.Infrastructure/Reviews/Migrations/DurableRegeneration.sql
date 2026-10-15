-- Versioned M8.3b migration. Never edit after release. Adds durable regeneration of an existing work
-- item, publishing the next version of its managed review case under the M6 guards. Replaces the
-- retired synchronous M6 regeneration route. Existing rows keep their creation semantics.
ALTER TABLE orchestration."WorkflowRuns" DROP CONSTRAINT "WorkflowRuns_CaseId_key";
ALTER TABLE orchestration."WorkflowRuns" ADD COLUMN "TargetWorkItemId" uuid;
ALTER TABLE orchestration."WorkflowRuns" ADD COLUMN "ExpectedETag" text;
ALTER TABLE orchestration."WorkflowRuns" ADD COLUMN "NextVersion" integer NOT NULL DEFAULT 1 CHECK ("NextVersion">0);
ALTER TABLE orchestration."WorkflowRuns" ADD COLUMN "ExpectedRevision" bigint NOT NULL DEFAULT 0 CHECK ("ExpectedRevision">=0);
ALTER TABLE orchestration."WorkflowRuns" ADD CONSTRAINT run_target CHECK (
    (("TargetWorkItemId" IS NULL) = ("ExpectedETag" IS NULL))
    AND ("TargetWorkItemId" IS NOT NULL OR ("NextVersion"=1 AND "ExpectedRevision"=0))
    AND ("WorkItemId" IS NULL OR "TargetWorkItemId" IS NULL OR "WorkItemId"="TargetWorkItemId"));
ALTER TABLE orchestration."WorkflowRuns" ADD CONSTRAINT run_target_work_item
    FOREIGN KEY ("TargetWorkItemId","ProjectId") REFERENCES lifecycle."WorkItems"("Id","ProjectId");
-- A creation run owns a fresh case; regenerations target an existing one, one active at a time.
CREATE UNIQUE INDEX one_case_per_creation ON orchestration."WorkflowRuns"("CaseId") WHERE "TargetWorkItemId" IS NULL;
CREATE UNIQUE INDEX one_active_regeneration ON orchestration."WorkflowRuns"("TargetWorkItemId")
    WHERE "TargetWorkItemId" IS NOT NULL AND "Status" IN ('Queued','Running','Waiting');

CREATE FUNCTION orchestration.submit_regeneration(p_project text,p_issuer text,p_subject text,p_kind text,p_key uuid,p_work uuid,
    p_tag text,p_version text,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE actor uuid; fp text; r orchestration."WorkflowRuns"%ROWTYPE; replay boolean:=false; now timestamptz:=pg_catalog.clock_timestamp();
    rid uuid; w lifecycle."WorkItems"%ROWTYPE; snap jsonb; cid uuid;
BEGIN
    actor:=lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind);
    IF p_key IS NULL OR p_key='00000000-0000-0000-0000-000000000000'::uuid OR p_work IS NULL OR p_tag IS NULL
        OR p_version IS DISTINCT FROM 'analysis-architecture-v1' OR p_input->>'projectId' IS DISTINCT FROM p_project
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
        INSERT INTO orchestration."WorkflowSteps" ("RunId","Ordinal","Kind","Status","Attempts") VALUES
            (rid,0,'Analysis','Pending',0),(rid,1,'Architecture','Pending',0),(rid,2,'Publication','Pending',0);
    END IF;
    RETURN pg_catalog.jsonb_build_object('runId',r."Id",'status',r."Status",'replayed',replay);
END;
$$;

CREATE OR REPLACE FUNCTION orchestration.fail(p_attempt uuid,p_token bytea,p_failure text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE a orchestration."StepAttempts"%ROWTYPE;
BEGIN
    a:=orchestration.lock_attempt(p_attempt,p_token);
    IF p_failure IS NULL OR p_failure NOT IN ('invalid_request','refused','incomplete_response','empty_response','invalid_output',
        'generation_failed','generation_timeout','no_requirements_for_architecture','publication_forbidden','publication_integrity','publication_stale')
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    UPDATE orchestration."StepAttempts" SET "Outcome"='Failed',"FailureCode"=p_failure,"FinishedAt"=pg_catalog.clock_timestamp() WHERE "Id"=a."Id";
    PERFORM orchestration.fail_run(a."RunId",a."Ordinal",p_failure);
END;
$$;

CREATE OR REPLACE FUNCTION orchestration.claim(p_worker text,p_token bytea) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE r orchestration."WorkflowRuns"%ROWTYPE; s orchestration."WorkflowSteps"%ROWTYPE; x record; bound integer; lost bigint;
    now timestamptz:=pg_catalog.clock_timestamp(); aid uuid:=pg_catalog.gen_random_uuid();
BEGIN
    IF p_worker IS NULL OR pg_catalog.length(p_worker) NOT BETWEEN 1 AND 200 OR p_token IS NULL OR pg_catalog.octet_length(p_token)<>32
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    FOR x IN SELECT a."Id",a."RunId",a."Ordinal" FROM orchestration."StepAttempts" a JOIN orchestration."WorkflowRuns" w ON w."Id"=a."RunId"
        WHERE a."Outcome"='Running' AND a."LeaseExpiresAt"<=now ORDER BY a."LeaseExpiresAt" LIMIT 10 FOR UPDATE OF w, a SKIP LOCKED LOOP
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
            AND NOT EXISTS (SELECT 1 FROM orchestration."StepAttempts" a WHERE a."RunId"=w."Id" AND a."Outcome"='Running')
            AND EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" t WHERE t."RunId"=w."Id" AND t."Status"='Pending')
        ORDER BY w."CreatedAt",w."Id" LIMIT 1 FOR UPDATE OF w SKIP LOCKED;
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT * INTO STRICT s FROM orchestration."WorkflowSteps" WHERE "RunId"=r."Id" AND "Status"='Pending' ORDER BY "Ordinal" LIMIT 1 FOR UPDATE;
    UPDATE orchestration."WorkflowSteps" SET "Status"='Running',"Attempts"="Attempts"+1 WHERE "RunId"=r."Id" AND "Ordinal"=s."Ordinal" RETURNING * INTO s;
    INSERT INTO orchestration."StepAttempts" ("Id","RunId","Ordinal","Number","WorkerId","TokenHash","Outcome","StartedAt","LeaseExpiresAt")
        VALUES (aid,r."Id",s."Ordinal",s."Attempts",p_worker,pg_catalog.sha256(p_token),'Running',now,now+interval '60 seconds');
    UPDATE orchestration."WorkflowRuns" SET "Status"='Running',"UpdatedAt"=now WHERE "Id"=r."Id";
    RETURN pg_catalog.jsonb_build_object('runId',r."Id",'attemptId',aid,'ordinal',s."Ordinal",'kind',s."Kind",'caseId',r."CaseId",
        'nextVersion',r."NextVersion",'request',r."Input",
        'steps',orchestration.view(r)->'steps',
        'analysis',(SELECT "Output" FROM orchestration."WorkflowSteps" WHERE "RunId"=r."Id" AND "Ordinal"=0),
        'architecture',(SELECT "Output" FROM orchestration."WorkflowSteps" WHERE "RunId"=r."Id" AND "Ordinal"=1),
        'decisions',(SELECT pg_catalog.jsonb_agg(orchestration.decision_view(d) ORDER BY d."CreatedAt",d."Id")
            FROM orchestration."StepDecisions" d WHERE d."RunId"=r."Id" AND d."Ordinal"=s."Ordinal" AND d."Status"='Decided'));
END;
$$;

-- Creation runs: new work item and case (unchanged M7.1 semantics). Regeneration runs: next version
-- of the targeted work item's case (or its first case), fenced by the case revision captured at
-- submission; a human decision in between makes publication stale instead of overwriting.
CREATE OR REPLACE FUNCTION orchestration.publish(p_attempt uuid,p_token bytea,p_bytes bytea) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE a orchestration."StepAttempts"%ROWTYPE; r orchestration."WorkflowRuns"%ROWTYPE; p access."Principals"%ROWTYPE;
    actor uuid; e jsonb; hash text; created timestamptz; wid uuid; now timestamptz:=pg_catalog.clock_timestamp();
    w lifecycle."WorkItems"%ROWTYPE; c review."ReviewCases"%ROWTYPE; kind text; attempt bigint;
BEGIN
    a:=orchestration.lock_attempt(p_attempt,p_token);
    SELECT * INTO STRICT r FROM orchestration."WorkflowRuns" WHERE "Id"=a."RunId";
    IF NOT EXISTS (SELECT 1 FROM orchestration."WorkflowSteps" WHERE "RunId"=r."Id" AND "Ordinal"=a."Ordinal" AND "Kind"='Publication')
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    SELECT * INTO STRICT p FROM access."Principals" WHERE "Id"=r."RequesterId";
    actor:=lifecycle.lock_actor(r."ProjectId",p."Issuer",p."Subject",p."Kind");
    IF actor<>r."RequesterId" THEN RAISE EXCEPTION 'forbidden' USING ERRCODE='42501'; END IF;
    e:=pg_catalog.convert_from(p_bytes,'UTF8')::jsonb; hash:=pg_catalog.encode(pg_catalog.sha256(p_bytes),'hex'); created:=(e->>'createdAt')::timestamptz;
    IF e->>'workflowId' IS DISTINCT FROM r."Id"::text OR e->>'caseId' IS DISTINCT FROM r."CaseId"::text OR (e->>'version')::integer IS DISTINCT FROM r."NextVersion"
        OR (e->>'formatVersion')::integer IS DISTINCT FROM 1 OR e->>'validatorVersion' IS DISTINCT FROM 'milestone3-v1'
        OR e->'request'->>'projectId' IS DISTINCT FROM r."ProjectId" OR lifecycle.input_hash(r."ProjectId",e->'request') IS DISTINCT FROM r."InputHash"
        OR e->'analysis' IS DISTINCT FROM (SELECT "Output" FROM orchestration."WorkflowSteps" WHERE "RunId"=r."Id" AND "Ordinal"=0 AND "Status"='Succeeded')
        OR e->'architecture' IS DISTINCT FROM (SELECT "Output" FROM orchestration."WorkflowSteps" WHERE "RunId"=r."Id" AND "Ordinal"=1 AND "Status"='Succeeded')
        THEN RAISE EXCEPTION 'orchestration_integrity' USING ERRCODE='23514'; END IF;
    IF r."TargetWorkItemId" IS NULL THEN
        wid:=pg_catalog.gen_random_uuid(); kind:='Create'; attempt:=1;
    ELSE
        wid:=r."TargetWorkItemId"; kind:='Generate';
        SELECT * INTO STRICT w FROM lifecycle."WorkItems" WHERE "Id"=wid FOR UPDATE;
        IF w."ActiveRunId" IS NOT NULL THEN RAISE EXCEPTION 'stale_revision' USING ERRCODE='P6012'; END IF;
        attempt:=w."AttemptNumber"+1;
        IF r."ExpectedRevision">0 THEN
            SELECT * INTO c FROM review."ReviewCases" WHERE "Id"=r."CaseId" AND "ProjectId"=r."ProjectId" FOR UPDATE;
            IF NOT FOUND OR c."Revision"<>r."ExpectedRevision" OR c."CurrentVersion"+1<>r."NextVersion"
                THEN RAISE EXCEPTION 'stale_revision' USING ERRCODE='P6012'; END IF;
        ELSIF EXISTS (SELECT 1 FROM review."ReviewCaseLineage" WHERE "WorkItemId"=wid) THEN
            RAISE EXCEPTION 'stale_revision' USING ERRCODE='P6012';
        END IF;
    END IF;
    INSERT INTO lifecycle."GenerationCommands" ("RunId","WorkItemId","ProjectId","PrincipalId","Kind","Key","Fingerprint","InputHash","TokenHash","AttemptNumber","Deadline","CaseId","NextVersion","ExpectedRevision","Status","HttpStatus")
        VALUES (r."Id",wid,r."ProjectId",actor,kind,r."Id",
            lifecycle.fingerprint(r."ProjectId",r."TargetWorkItemId",r."ExpectedETag",r."WorkflowVersion",r."Input"),r."InputHash",
            pg_catalog.sha256(p_token),attempt,now+interval '180 seconds',r."CaseId",r."NextVersion",r."ExpectedRevision",'Running',202);
    IF r."TargetWorkItemId" IS NULL THEN
        INSERT INTO lifecycle."WorkItems" VALUES (wid,r."ProjectId",actor,1,1,r."Id",r."Id");
    ELSE
        UPDATE lifecycle."WorkItems" SET "Revision"="Revision"+1,"AttemptNumber"="AttemptNumber"+1,"ActiveRunId"=r."Id","LatestRunId"=r."Id" WHERE "Id"=wid;
    END IF;
    IF r."ExpectedRevision"=0 THEN
        INSERT INTO review."ReviewCases" ("Id","ProjectId","CurrentVersion","Revision","State") VALUES (r."CaseId",r."ProjectId",1,1,'PendingReview');
        INSERT INTO review."ReviewCaseLineage" VALUES (r."CaseId",wid,r."ProjectId");
    ELSE
        UPDATE review."ReviewCases" SET "CurrentVersion"="CurrentVersion"+1,"Revision"="Revision"+1,"State"='PendingReview' WHERE "Id"=r."CaseId";
    END IF;
    INSERT INTO review."ArtifactVersions" ("CaseId","Version","WorkflowId","Content","ContentHash","CreatedAt") VALUES (r."CaseId",r."NextVersion",r."Id",p_bytes,hash,created);
    INSERT INTO review."PublicationEvents" ("Id","CaseId","Version","ContentHash","CreatedAt") VALUES (pg_catalog.gen_random_uuid(),r."CaseId",r."NextVersion",hash,created);
    UPDATE lifecycle."GenerationCommands" SET "Status"='Published',"HttpStatus"=CASE WHEN kind='Create' THEN 201 ELSE 200 END,
        "ContentHash"=hash,"ResultRevision"=r."ExpectedRevision"+1 WHERE "RunId"=r."Id";
    UPDATE lifecycle."WorkItems" SET "ActiveRunId"=NULL,"Revision"="Revision"+1 WHERE "Id"=wid;
    UPDATE orchestration."StepAttempts" SET "Outcome"='Succeeded',"FinishedAt"=now WHERE "Id"=a."Id";
    UPDATE orchestration."WorkflowSteps" SET "Status"='Succeeded' WHERE "RunId"=r."Id" AND "Ordinal"=a."Ordinal";
    UPDATE orchestration."WorkflowRuns" SET "Status"='Succeeded',"WorkItemId"=wid,"UpdatedAt"=now WHERE "Id"=r."Id";
    RETURN pg_catalog.jsonb_build_object('workItemId',wid,'caseId',r."CaseId",'version',r."NextVersion",'contentHash',hash);
END;
$$;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA orchestration FROM PUBLIC;
