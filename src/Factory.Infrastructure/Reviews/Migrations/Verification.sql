-- Versioned M9.3 migration. Never edit after release. Owner provisioning is separate
-- (scripts/orchestration-runtime-grants.sql). Workflow tables and functions are unchanged.

-- Verification jobs: generate an application from a registered template and verify it in the
-- sandbox. Identity, template and name never change; only the lease and status move.
CREATE TABLE orchestration."VerificationJobs" (
    "Id" uuid PRIMARY KEY,
    "ProjectId" varchar(200) NOT NULL REFERENCES access."Projects"("Id"),
    "RequesterId" uuid NOT NULL REFERENCES access."Principals"("Id"),
    "Key" uuid NOT NULL,
    "TemplateId" varchar(100) NOT NULL CHECK (pg_catalog.length(pg_catalog.btrim("TemplateId"))>0),
    "TemplateVersion" varchar(50) NOT NULL CHECK (pg_catalog.length(pg_catalog.btrim("TemplateVersion"))>0),
    "ApplicationName" varchar(64) NOT NULL CHECK ("ApplicationName" ~ '^[A-Z][a-z0-9]+([A-Z][a-z0-9]+){0,5}$'),
    "Status" text NOT NULL CHECK ("Status" IN ('Queued','Running','Passed','Failed','TimedOut','SandboxError')),
    "FailureCode" varchar(64) CHECK ("FailureCode" ~ '^[a-z_]{1,64}$'),
    "Attempts" integer NOT NULL CHECK ("Attempts" BETWEEN 0 AND 2),
    "WorkerId" varchar(200), "TokenHash" bytea CHECK (pg_catalog.octet_length("TokenHash")=32), "LeaseExpiresAt" timestamptz,
    "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL,
    UNIQUE ("ProjectId","RequesterId","Key"),
    CHECK (("Status" IN ('Failed','TimedOut','SandboxError')) = ("FailureCode" IS NOT NULL)),
    CHECK (("Status"='Running') = ("TokenHash" IS NOT NULL)),
    CHECK (("Status"='Running') = ("LeaseExpiresAt" IS NOT NULL)),
    CHECK ("Status"<>'Queued' OR "WorkerId" IS NULL)
);
CREATE INDEX queued_verifications ON orchestration."VerificationJobs"("CreatedAt","Id") WHERE "Status"='Queued';
CREATE INDEX running_verifications ON orchestration."VerificationJobs"("LeaseExpiresAt") WHERE "Status"='Running';
CREATE INDEX project_verifications ON orchestration."VerificationJobs"("ProjectId","CreatedAt" DESC);

-- Evidence: the exact report bytes written once by the worker, with their SHA-256.
CREATE TABLE orchestration."VerificationEvidence" (
    "JobId" uuid PRIMARY KEY REFERENCES orchestration."VerificationJobs"("Id"),
    "Content" bytea NOT NULL CHECK (pg_catalog.octet_length("Content") BETWEEN 2 AND 1048576),
    "ContentSha256" varchar(64) NOT NULL,
    "Outcome" text NOT NULL CHECK ("Outcome" IN ('Passed','Failed','TimedOut','SandboxError')),
    "RunnerImage" varchar(300) NOT NULL, "TemplateSha256" varchar(64) NOT NULL, "WorkspaceSha256" varchar(64) NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    CHECK ("ContentSha256"=pg_catalog.encode(pg_catalog.sha256("Content"),'hex')),
    CHECK ("TemplateSha256" ~ '^[0-9a-f]{64}$' AND "WorkspaceSha256" ~ '^[0-9a-f]{64}$')
);

CREATE TRIGGER verification_jobs_no_delete BEFORE DELETE OR TRUNCATE ON orchestration."VerificationJobs"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();
CREATE TRIGGER verification_evidence_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON orchestration."VerificationEvidence"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();

