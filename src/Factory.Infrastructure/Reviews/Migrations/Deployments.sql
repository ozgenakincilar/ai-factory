-- Versioned M13A.2 migration. Never edit after release. Owner provisioning is separate
-- (scripts/orchestration-runtime-grants.sql). Existing tables and functions are unchanged.

-- Deployment targets, registered by the operator (owner login) after the cloud bootstrap. A target
-- belongs to exactly one project; projects can deploy only to their own targets. The descriptor is
-- the non-secret target description; credentials are worker configuration. Rows never change.
CREATE TABLE orchestration."DeploymentTargets" (
    "Name" varchar(64) PRIMARY KEY CHECK ("Name" ~ '^[a-z][a-z0-9-]{1,62}[a-z0-9]$'),
    "ProjectId" varchar(200) NOT NULL REFERENCES access."Projects"("Id"),
    "Cloud" text NOT NULL CHECK ("Cloud" IN ('azure')),
    "Environment" text NOT NULL CHECK ("Environment" IN ('staging')),
    "Application" varchar(32) NOT NULL CHECK ("Application" ~ '^[a-z][a-z0-9-]{1,30}[a-z0-9]$'),
    "Descriptor" jsonb NOT NULL CHECK (pg_catalog.jsonb_typeof("Descriptor")='object'),
    "CreatedAt" timestamptz NOT NULL
);
CREATE TRIGGER deployment_targets_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON orchestration."DeploymentTargets"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();

-- Deployments of a verified development job's release to a target. The plan, its binding hash and the
-- approval are stored with the job; an approval names exactly one binding. One active deployment
-- per target at a time. Apply rebuilds the release from the same evidence and the binding must match.
CREATE TABLE orchestration."Deployments" (
    "Id" uuid PRIMARY KEY,
    "ProjectId" varchar(200) NOT NULL REFERENCES access."Projects"("Id"),
    "TargetName" varchar(64) NOT NULL REFERENCES orchestration."DeploymentTargets"("Name"),
    "DevelopmentJobId" uuid NOT NULL REFERENCES orchestration."DevelopmentJobs"("Id"),
    "RequesterId" uuid NOT NULL REFERENCES access."Principals"("Id"),
    "Key" uuid NOT NULL,
    "Status" text NOT NULL CHECK ("Status" IN ('Queued','Planning','AwaitingApproval','Approved','Applying','Succeeded','Unhealthy',
        'Failed','Rejected','Expired')),
    "FailureCode" varchar(64) CHECK ("FailureCode" ~ '^[a-z_]{1,64}$'),
    "ModuleId" varchar(100), "ModuleVersion" varchar(50),
    "BindingSha256" varchar(64) CHECK ("BindingSha256" ~ '^[0-9a-f]{64}$'),
    "Binding" jsonb,
    "ReleaseSha256" varchar(64) CHECK ("ReleaseSha256" ~ '^[0-9a-f]{64}$'),
    "PlanFile" bytea CHECK (pg_catalog.octet_length("PlanFile") BETWEEN 1 AND 4194304),
    "PlanSha256" varchar(64),
    "Summary" jsonb,
    "Destructive" boolean,
    "PlannedAt" timestamptz,
    "ApproverId" uuid REFERENCES access."Principals"("Id"),
    "DecidedAt" timestamptz,
    "AllowDestructive" boolean,
    "Url" varchar(300) CHECK ("Url" ~ '^https://[a-z0-9.-]{1,253}$'),
    "Result" jsonb,
    "WorkerId" varchar(200), "TokenHash" bytea CHECK (pg_catalog.octet_length("TokenHash")=32), "LeaseExpiresAt" timestamptz,
    "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL,
    UNIQUE ("ProjectId","RequesterId","Key"),
    CHECK ("PlanSha256" IS NULL OR "PlanSha256"=pg_catalog.encode(pg_catalog.sha256("PlanFile"),'hex')),
    CHECK (("Status" IN ('Queued','Planning')) = ("BindingSha256" IS NULL) OR "Status"='Failed'),
    CHECK (("BindingSha256" IS NULL) = ("PlanFile" IS NULL) AND ("BindingSha256" IS NULL) = ("Summary" IS NULL)
        AND ("BindingSha256" IS NULL) = ("ReleaseSha256" IS NULL) AND ("BindingSha256" IS NULL) = ("Binding" IS NULL)),
    CHECK (("Status" IN ('Approved','Applying','Succeeded','Unhealthy')) <= ("ApproverId" IS NOT NULL AND "AllowDestructive" IS NOT NULL)),
    CHECK ("Status"<>'Rejected' OR "ApproverId" IS NOT NULL),
    CHECK (("Status" IN ('Planning','Applying')) = ("TokenHash" IS NOT NULL)),
    CHECK (("Status" IN ('Planning','Applying')) = ("LeaseExpiresAt" IS NOT NULL)),
    CHECK (("Status" IN ('Failed','Unhealthy','Expired')) = ("FailureCode" IS NOT NULL)),
    CHECK (("Status" IN ('Succeeded','Unhealthy')) <= ("Url" IS NOT NULL AND "Result" IS NOT NULL))
);
CREATE INDEX queued_deployments ON orchestration."Deployments"("UpdatedAt","Id") WHERE "Status" IN ('Queued','Approved');
CREATE INDEX leased_deployments ON orchestration."Deployments"("LeaseExpiresAt") WHERE "Status" IN ('Planning','Applying');
CREATE INDEX project_deployments ON orchestration."Deployments"("ProjectId","CreatedAt" DESC);
CREATE UNIQUE INDEX active_deployment_per_target ON orchestration."Deployments"("TargetName")
    WHERE "Status" IN ('Queued','Planning','AwaitingApproval','Approved','Applying');
