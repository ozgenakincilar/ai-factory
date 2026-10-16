-- Versioned M10.3 migration. Never edit after release. Owner provisioning is separate
-- (scripts/orchestration-runtime-grants.sql). Existing tables and functions are unchanged.

-- Development jobs: the Developer Agent implements one approved review artifact version. Identity,
-- case, version, hash, template and name never change; paid work is never re-run automatically.
CREATE TABLE orchestration."DevelopmentJobs" (
    "Id" uuid PRIMARY KEY,
    "ProjectId" varchar(200) NOT NULL REFERENCES access."Projects"("Id"),
    "RequesterId" uuid NOT NULL REFERENCES access."Principals"("Id"),
    "Key" uuid NOT NULL,
    "CaseId" uuid NOT NULL REFERENCES review."ReviewCases"("Id"),
    "ArtifactVersion" integer NOT NULL CHECK ("ArtifactVersion">0),
    "ArtifactSha256" varchar(64) NOT NULL CHECK ("ArtifactSha256" ~ '^[0-9a-f]{64}$'),
    "TemplateId" varchar(100) NOT NULL CHECK (pg_catalog.length(pg_catalog.btrim("TemplateId"))>0),
    "TemplateVersion" varchar(50) NOT NULL CHECK (pg_catalog.length(pg_catalog.btrim("TemplateVersion"))>0),
    "ApplicationName" varchar(64) NOT NULL CHECK ("ApplicationName" ~ '^[A-Z][a-z0-9]+([A-Z][a-z0-9]+){0,5}$'),
    "Status" text NOT NULL CHECK ("Status" IN ('Queued','Running','Verified','VerificationFailed','NotFinished','BudgetExhausted',
        'ModelFailed','SandboxFailed','Failed')),
    "FailureCode" varchar(64) CHECK ("FailureCode" ~ '^[a-z_]{1,64}$'),
    "CostMicros" bigint CHECK ("CostMicros">=0),
    "CapMicros" bigint CHECK ("CapMicros">0),
    "WorkerId" varchar(200), "TokenHash" bytea CHECK (pg_catalog.octet_length("TokenHash")=32), "LeaseExpiresAt" timestamptz,
    "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL,
    UNIQUE ("ProjectId","RequesterId","Key"),
    FOREIGN KEY ("CaseId","ArtifactVersion","ArtifactSha256") REFERENCES review."ArtifactVersions"("CaseId","Version","ContentHash"),
    CHECK (("Status" IN ('Queued','Running','Verified')) = ("FailureCode" IS NULL)),
    CHECK (("Status"='Running') = ("TokenHash" IS NOT NULL)),
    CHECK (("Status"='Running') = ("LeaseExpiresAt" IS NOT NULL)),
    CHECK ("Status"<>'Running' OR ("CapMicros" IS NOT NULL AND "CostMicros" IS NULL)),
    CHECK ("Status"<>'Queued' OR ("WorkerId" IS NULL AND "CapMicros" IS NULL AND "CostMicros" IS NULL))
);
CREATE INDEX queued_developments ON orchestration."DevelopmentJobs"("CreatedAt","Id") WHERE "Status"='Queued';
CREATE INDEX running_developments ON orchestration."DevelopmentJobs"("LeaseExpiresAt") WHERE "Status"='Running';
CREATE INDEX project_developments ON orchestration."DevelopmentJobs"("ProjectId","CreatedAt" DESC);

CREATE TABLE orchestration."DevelopmentEvidence" (
    "JobId" uuid PRIMARY KEY REFERENCES orchestration."DevelopmentJobs"("Id"),
    "Content" bytea NOT NULL CHECK (pg_catalog.octet_length("Content") BETWEEN 2 AND 6291456),
    "ContentSha256" varchar(64) NOT NULL,
    "Status" text NOT NULL,
    "CostMicros" bigint NOT NULL CHECK ("CostMicros">=0),
    "DiffSha256" varchar(64) CHECK ("DiffSha256" ~ '^[0-9a-f]{64}$'),
    "CreatedAt" timestamptz NOT NULL,
    CHECK ("ContentSha256"=pg_catalog.encode(pg_catalog.sha256("Content"),'hex'))
);

CREATE TRIGGER development_jobs_no_delete BEFORE DELETE OR TRUNCATE ON orchestration."DevelopmentJobs"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();
CREATE TRIGGER development_evidence_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON orchestration."DevelopmentEvidence"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();