-- Allowed transitions: insert Queued; Queued→Running (claim); Running→Running (renew);
-- Running→Queued (lost lease, retry); Running→terminal. Terminal rows never change.
CREATE FUNCTION orchestration.guard_verification() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    IF TG_OP='INSERT' THEN
        IF NEW."Status"<>'Queued' OR NEW."Attempts"<>0 THEN RAISE EXCEPTION 'invalid_verification_transition' USING ERRCODE='23514'; END IF;
        RETURN NEW;
    END IF;
    IF NEW."Id"<>OLD."Id" OR NEW."ProjectId"<>OLD."ProjectId" OR NEW."RequesterId"<>OLD."RequesterId" OR NEW."Key"<>OLD."Key"
        OR NEW."TemplateId"<>OLD."TemplateId" OR NEW."TemplateVersion"<>OLD."TemplateVersion"
        OR NEW."ApplicationName"<>OLD."ApplicationName" OR NEW."CreatedAt"<>OLD."CreatedAt"
        OR OLD."Status" NOT IN ('Queued','Running')
        OR NOT ((OLD."Status"='Queued' AND NEW."Status"='Running' AND NEW."Attempts"=OLD."Attempts"+1)
             OR (OLD."Status"='Running' AND NEW."Attempts"=OLD."Attempts" AND NEW."Status"<>'Running' AND NEW."TokenHash" IS NULL)
             OR (OLD."Status"='Running' AND NEW."Status"='Running' AND NEW."Attempts"=OLD."Attempts" AND NEW."TokenHash"=OLD."TokenHash"))
        THEN RAISE EXCEPTION 'invalid_verification_transition' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER verification_transition BEFORE INSERT OR UPDATE ON orchestration."VerificationJobs"
    FOR EACH ROW EXECUTE FUNCTION orchestration.guard_verification();

CREATE FUNCTION orchestration.verification_view(j orchestration."VerificationJobs") RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT pg_catalog.jsonb_build_object('jobId',j."Id",'templateId',j."TemplateId",'templateVersion',j."TemplateVersion",
        'applicationName',j."ApplicationName",'status',j."Status",'failureCode',j."FailureCode",'attempts',j."Attempts",
        'createdAt',j."CreatedAt",'updatedAt',j."UpdatedAt");
$$;

-- Locks the job for its lease owner; every worker write uses this.
CREATE FUNCTION orchestration.lock_verification(p_job uuid,p_token bytea) RETURNS orchestration."VerificationJobs"
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE j orchestration."VerificationJobs"%ROWTYPE;
BEGIN
    SELECT * INTO j FROM orchestration."VerificationJobs" WHERE "Id"=p_job FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    IF p_token IS NULL OR j."TokenHash" IS DISTINCT FROM pg_catalog.sha256(p_token) THEN
        IF j."Status"='Running' THEN RAISE EXCEPTION 'forbidden' USING ERRCODE='42501'; END IF;
        RAISE EXCEPTION 'lease_lost' USING ERRCODE='P7010';
    END IF;
    IF j."LeaseExpiresAt"<=pg_catalog.clock_timestamp() THEN RAISE EXCEPTION 'lease_lost' USING ERRCODE='P7010'; END IF;
    RETURN j;
END;
$$;

-- Runtime surface (API): Generators submit; Generators and human Reviewers read.
CREATE FUNCTION orchestration.submit_verification(p_project text,p_issuer text,p_subject text,p_kind text,p_key uuid,
    p_template text,p_version text,p_name text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE actor uuid; j orchestration."VerificationJobs"%ROWTYPE; replay boolean:=false; now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    actor:=lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind);
    IF p_key IS NULL OR p_key='00000000-0000-0000-0000-000000000000'::uuid OR p_template IS NULL OR p_version IS NULL OR p_name IS NULL
        OR pg_catalog.length(pg_catalog.btrim(p_template)) NOT BETWEEN 1 AND 100 OR pg_catalog.length(pg_catalog.btrim(p_version)) NOT BETWEEN 1 AND 50
        OR p_name !~ '^[A-Z][a-z0-9]+([A-Z][a-z0-9]+){0,5}$' THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    PERFORM pg_catalog.pg_advisory_xact_lock(6043009,pg_catalog.hashtext(p_project));
    SELECT * INTO j FROM orchestration."VerificationJobs" WHERE "ProjectId"=p_project AND "RequesterId"=actor AND "Key"=p_key;
    IF FOUND THEN
        IF j."TemplateId"<>p_template OR j."TemplateVersion"<>p_version OR j."ApplicationName"<>p_name
            THEN RAISE EXCEPTION 'command_conflict' USING ERRCODE='P6009'; END IF;
        replay:=true;
    ELSE
        IF (SELECT pg_catalog.count(*) FROM orchestration."VerificationJobs" WHERE "ProjectId"=p_project AND "Status" IN ('Queued','Running'))>=5
            THEN RAISE EXCEPTION 'capacity' USING ERRCODE='P6029'; END IF;
        INSERT INTO orchestration."VerificationJobs" ("Id","ProjectId","RequesterId","Key","TemplateId","TemplateVersion","ApplicationName",
            "Status","Attempts","CreatedAt","UpdatedAt")
            VALUES (pg_catalog.gen_random_uuid(),p_project,actor,p_key,p_template,p_version,p_name,'Queued',0,now,now) RETURNING * INTO j;
    END IF;
    RETURN pg_catalog.jsonb_build_object('jobId',j."Id",'status',j."Status",'replayed',replay);