CREATE TRIGGER deployments_no_delete BEFORE DELETE OR TRUNCATE ON orchestration."Deployments"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();

-- Allowed: insert Queued; Queued→Planning (claim); Planning→Planning (renew), →AwaitingApproval (plan
-- recorded) or →Failed; AwaitingApproval→Approved/Rejected (human) or →Expired; Approved→Applying
-- (claim) or →Expired; Applying→Applying (renew) or →Succeeded/Unhealthy/Failed. Identity, target, job,
-- requester and a recorded plan never change; terminal rows never change.
CREATE FUNCTION orchestration.guard_deployment() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    IF TG_OP='INSERT' THEN
        IF NEW."Status"<>'Queued' OR NEW."BindingSha256" IS NOT NULL OR NEW."ApproverId" IS NOT NULL OR NEW."TokenHash" IS NOT NULL
            THEN RAISE EXCEPTION 'invalid_deployment_transition' USING ERRCODE='23514'; END IF;
        RETURN NEW;
    END IF;
    IF NEW."Id"<>OLD."Id" OR NEW."ProjectId"<>OLD."ProjectId" OR NEW."TargetName"<>OLD."TargetName" OR NEW."DevelopmentJobId"<>OLD."DevelopmentJobId"
        OR NEW."RequesterId"<>OLD."RequesterId" OR NEW."Key"<>OLD."Key" OR NEW."CreatedAt"<>OLD."CreatedAt"
        OR (OLD."BindingSha256" IS NOT NULL AND (NEW."BindingSha256" IS DISTINCT FROM OLD."BindingSha256" OR NEW."PlanFile" IS DISTINCT FROM OLD."PlanFile"
            OR NEW."Summary" IS DISTINCT FROM OLD."Summary" OR NEW."Binding" IS DISTINCT FROM OLD."Binding"
            OR NEW."ReleaseSha256" IS DISTINCT FROM OLD."ReleaseSha256" OR NEW."Destructive" IS DISTINCT FROM OLD."Destructive"))
        OR (OLD."ApproverId" IS NOT NULL AND (NEW."ApproverId" IS DISTINCT FROM OLD."ApproverId" OR NEW."AllowDestructive" IS DISTINCT FROM OLD."AllowDestructive"))
        OR OLD."Status" NOT IN ('Queued','Planning','AwaitingApproval','Approved','Applying')
        OR NOT ((OLD."Status"='Queued' AND NEW."Status"='Planning')
             OR (OLD."Status"='Planning' AND NEW."Status"='Planning' AND NEW."TokenHash"=OLD."TokenHash")
             OR (OLD."Status"='Planning' AND NEW."Status" IN ('AwaitingApproval','Failed') AND NEW."TokenHash" IS NULL)
             OR (OLD."Status"='AwaitingApproval' AND NEW."Status" IN ('Approved','Rejected','Expired'))
             OR (OLD."Status"='Approved' AND NEW."Status" IN ('Applying','Expired'))
             OR (OLD."Status"='Applying' AND NEW."Status"='Applying' AND NEW."TokenHash"=OLD."TokenHash")
             OR (OLD."Status"='Applying' AND NEW."Status" IN ('Succeeded','Unhealthy','Failed') AND NEW."TokenHash" IS NULL))
        THEN RAISE EXCEPTION 'invalid_deployment_transition' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER deployment_transition BEFORE INSERT OR UPDATE ON orchestration."Deployments"
    FOR EACH ROW EXECUTE FUNCTION orchestration.guard_deployment();