-- Allowed: insert Queued; Queued→Running (claim) or Queued→terminal (refused at claim);
-- Running→Running (renew); Running→terminal. Terminal rows never change.
CREATE FUNCTION orchestration.guard_development() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    IF TG_OP='INSERT' THEN
        IF NEW."Status"<>'Queued' THEN RAISE EXCEPTION 'invalid_development_transition' USING ERRCODE='23514'; END IF;
        RETURN NEW;
    END IF;
    IF NEW."Id"<>OLD."Id" OR NEW."ProjectId"<>OLD."ProjectId" OR NEW."RequesterId"<>OLD."RequesterId" OR NEW."Key"<>OLD."Key"
        OR NEW."CaseId"<>OLD."CaseId" OR NEW."ArtifactVersion"<>OLD."ArtifactVersion" OR NEW."ArtifactSha256"<>OLD."ArtifactSha256"
        OR NEW."TemplateId"<>OLD."TemplateId" OR NEW."TemplateVersion"<>OLD."TemplateVersion" OR NEW."ApplicationName"<>OLD."ApplicationName"
        OR NEW."CreatedAt"<>OLD."CreatedAt" OR OLD."Status" NOT IN ('Queued','Running')
        OR NOT ((OLD."Status"='Queued' AND NEW."Status"='Running')
             OR (OLD."Status"='Queued' AND NEW."Status" IN ('BudgetExhausted','Failed') AND NEW."TokenHash" IS NULL)
             OR (OLD."Status"='Running' AND NEW."Status"='Running' AND NEW."TokenHash"=OLD."TokenHash" AND NEW."CapMicros"=OLD."CapMicros")
             OR (OLD."Status"='Running' AND NEW."Status" NOT IN ('Queued','Running') AND NEW."TokenHash" IS NULL))
        THEN RAISE EXCEPTION 'invalid_development_transition' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER development_transition BEFORE INSERT OR UPDATE ON orchestration."DevelopmentJobs"
    FOR EACH ROW EXECUTE FUNCTION orchestration.guard_development();

CREATE FUNCTION orchestration.development_view(j orchestration."DevelopmentJobs") RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT pg_catalog.jsonb_build_object('jobId',j."Id",'caseId',j."CaseId",'artifactVersion',j."ArtifactVersion",
        'applicationName',j."ApplicationName",'status',j."Status",'failureCode',j."FailureCode",'costMicros',j."CostMicros",
        'createdAt',j."CreatedAt",'updatedAt',j."UpdatedAt");
$$;

-- True when the case is approved at exactly this version and hash.
CREATE FUNCTION orchestration.approved_artifact(p_project text,p_case uuid,p_version integer,p_hash text) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
    SELECT EXISTS (SELECT 1 FROM review."ReviewCases" c JOIN review."ArtifactVersions" a ON a."CaseId"=c."Id" AND a."Version"=c."CurrentVersion"
        WHERE c."Id"=p_case AND c."ProjectId"=p_project AND c."State"='Approved' AND c."CurrentVersion"=p_version AND a."ContentHash"=p_hash);
$$;

CREATE FUNCTION orchestration.lock_development(p_job uuid,p_token bytea) RETURNS orchestration."DevelopmentJobs"
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE j orchestration."DevelopmentJobs"%ROWTYPE;
BEGIN
    SELECT * INTO j FROM orchestration."DevelopmentJobs" WHERE "Id"=p_job FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    IF p_token IS NULL OR j."TokenHash" IS DISTINCT FROM pg_catalog.sha256(p_token) THEN
        IF j."Status"='Running' THEN RAISE EXCEPTION 'forbidden' USING ERRCODE='42501'; END IF;
        RAISE EXCEPTION 'lease_lost' USING ERRCODE='P7010';
    END IF;
    IF j."LeaseExpiresAt"<=pg_catalog.clock_timestamp() THEN RAISE EXCEPTION 'lease_lost' USING ERRCODE='P7010'; END IF;
    RETURN j;
END;
$$;

