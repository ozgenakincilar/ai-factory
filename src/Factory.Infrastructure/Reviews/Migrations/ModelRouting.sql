-- Versioned M8.5a migration. Never edit after release. Records, per step attempt, the model tier the
-- routing policy chose, the static reason code, the policy version and the provider/model used.
-- Owner provisioning is separate (orchestration-runtime-grants.sql).

-- Append-only evidence; one route per attempt. Provider/model are null only when the worker has no
-- configured identity (development provider without metering).
CREATE TABLE orchestration."StepRoutes" (
    "AttemptId" uuid PRIMARY KEY REFERENCES orchestration."StepAttempts"("Id"),
    "RunId" uuid NOT NULL REFERENCES orchestration."WorkflowRuns"("Id"),
    "Ordinal" integer NOT NULL,
    "Tier" text NOT NULL CHECK ("Tier" IN ('Economy','Standard','Strong')),
    "Reason" text NOT NULL CHECK ("Reason" ~ '^[a-z_]{1,64}$'),
    "PolicyVersion" text NOT NULL CHECK ("PolicyVersion" ~ '^[a-z0-9.-]{1,32}$'),
    "Provider" varchar(100) CHECK ("Provider" ~ '^[A-Za-z0-9._:/-]{1,100}$'),
    "Model" varchar(200) CHECK ("Model" ~ '^[A-Za-z0-9._:/-]{1,200}$'),
    "CreatedAt" timestamptz NOT NULL,
    CHECK (("Provider" IS NULL) = ("Model" IS NULL)),
    FOREIGN KEY ("RunId","Ordinal") REFERENCES orchestration."WorkflowSteps"("RunId","Ordinal")
);
CREATE INDEX step_routes_run ON orchestration."StepRoutes"("RunId");
CREATE TRIGGER step_routes_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON orchestration."StepRoutes"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();

-- Lease-authorized; the attempt must still own its step. A second route for the same attempt is a
-- conflict, never an overwrite.
CREATE FUNCTION orchestration.record_route(p_attempt uuid,p_token bytea,p_tier text,p_reason text,p_policy text,
    p_provider text,p_model text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE a orchestration."StepAttempts"%ROWTYPE;
BEGIN
    a:=orchestration.lock_attempt(p_attempt,p_token);
    IF p_tier IS NULL OR p_tier NOT IN ('Economy','Standard','Strong')
        OR p_reason IS NULL OR NOT (p_reason ~ '^[a-z_]{1,64}$')
        OR p_policy IS NULL OR NOT (p_policy ~ '^[a-z0-9.-]{1,32}$')
        OR (p_provider IS NULL) <> (p_model IS NULL)
        OR (p_provider IS NOT NULL AND NOT (p_provider ~ '^[A-Za-z0-9._:/-]{1,100}$'))
        OR (p_model IS NOT NULL AND NOT (p_model ~ '^[A-Za-z0-9._:/-]{1,200}$'))
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    IF EXISTS (SELECT 1 FROM orchestration."StepRoutes" WHERE "AttemptId"=a."Id")
        THEN RAISE EXCEPTION 'conflict' USING ERRCODE='P6009'; END IF;
    INSERT INTO orchestration."StepRoutes" ("AttemptId","RunId","Ordinal","Tier","Reason","PolicyVersion","Provider","Model","CreatedAt")
        VALUES (a."Id",a."RunId",a."Ordinal",p_tier,p_reason,p_policy,p_provider,p_model,pg_catalog.clock_timestamp());
END;
$$;

-- The run view gains the routes (safe codes and model names only).
CREATE OR REPLACE FUNCTION orchestration.view(p orchestration."WorkflowRuns") RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT pg_catalog.jsonb_build_object('runId',p."Id",'projectId',p."ProjectId",'status',p."Status",'failureCode',p."FailureCode",
        'steps',(SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object('ordinal',s."Ordinal",'kind',s."Kind",'status',s."Status",'attempts',s."Attempts",
                'abandoned',(SELECT pg_catalog.count(*) FROM orchestration."StepAttempts" a WHERE a."RunId"=s."RunId" AND a."Ordinal"=s."Ordinal" AND a."Outcome"='Abandoned'))
                ORDER BY s."Ordinal")
            FROM orchestration."WorkflowSteps" s WHERE s."RunId"=p."Id"),
        'publication',(SELECT pg_catalog.jsonb_build_object('workItemId',g."WorkItemId",'caseId',g."CaseId",'version',g."NextVersion",'contentHash',g."ContentHash")
            FROM lifecycle."GenerationCommands" g WHERE g."RunId"=p."Id" AND g."Status"='Published' AND p."Status"='Succeeded'),
        'decisions',(SELECT pg_catalog.jsonb_agg(orchestration.decision_view(d) ORDER BY d."CreatedAt",d."Id")
            FROM orchestration."StepDecisions" d WHERE d."RunId"=p."Id"),
        'usage',orchestration.run_usage(p."Id"),
        'routes',(SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object('ordinal',r."Ordinal",'attempt',a."Number",'tier',r."Tier",
                'reason',r."Reason",'policyVersion',r."PolicyVersion",'provider',r."Provider",'model',r."Model") ORDER BY r."Ordinal",a."Number")
            FROM orchestration."StepRoutes" r JOIN orchestration."StepAttempts" a ON a."Id"=r."AttemptId" WHERE r."RunId"=p."Id"));
$$;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA orchestration FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA orchestration FROM PUBLIC;