CREATE FUNCTION orchestration.deployment_view(d orchestration."Deployments") RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT pg_catalog.jsonb_build_object('deploymentId',d."Id",'targetName',d."TargetName",'developmentJobId',d."DevelopmentJobId",
        'status',d."Status",'failureCode',d."FailureCode",'bindingSha256',d."BindingSha256",'releaseSha256',d."ReleaseSha256",
        'moduleId',d."ModuleId",'moduleVersion',d."ModuleVersion",'summary',d."Summary",'destructive',d."Destructive",'plannedAt',d."PlannedAt",
        'approved',CASE WHEN d."ApproverId" IS NULL THEN NULL ELSE d."Status" NOT IN ('Rejected') END,'decidedAt',d."DecidedAt",
        'allowDestructive',d."AllowDestructive",'url',d."Url",'result',d."Result",'createdAt',d."CreatedAt",'updatedAt',d."UpdatedAt");
$$;

-- An approval is valid for 24 hours after planning; a stale plan needs a new deployment.
CREATE FUNCTION orchestration.expire_deployments() RETURNS void
LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
    UPDATE orchestration."Deployments" SET "Status"='Expired',"FailureCode"='plan_expired',"UpdatedAt"=pg_catalog.clock_timestamp()
        WHERE "Status" IN ('AwaitingApproval','Approved') AND "PlannedAt"<=pg_catalog.clock_timestamp()-interval '24 hours';
$$;

CREATE FUNCTION orchestration.lock_deployment(p_id uuid,p_token bytea) RETURNS orchestration."Deployments"
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE d orchestration."Deployments"%ROWTYPE;
BEGIN
    SELECT * INTO d FROM orchestration."Deployments" WHERE "Id"=p_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    IF p_token IS NULL OR d."TokenHash" IS DISTINCT FROM pg_catalog.sha256(p_token) THEN
        IF d."Status" IN ('Planning','Applying') THEN RAISE EXCEPTION 'forbidden' USING ERRCODE='42501'; END IF;
        RAISE EXCEPTION 'lease_lost' USING ERRCODE='P7010';
    END IF;
    IF d."LeaseExpiresAt"<=pg_catalog.clock_timestamp() THEN RAISE EXCEPTION 'lease_lost' USING ERRCODE='P7010'; END IF;
    RETURN d;
END;
$$;