-- Runtime surface (API). Only the current approved version can be submitted (stale: P6012).
CREATE FUNCTION orchestration.submit_development(p_project text,p_issuer text,p_subject text,p_kind text,p_key uuid,
    p_case uuid,p_version integer,p_hash text,p_template text,p_template_version text,p_name text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE actor uuid; j orchestration."DevelopmentJobs"%ROWTYPE; replay boolean:=false; now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    actor:=lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind);
    IF p_key IS NULL OR p_key='00000000-0000-0000-0000-000000000000'::uuid OR p_case IS NULL OR p_version IS NULL OR p_version<1
        OR p_hash IS NULL OR p_hash !~ '^[0-9a-f]{64}$' OR p_template IS NULL OR p_template_version IS NULL OR p_name IS NULL
        OR pg_catalog.length(pg_catalog.btrim(p_template)) NOT BETWEEN 1 AND 100 OR pg_catalog.length(pg_catalog.btrim(p_template_version)) NOT BETWEEN 1 AND 50
        OR p_name !~ '^[A-Z][a-z0-9]+([A-Z][a-z0-9]+){0,5}$' THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    PERFORM pg_catalog.pg_advisory_xact_lock(6043010,pg_catalog.hashtext(p_project));
    SELECT * INTO j FROM orchestration."DevelopmentJobs" WHERE "ProjectId"=p_project AND "RequesterId"=actor AND "Key"=p_key;
    IF FOUND THEN
        IF j."CaseId"<>p_case OR j."ArtifactVersion"<>p_version OR j."ArtifactSha256"<>p_hash OR j."TemplateId"<>p_template
            OR j."TemplateVersion"<>p_template_version OR j."ApplicationName"<>p_name THEN RAISE EXCEPTION 'command_conflict' USING ERRCODE='P6009'; END IF;
        replay:=true;
    ELSE
        IF NOT EXISTS (SELECT 1 FROM review."ReviewCases" WHERE "Id"=p_case AND "ProjectId"=p_project) THEN
            RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
        IF NOT orchestration.approved_artifact(p_project,p_case,p_version,p_hash) THEN RAISE EXCEPTION 'stale' USING ERRCODE='P6012'; END IF;
        IF (SELECT pg_catalog.count(*) FROM orchestration."DevelopmentJobs" WHERE "ProjectId"=p_project AND "Status" IN ('Queued','Running'))>=2
            THEN RAISE EXCEPTION 'capacity' USING ERRCODE='P6029'; END IF;
        INSERT INTO orchestration."DevelopmentJobs" ("Id","ProjectId","RequesterId","Key","CaseId","ArtifactVersion","ArtifactSha256","TemplateId",
            "TemplateVersion","ApplicationName","Status","CreatedAt","UpdatedAt")
            VALUES (pg_catalog.gen_random_uuid(),p_project,actor,p_key,p_case,p_version,p_hash,p_template,p_template_version,p_name,'Queued',now,now)
            RETURNING * INTO j;
    END IF;
    RETURN pg_catalog.jsonb_build_object('jobId',j."Id",'status',j."Status",'replayed',replay);
END;
$$;

CREATE FUNCTION orchestration.list_developments(p_project text,p_issuer text,p_subject text,p_kind text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    PERFORM lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind,true);
    RETURN COALESCE((SELECT pg_catalog.jsonb_agg(orchestration.development_view(j) ORDER BY j."CreatedAt" DESC, j."Id")
        FROM (SELECT * FROM orchestration."DevelopmentJobs" WHERE "ProjectId"=p_project ORDER BY "CreatedAt" DESC, "Id" LIMIT 50) j),'[]'::jsonb);
END;
$$;

CREATE FUNCTION orchestration.read_development(p_project text,p_issuer text,p_subject text,p_kind text,p_job uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE result jsonb;
BEGIN
    PERFORM lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind,true);
    SELECT pg_catalog.jsonb_build_object('job',orchestration.development_view(j),'evidenceSha256',e."ContentSha256") INTO result
        FROM orchestration."DevelopmentJobs" j LEFT JOIN orchestration."DevelopmentEvidence" e ON e."JobId"=j."Id"
        WHERE j."Id"=p_job AND j."ProjectId"=p_project;
    IF result IS NULL THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    RETURN result;
END;
$$;