END;
$$;

CREATE FUNCTION orchestration.list_verifications(p_project text,p_issuer text,p_subject text,p_kind text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    PERFORM lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind,true);
    RETURN COALESCE((SELECT pg_catalog.jsonb_agg(orchestration.verification_view(j) ORDER BY j."CreatedAt" DESC, j."Id")
        FROM (SELECT * FROM orchestration."VerificationJobs" WHERE "ProjectId"=p_project ORDER BY "CreatedAt" DESC, "Id" LIMIT 50) j),'[]'::jsonb);
END;
$$;

CREATE FUNCTION orchestration.read_verification(p_project text,p_issuer text,p_subject text,p_kind text,p_job uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE result jsonb;
BEGIN
    PERFORM lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind,true);
    SELECT pg_catalog.jsonb_build_object('job',orchestration.verification_view(j),'evidenceSha256',e."ContentSha256") INTO result
        FROM orchestration."VerificationJobs" j LEFT JOIN orchestration."VerificationEvidence" e ON e."JobId"=j."Id"
        WHERE j."Id"=p_job AND j."ProjectId"=p_project;
    IF result IS NULL THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    RETURN result;
END;
$$;

-- The exact evidence bytes, for display and independent hash checks.
CREATE FUNCTION orchestration.read_verification_evidence(p_project text,p_issuer text,p_subject text,p_kind text,p_job uuid) RETURNS bytea
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE content bytea;
BEGIN
    PERFORM lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind,true);
    SELECT e."Content" INTO content FROM orchestration."VerificationEvidence" e JOIN orchestration."VerificationJobs" j ON j."Id"=e."JobId"
        WHERE j."Id"=p_job AND j."ProjectId"=p_project;
    IF content IS NULL THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    RETURN content;
END;
$$;

-- Worker surface. Expired leases are retried once, then the job fails.
CREATE FUNCTION orchestration.claim_verification(p_worker text,p_token bytea) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE j orchestration."VerificationJobs"%ROWTYPE; x record; now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    IF p_worker IS NULL OR pg_catalog.length(p_worker) NOT BETWEEN 1 AND 200 OR p_token IS NULL OR pg_catalog.octet_length(p_token)<>32
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    FOR x IN SELECT "Id","Attempts" FROM orchestration."VerificationJobs" WHERE "Status"='Running' AND "LeaseExpiresAt"<=now
        ORDER BY "LeaseExpiresAt" LIMIT 10 FOR UPDATE SKIP LOCKED LOOP
        IF x."Attempts"<2 THEN
            UPDATE orchestration."VerificationJobs" SET "Status"='Queued',"WorkerId"=NULL,"TokenHash"=NULL,"LeaseExpiresAt"=NULL,"UpdatedAt"=now WHERE "Id"=x."Id";
        ELSE
            UPDATE orchestration."VerificationJobs" SET "Status"='Failed',"FailureCode"='attempts_exhausted',"TokenHash"=NULL,"LeaseExpiresAt"=NULL,"UpdatedAt"=now
                WHERE "Id"=x."Id";
        END IF;
    END LOOP;
    SELECT * INTO j FROM orchestration."VerificationJobs" WHERE "Status"='Queued' ORDER BY "CreatedAt","Id" LIMIT 1 FOR UPDATE SKIP LOCKED;
    IF NOT FOUND THEN RETURN NULL; END IF;
    UPDATE orchestration."VerificationJobs" SET "Status"='Running',"Attempts"="Attempts"+1,"WorkerId"=p_worker,"TokenHash"=pg_catalog.sha256(p_token),
        "LeaseExpiresAt"=now+interval '90 seconds',"UpdatedAt"=now WHERE "Id"=j."Id" RETURNING * INTO j;
    RETURN pg_catalog.jsonb_build_object('jobId',j."Id",'projectId',j."ProjectId",'templateId',j."TemplateId",'templateVersion',j."TemplateVersion",
        'applicationName',j."ApplicationName",'attempt',j."Attempts");