-- Runtime surface (API). Generators request deployments of their project's verified jobs to their
-- project's targets; Owners/Admins of the project's organization approve or reject the exact plan.
CREATE FUNCTION orchestration.submit_deployment(p_project text,p_issuer text,p_subject text,p_kind text,p_key uuid,p_target text,p_job uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE actor uuid; d orchestration."Deployments"%ROWTYPE; replay boolean:=false; now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    actor:=lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind);
    IF p_key IS NULL OR p_key='00000000-0000-0000-0000-000000000000'::uuid OR p_target IS NULL OR p_job IS NULL
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    PERFORM orchestration.expire_deployments();
    SELECT * INTO d FROM orchestration."Deployments" WHERE "ProjectId"=p_project AND "RequesterId"=actor AND "Key"=p_key;
    IF FOUND THEN
        IF d."TargetName"<>p_target OR d."DevelopmentJobId"<>p_job THEN RAISE EXCEPTION 'command_conflict' USING ERRCODE='P6009'; END IF;
        replay:=true;
    ELSE
        -- Wrong-target denial: the target must belong to this project.
        IF NOT EXISTS (SELECT 1 FROM orchestration."DeploymentTargets" WHERE "Name"=p_target AND "ProjectId"=p_project)
            OR NOT EXISTS (SELECT 1 FROM orchestration."DevelopmentJobs" WHERE "Id"=p_job AND "ProjectId"=p_project)
            THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
        IF NOT EXISTS (SELECT 1 FROM orchestration."DevelopmentJobs" j JOIN orchestration."DevelopmentEvidence" e ON e."JobId"=j."Id"
            WHERE j."Id"=p_job AND j."Status"='Verified') THEN RAISE EXCEPTION 'stale' USING ERRCODE='P6012'; END IF;
        BEGIN
            INSERT INTO orchestration."Deployments" ("Id","ProjectId","TargetName","DevelopmentJobId","RequesterId","Key","Status","CreatedAt","UpdatedAt")
                VALUES (pg_catalog.gen_random_uuid(),p_project,p_target,p_job,actor,p_key,'Queued',now,now) RETURNING * INTO d;
        EXCEPTION WHEN unique_violation THEN RAISE EXCEPTION 'capacity' USING ERRCODE='P6029';
        END;
    END IF;
    RETURN pg_catalog.jsonb_build_object('deploymentId',d."Id",'status',d."Status",'replayed',replay);
END;
$$;

CREATE FUNCTION orchestration.decide_deployment(p_project text,p_issuer text,p_subject text,p_kind text,p_id uuid,p_approve boolean,
    p_binding text,p_allow_destructive boolean) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE actor uuid; d orchestration."Deployments"%ROWTYPE; now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    actor:=tenancy.lock_project_admin(p_project,p_issuer,p_subject,p_kind);
    IF p_id IS NULL OR p_approve IS NULL OR p_binding IS NULL OR p_binding !~ '^[0-9a-f]{64}$' OR p_allow_destructive IS NULL
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    PERFORM orchestration.expire_deployments();
    SELECT * INTO d FROM orchestration."Deployments" WHERE "Id"=p_id AND "ProjectId"=p_project FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    -- A decision names the exact plan it was made on; another or a stale plan is refused.
    IF d."Status"<>'AwaitingApproval' OR d."BindingSha256"<>p_binding THEN RAISE EXCEPTION 'stale' USING ERRCODE='P6012'; END IF;
    IF p_approve AND d."Destructive" AND NOT p_allow_destructive THEN RAISE EXCEPTION 'destructive_plan' USING ERRCODE='22023'; END IF;
    UPDATE orchestration."Deployments" SET "Status"=CASE WHEN p_approve THEN 'Approved' ELSE 'Rejected' END,"ApproverId"=actor,"DecidedAt"=now,
        "AllowDestructive"=p_approve AND p_allow_destructive,"UpdatedAt"=now WHERE "Id"=d."Id" RETURNING * INTO d;
    RETURN orchestration.deployment_view(d);
END;
$$;

CREATE FUNCTION orchestration.list_deployments(p_project text,p_issuer text,p_subject text,p_kind text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    PERFORM lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind,true);
    -- Reads record expiry (a refused decision rolls back its own expiry update).
    PERFORM orchestration.expire_deployments();
    RETURN pg_catalog.jsonb_build_object(
        'targets',COALESCE((SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object('name',t."Name",'cloud',t."Cloud",'environment',t."Environment",
            'application',t."Application",'location',t."Descriptor"->>'location',
            'resourceGroup',t."Descriptor"->>'resourceGroup') ORDER BY t."Name")
            FROM orchestration."DeploymentTargets" t WHERE t."ProjectId"=p_project),'[]'::jsonb),
        'deployments',COALESCE((SELECT pg_catalog.jsonb_agg(orchestration.deployment_view(d) ORDER BY d."CreatedAt" DESC, d."Id")
            FROM (SELECT * FROM orchestration."Deployments" WHERE "ProjectId"=p_project ORDER BY "CreatedAt" DESC, "Id" LIMIT 50) d),'[]'::jsonb));
END;
$$;