CREATE FUNCTION orchestration.read_development_evidence(p_project text,p_issuer text,p_subject text,p_kind text,p_job uuid) RETURNS bytea
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE content bytea;
BEGIN
    PERFORM lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind,true);
    SELECT e."Content" INTO content FROM orchestration."DevelopmentEvidence" e JOIN orchestration."DevelopmentJobs" j ON j."Id"=e."JobId"
        WHERE j."Id"=p_job AND j."ProjectId"=p_project;
    IF content IS NULL THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    RETURN content;
END;
$$;

-- Worker surface. Expired leases end the job (worker_lost): paid work is never re-executed. The
-- total cap counts recorded spend plus the full cap of every running job.
CREATE FUNCTION orchestration.claim_development(p_worker text,p_token bytea,p_job_cap bigint,p_total_cap bigint) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE j orchestration."DevelopmentJobs"%ROWTYPE; now timestamptz:=pg_catalog.clock_timestamp(); committed bigint;
BEGIN
    IF p_worker IS NULL OR pg_catalog.length(p_worker) NOT BETWEEN 1 AND 200 OR p_token IS NULL OR pg_catalog.octet_length(p_token)<>32
        OR p_job_cap IS NULL OR p_job_cap<=0 OR p_total_cap IS NULL OR p_total_cap<=0 THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    UPDATE orchestration."DevelopmentJobs" SET "Status"='Failed',"FailureCode"='worker_lost',"TokenHash"=NULL,"LeaseExpiresAt"=NULL,"UpdatedAt"=now
        WHERE "Id" IN (SELECT "Id" FROM orchestration."DevelopmentJobs" WHERE "Status"='Running' AND "LeaseExpiresAt"<=now
            ORDER BY "LeaseExpiresAt" LIMIT 10 FOR UPDATE SKIP LOCKED);
    -- Serializes budget admission across workers.
    PERFORM pg_catalog.pg_advisory_xact_lock(6043011,0);
    SELECT * INTO j FROM orchestration."DevelopmentJobs" WHERE "Status"='Queued' ORDER BY "CreatedAt","Id" LIMIT 1 FOR UPDATE SKIP LOCKED;
    IF NOT FOUND THEN RETURN NULL; END IF;
    IF NOT orchestration.approved_artifact(j."ProjectId",j."CaseId",j."ArtifactVersion",j."ArtifactSha256") THEN
        UPDATE orchestration."DevelopmentJobs" SET "Status"='Failed',"FailureCode"='approval_changed',"UpdatedAt"=now WHERE "Id"=j."Id";
        RETURN pg_catalog.jsonb_build_object('refused',j."Id");
    END IF;
    -- Lost workers' spend is unknown, so their full cap counts as spent.
    SELECT COALESCE((SELECT pg_catalog.sum("CostMicros") FROM orchestration."DevelopmentEvidence"),0)
        + COALESCE((SELECT pg_catalog.sum("CapMicros") FROM orchestration."DevelopmentJobs"
            WHERE "Status"='Running' OR ("Status"='Failed' AND "FailureCode"='worker_lost')),0) INTO committed;
    IF committed+p_job_cap>p_total_cap THEN
        UPDATE orchestration."DevelopmentJobs" SET "Status"='BudgetExhausted',"FailureCode"='total_cap_reached',"UpdatedAt"=now WHERE "Id"=j."Id";
        RETURN pg_catalog.jsonb_build_object('refused',j."Id");
    END IF;
    UPDATE orchestration."DevelopmentJobs" SET "Status"='Running',"WorkerId"=p_worker,"TokenHash"=pg_catalog.sha256(p_token),"CapMicros"=p_job_cap,
        "LeaseExpiresAt"=now+interval '90 seconds',"UpdatedAt"=now WHERE "Id"=j."Id" RETURNING * INTO j;
    RETURN pg_catalog.jsonb_build_object('jobId',j."Id",'projectId',j."ProjectId",'caseId',j."CaseId",'artifactVersion',j."ArtifactVersion",
        'artifactSha256',j."ArtifactSha256",'templateId',j."TemplateId",'templateVersion',j."TemplateVersion",'applicationName',j."ApplicationName",
        'artifact',(SELECT pg_catalog.convert_from(a."Content",'UTF8') FROM review."ArtifactVersions" a
            WHERE a."CaseId"=j."CaseId" AND a."Version"=j."ArtifactVersion" AND a."ContentHash"=j."ArtifactSha256"));
END;
$$;