END;
$$;

CREATE FUNCTION orchestration.renew_verification(p_job uuid,p_token bytea) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE j orchestration."VerificationJobs"%ROWTYPE;
BEGIN
    j:=orchestration.lock_verification(p_job,p_token);
    UPDATE orchestration."VerificationJobs" SET "LeaseExpiresAt"=pg_catalog.clock_timestamp()+interval '90 seconds' WHERE "Id"=j."Id";
END;
$$;

-- Stores the evidence and ends the job in one transaction. The envelope must describe this job;
-- the status and failure code follow the sandbox report.
CREATE FUNCTION orchestration.complete_verification(p_job uuid,p_token bytea,p_content bytea) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE j orchestration."VerificationJobs"%ROWTYPE; e jsonb; outcome text; failure text; now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    j:=orchestration.lock_verification(p_job,p_token);
    IF p_content IS NULL OR pg_catalog.octet_length(p_content) NOT BETWEEN 2 AND 1048576 THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    BEGIN e:=pg_catalog.convert_from(p_content,'UTF8')::jsonb;
    EXCEPTION WHEN OTHERS THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END;
    outcome:=CASE e->'report'->>'outcome' WHEN 'passed' THEN 'Passed' WHEN 'failed' THEN 'Failed' WHEN 'timedOut' THEN 'TimedOut'
        WHEN 'sandboxError' THEN 'SandboxError' END;
    failure:=e->'report'->>'failureCode';
    IF pg_catalog.jsonb_typeof(e)<>'object' OR (e->>'formatVersion') IS DISTINCT FROM '1' OR (e->>'jobId') IS DISTINCT FROM j."Id"::text
        OR (e->>'projectId') IS DISTINCT FROM j."ProjectId" OR (e->>'templateId') IS DISTINCT FROM j."TemplateId"
        OR (e->>'templateVersion') IS DISTINCT FROM j."TemplateVersion" OR (e->>'applicationName') IS DISTINCT FROM j."ApplicationName"
        OR outcome IS NULL OR (outcome='Passed') <> (failure IS NULL) OR (failure IS NOT NULL AND failure !~ '^[a-z_]{1,64}$')
        OR (e->>'manifestSha256') IS DISTINCT FROM (e->'report'->>'workspaceSha256')
        OR COALESCE(e->>'templateSha256','') !~ '^[0-9a-f]{64}$' OR COALESCE(e->>'manifestSha256','') !~ '^[0-9a-f]{64}$'
        OR pg_catalog.length(COALESCE(e->'report'->>'runnerImage','')) NOT BETWEEN 1 AND 300
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    INSERT INTO orchestration."VerificationEvidence" ("JobId","Content","ContentSha256","Outcome","RunnerImage","TemplateSha256","WorkspaceSha256","CreatedAt")
        VALUES (j."Id",p_content,pg_catalog.encode(pg_catalog.sha256(p_content),'hex'),outcome,e->'report'->>'runnerImage',
            e->>'templateSha256',e->>'manifestSha256',now);
    UPDATE orchestration."VerificationJobs" SET "Status"=outcome,"FailureCode"=failure,"TokenHash"=NULL,"LeaseExpiresAt"=NULL,"UpdatedAt"=now
        WHERE "Id"=j."Id";
END;
$$;

CREATE FUNCTION orchestration.fail_verification(p_job uuid,p_token bytea,p_failure text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE j orchestration."VerificationJobs"%ROWTYPE;
BEGIN
    j:=orchestration.lock_verification(p_job,p_token);
    IF p_failure IS NULL OR p_failure NOT IN ('template_unavailable','invalid_application_name')
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    UPDATE orchestration."VerificationJobs" SET "Status"='Failed',"FailureCode"=p_failure,"TokenHash"=NULL,"LeaseExpiresAt"=NULL,
        "UpdatedAt"=pg_catalog.clock_timestamp() WHERE "Id"=j."Id";
END;
$$;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA orchestration FROM PUBLIC;