CREATE FUNCTION orchestration.read_deployment(p_project text,p_issuer text,p_subject text,p_kind text,p_id uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE result jsonb;
BEGIN
    PERFORM lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind,true);
    PERFORM orchestration.expire_deployments();
    SELECT orchestration.deployment_view(d)||pg_catalog.jsonb_build_object('binding',d."Binding",'planSha256',d."PlanSha256")
        INTO result FROM orchestration."Deployments" d WHERE d."Id"=p_id AND d."ProjectId"=p_project;
    IF result IS NULL THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    RETURN result;
END;
$$;

-- Worker surface. A lost planning or applying worker ends the deployment (worker_lost): infrastructure
-- may have changed, so nothing is applied again without a new plan and approval.
CREATE FUNCTION orchestration.claim_deployment(p_worker text,p_token bytea) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE d orchestration."Deployments"%ROWTYPE; t orchestration."DeploymentTargets"%ROWTYPE; e orchestration."DevelopmentEvidence"%ROWTYPE;
    now timestamptz:=pg_catalog.clock_timestamp(); phase text;
BEGIN
    IF p_worker IS NULL OR pg_catalog.length(p_worker) NOT BETWEEN 1 AND 200 OR p_token IS NULL OR pg_catalog.octet_length(p_token)<>32
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    UPDATE orchestration."Deployments" SET "Status"='Failed',"FailureCode"='worker_lost',"TokenHash"=NULL,"LeaseExpiresAt"=NULL,"UpdatedAt"=now
        WHERE "Id" IN (SELECT "Id" FROM orchestration."Deployments" WHERE "Status" IN ('Planning','Applying') AND "LeaseExpiresAt"<=now
            ORDER BY "LeaseExpiresAt" LIMIT 10 FOR UPDATE SKIP LOCKED);
    PERFORM orchestration.expire_deployments();
    SELECT * INTO d FROM orchestration."Deployments" WHERE "Status" IN ('Queued','Approved') ORDER BY "UpdatedAt","Id" LIMIT 1 FOR UPDATE SKIP LOCKED;
    IF NOT FOUND THEN RETURN NULL; END IF;
    phase:=CASE d."Status" WHEN 'Queued' THEN 'plan' ELSE 'apply' END;
    UPDATE orchestration."Deployments" SET "Status"=CASE phase WHEN 'plan' THEN 'Planning' ELSE 'Applying' END,"WorkerId"=p_worker,
        "TokenHash"=pg_catalog.sha256(p_token),"LeaseExpiresAt"=now+interval '90 seconds',"UpdatedAt"=now WHERE "Id"=d."Id" RETURNING * INTO d;
    SELECT * INTO t FROM orchestration."DeploymentTargets" WHERE "Name"=d."TargetName";
    SELECT * INTO e FROM orchestration."DevelopmentEvidence" WHERE "JobId"=d."DevelopmentJobId";
    RETURN pg_catalog.jsonb_build_object('deploymentId',d."Id",'projectId',d."ProjectId",'phase',phase,'targetName',t."Name",
        'target',t."Descriptor",'developmentJobId',d."DevelopmentJobId",
        'evidence',pg_catalog.encode(e."Content",'base64'),'evidenceSha256',e."ContentSha256",
        'bindingSha256',d."BindingSha256",'binding',d."Binding",'planFile',pg_catalog.encode(d."PlanFile",'base64'),'summary',d."Summary",
        'moduleId',d."ModuleId",'moduleVersion',d."ModuleVersion",'allowDestructive',d."AllowDestructive",
        'approvedBy',(SELECT p."Issuer"||'|'||p."Subject" FROM access."Principals" p WHERE p."Id"=d."ApproverId"),'approvedAt',d."DecidedAt");
END;
$$;

CREATE FUNCTION orchestration.renew_deployment(p_id uuid,p_token bytea) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE d orchestration."Deployments"%ROWTYPE;
BEGIN
    d:=orchestration.lock_deployment(p_id,p_token);
    UPDATE orchestration."Deployments" SET "LeaseExpiresAt"=pg_catalog.clock_timestamp()+interval '90 seconds' WHERE "Id"=d."Id";
END;
$$;