CREATE FUNCTION orchestration.renew_development(p_job uuid,p_token bytea) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE j orchestration."DevelopmentJobs"%ROWTYPE;
BEGIN
    j:=orchestration.lock_development(p_job,p_token);
    UPDATE orchestration."DevelopmentJobs" SET "LeaseExpiresAt"=pg_catalog.clock_timestamp()+interval '90 seconds' WHERE "Id"=j."Id";
END;
$$;

-- Stores the evidence and ends the job in one transaction. The envelope must describe this job, its
-- cost must stay within the job's cap, and the job status follows the envelope.
CREATE FUNCTION orchestration.complete_development(p_job uuid,p_token bytea,p_content bytea) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE j orchestration."DevelopmentJobs"%ROWTYPE; e jsonb; status text; failure text; cost bigint; now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    j:=orchestration.lock_development(p_job,p_token);
    IF p_content IS NULL OR pg_catalog.octet_length(p_content) NOT BETWEEN 2 AND 6291456 THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    BEGIN
        e:=pg_catalog.convert_from(p_content,'UTF8')::jsonb;
        cost:=(e->>'costMicros')::bigint;
    EXCEPTION WHEN OTHERS THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END;
    status:=CASE e->>'status' WHEN 'verified' THEN 'Verified' WHEN 'verificationFailed' THEN 'VerificationFailed' WHEN 'notFinished' THEN 'NotFinished'
        WHEN 'budgetExhausted' THEN 'BudgetExhausted' WHEN 'modelFailed' THEN 'ModelFailed' WHEN 'sandboxFailed' THEN 'SandboxFailed' END;
    failure:=e->>'failureCode';
    IF pg_catalog.jsonb_typeof(e)<>'object' OR (e->>'formatVersion') IS DISTINCT FROM '1' OR (e->>'jobId') IS DISTINCT FROM j."Id"::text
        OR (e->>'projectId') IS DISTINCT FROM j."ProjectId" OR (e->>'caseId') IS DISTINCT FROM j."CaseId"::text
        OR (e->>'artifactVersion') IS DISTINCT FROM j."ArtifactVersion"::text OR (e->>'artifactSha256') IS DISTINCT FROM j."ArtifactSha256"
        OR (e->>'templateId') IS DISTINCT FROM j."TemplateId" OR (e->>'templateVersion') IS DISTINCT FROM j."TemplateVersion"
        OR (e->>'applicationName') IS DISTINCT FROM j."ApplicationName"
        OR status IS NULL OR (status='Verified') <> (failure IS NULL) OR (failure IS NOT NULL AND failure !~ '^[a-z_]{1,64}$')
        OR cost IS NULL OR cost<0 OR cost>j."CapMicros"
        OR (status='Verified' AND (e->>'diffSha256' IS NULL OR e->'verification'->>'outcome' IS DISTINCT FROM 'passed'))
        OR (e->>'diffSha256' IS NOT NULL AND e->>'diffSha256' !~ '^[0-9a-f]{64}$')
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    INSERT INTO orchestration."DevelopmentEvidence" ("JobId","Content","ContentSha256","Status","CostMicros","DiffSha256","CreatedAt")
        VALUES (j."Id",p_content,pg_catalog.encode(pg_catalog.sha256(p_content),'hex'),status,cost,e->>'diffSha256',now);
    UPDATE orchestration."DevelopmentJobs" SET "Status"=status,"FailureCode"=failure,"CostMicros"=cost,"TokenHash"=NULL,"LeaseExpiresAt"=NULL,"UpdatedAt"=now
        WHERE "Id"=j."Id";
END;
$$;

-- Ends a job without evidence before any model call (template or artifact unusable).
CREATE FUNCTION orchestration.fail_development(p_job uuid,p_token bytea,p_failure text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE j orchestration."DevelopmentJobs"%ROWTYPE;
BEGIN
    j:=orchestration.lock_development(p_job,p_token);
    IF p_failure IS NULL OR p_failure NOT IN ('template_unavailable','invalid_application_name','invalid_artifact')
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    UPDATE orchestration."DevelopmentJobs" SET "Status"='Failed',"FailureCode"=p_failure,"CostMicros"=0,"TokenHash"=NULL,"LeaseExpiresAt"=NULL,
        "UpdatedAt"=pg_catalog.clock_timestamp() WHERE "Id"=j."Id";
END;
$$;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA orchestration FROM PUBLIC;