-- Stores the plan; the binding must name this deployment's job, the plan bytes' hash and the release.
CREATE FUNCTION orchestration.record_deployment_plan(p_id uuid,p_token bytea,p_module text,p_module_version text,p_binding_sha text,
    p_binding jsonb,p_release text,p_plan bytea,p_summary jsonb,p_destructive boolean) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE d orchestration."Deployments"%ROWTYPE; now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    d:=orchestration.lock_deployment(p_id,p_token);
    IF d."Status"<>'Planning' OR p_module IS NULL OR pg_catalog.length(p_module) NOT BETWEEN 1 AND 100 OR p_module_version IS NULL
        OR pg_catalog.length(p_module_version) NOT BETWEEN 1 AND 50 OR p_binding_sha IS NULL OR p_binding_sha !~ '^[0-9a-f]{64}$'
        OR p_binding IS NULL OR pg_catalog.jsonb_typeof(p_binding)<>'object' OR p_release IS NULL OR p_release !~ '^[0-9a-f]{64}$'
        OR p_plan IS NULL OR p_summary IS NULL OR pg_catalog.jsonb_typeof(p_summary)<>'object' OR p_destructive IS NULL
        OR (p_binding->>'developmentJobId') IS DISTINCT FROM d."DevelopmentJobId"::text OR (p_binding->>'releaseSha256') IS DISTINCT FROM p_release
        OR (p_binding->>'planSha256') IS DISTINCT FROM pg_catalog.encode(pg_catalog.sha256(p_plan),'hex')
        OR (p_binding->>'moduleId') IS DISTINCT FROM p_module OR (p_binding->>'moduleVersion') IS DISTINCT FROM p_module_version
        OR (p_summary->>'destroy') IS NULL OR ((p_summary->>'destroy')::integer>0) <> p_destructive
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    UPDATE orchestration."Deployments" SET "Status"='AwaitingApproval',"ModuleId"=p_module,"ModuleVersion"=p_module_version,"BindingSha256"=p_binding_sha,
        "Binding"=p_binding,"ReleaseSha256"=p_release,"PlanFile"=p_plan,"PlanSha256"=pg_catalog.encode(pg_catalog.sha256(p_plan),'hex'),"Summary"=p_summary,
        "Destructive"=p_destructive,"PlannedAt"=now,"TokenHash"=NULL,"LeaseExpiresAt"=NULL,"UpdatedAt"=now WHERE "Id"=d."Id";
END;
$$;

CREATE FUNCTION orchestration.complete_deployment(p_id uuid,p_token bytea,p_healthy boolean,p_url text,p_result jsonb,p_failure text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE d orchestration."Deployments"%ROWTYPE;
BEGIN
    d:=orchestration.lock_deployment(p_id,p_token);
    IF d."Status"<>'Applying' OR p_healthy IS NULL OR p_url IS NULL OR p_url !~ '^https://[a-z0-9.-]{1,253}$'
        OR p_result IS NULL OR pg_catalog.jsonb_typeof(p_result)<>'object' OR (p_result->>'bindingSha256') IS DISTINCT FROM d."BindingSha256"
        OR p_healthy <> (p_failure IS NULL) OR (p_failure IS NOT NULL AND p_failure !~ '^[a-z_]{1,64}$')
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    UPDATE orchestration."Deployments" SET "Status"=CASE WHEN p_healthy THEN 'Succeeded' ELSE 'Unhealthy' END,"FailureCode"=p_failure,
        "Url"=p_url,"Result"=p_result,"TokenHash"=NULL,"LeaseExpiresAt"=NULL,"UpdatedAt"=pg_catalog.clock_timestamp() WHERE "Id"=d."Id";
END;
$$;

CREATE FUNCTION orchestration.fail_deployment(p_id uuid,p_token bytea,p_failure text,p_result jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE d orchestration."Deployments"%ROWTYPE;
BEGIN
    d:=orchestration.lock_deployment(p_id,p_token);
    IF p_failure IS NULL OR p_failure !~ '^[a-z_]{1,64}$' OR (p_result IS NOT NULL AND pg_catalog.jsonb_typeof(p_result)<>'object')
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    UPDATE orchestration."Deployments" SET "Status"='Failed',"FailureCode"=p_failure,"Result"=p_result,"TokenHash"=NULL,"LeaseExpiresAt"=NULL,
        "UpdatedAt"=pg_catalog.clock_timestamp() WHERE "Id"=d."Id";
END;
$$;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA orchestration FROM PUBLIC;
