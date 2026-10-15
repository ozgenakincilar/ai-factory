-- Versioned M8.1 migration. Never edit after release. Adds per-call model usage and operator
-- prices. Owner provisioning is separate (orchestration-runtime-grants.sql).

-- Operator-maintained USD prices in millionths per million tokens. Append-only: a correction is a
-- new row with a later EffectiveFrom. The Factory never invents prices; the table starts empty.
CREATE TABLE orchestration."ModelPrices" (
    "Provider" varchar(100) NOT NULL, "Model" varchar(200) NOT NULL, "EffectiveFrom" timestamptz NOT NULL,
    "InputMicrosPerMillion" bigint NOT NULL CHECK ("InputMicrosPerMillion">=0),
    "OutputMicrosPerMillion" bigint NOT NULL CHECK ("OutputMicrosPerMillion">=0),
    "Source" varchar(500) NOT NULL CHECK (pg_catalog.length(pg_catalog.btrim("Source"))>0),
    PRIMARY KEY ("Provider","Model","EffectiveFrom")
);
CREATE TRIGGER prices_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON orchestration."ModelPrices"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();

-- One row per provider call observed by a worker; append-only. Cost is fixed at the price
-- effective when the call was recorded.
CREATE TABLE orchestration."ModelCalls" (
    "Id" uuid PRIMARY KEY,
    "AttemptId" uuid NOT NULL REFERENCES orchestration."StepAttempts"("Id"),
    "RunId" uuid NOT NULL REFERENCES orchestration."WorkflowRuns"("Id"),
    "ProjectId" varchar(200) NOT NULL REFERENCES access."Projects"("Id"),
    "Provider" varchar(100), "Model" varchar(200),
    "InputTokens" integer CHECK ("InputTokens">=0), "OutputTokens" integer CHECK ("OutputTokens">=0),
    "DurationMs" integer NOT NULL CHECK ("DurationMs" BETWEEN 0 AND 86400000),
    "Outcome" text NOT NULL CHECK ("Outcome" IN ('Completed','Refused','Incomplete','Empty','Failed','Cancelled')),
    "EstimatedCostMicros" bigint CHECK ("EstimatedCostMicros">=0), "PriceEffectiveFrom" timestamptz,
    "CreatedAt" timestamptz NOT NULL,
    CHECK (("InputTokens" IS NULL) = ("OutputTokens" IS NULL)),
    CHECK ("InputTokens" IS NULL OR ("Provider" IS NOT NULL AND "Model" IS NOT NULL)),
    CHECK (("EstimatedCostMicros" IS NULL) = ("PriceEffectiveFrom" IS NULL)),
    CHECK ("EstimatedCostMicros" IS NULL OR "InputTokens" IS NOT NULL)
);
CREATE INDEX model_calls_run ON orchestration."ModelCalls"("RunId");
CREATE INDEX model_calls_project_time ON orchestration."ModelCalls"("ProjectId","CreatedAt");
CREATE TRIGGER model_calls_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON orchestration."ModelCalls"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();

-- Most specific model match wins (exact name, or a dated snapshot "<name>-..."), then the latest
-- price effective at the given time.
CREATE FUNCTION orchestration.price_for(p_provider text,p_model text,p_at timestamptz) RETURNS orchestration."ModelPrices"
LANGUAGE sql STABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT * FROM orchestration."ModelPrices" p
    WHERE p."Provider"=p_provider AND p."EffectiveFrom"<=p_at
        AND (p."Model"=p_model OR pg_catalog.left(p_model,pg_catalog.length(p."Model")+1)=p."Model" || '-')
    ORDER BY pg_catalog.length(p."Model") DESC, p."EffectiveFrom" DESC LIMIT 1;
$$;

CREATE FUNCTION orchestration.usage_totals(p_calls jsonb) RETURNS jsonb LANGUAGE sql IMMUTABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT pg_catalog.jsonb_build_object(
        'calls',pg_catalog.count(*),
        'inputTokens',COALESCE(pg_catalog.sum((c->>'InputTokens')::bigint),0),
        'outputTokens',COALESCE(pg_catalog.sum((c->>'OutputTokens')::bigint),0),
        'estimatedCostMicros',COALESCE(pg_catalog.sum((c->>'EstimatedCostMicros')::bigint),0),
        'unpricedCalls',pg_catalog.count(*) FILTER (WHERE c->>'InputTokens' IS NOT NULL AND c->>'EstimatedCostMicros' IS NULL),
        'unknownUsageCalls',pg_catalog.count(*) FILTER (WHERE c->>'InputTokens' IS NULL))
    FROM pg_catalog.jsonb_array_elements(COALESCE(p_calls,'[]'::jsonb)) c;
$$;

CREATE FUNCTION orchestration.run_usage(p_run uuid) RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT orchestration.usage_totals((SELECT pg_catalog.jsonb_agg(pg_catalog.to_jsonb(m)) FROM orchestration."ModelCalls" m WHERE m."RunId"=p_run));
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
            FROM orchestration."StepDecisions" d WHERE d."RunId"=p."Id"),
        'usage',orchestration.run_usage(p."Id"));
$$;

CREATE FUNCTION orchestration.record_model_call(p_attempt uuid,p_token bytea,p_provider text,p_model text,
    p_input integer,p_output integer,p_duration integer,p_outcome text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE a orchestration."StepAttempts"%ROWTYPE; r orchestration."WorkflowRuns"%ROWTYPE; price orchestration."ModelPrices"%ROWTYPE;
    now timestamptz:=pg_catalog.clock_timestamp(); cost bigint;
BEGIN
    -- Serializes the per-attempt bound; only the attempt row is locked, never the run.
    SELECT * INTO a FROM orchestration."StepAttempts" WHERE "Id"=p_attempt FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    IF p_token IS NULL OR a."TokenHash" IS DISTINCT FROM pg_catalog.sha256(p_token) THEN RAISE EXCEPTION 'forbidden' USING ERRCODE='42501'; END IF;
    IF a."StartedAt"<now-interval '1 day' OR p_outcome IS NULL
        OR p_outcome NOT IN ('Completed','Refused','Incomplete','Empty','Failed','Cancelled')
        OR p_duration IS NULL OR p_duration NOT BETWEEN 0 AND 86400000
        OR (p_input IS NULL) <> (p_output IS NULL) OR p_input<0 OR p_output<0
        OR (p_provider IS NOT NULL AND NOT (p_provider ~ '^[A-Za-z0-9._:/-]{1,100}$'))
        OR (p_model IS NOT NULL AND NOT (p_model ~ '^[A-Za-z0-9._:/-]{1,200}$'))
        OR (p_input IS NOT NULL AND (p_provider IS NULL OR p_model IS NULL))
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    IF (SELECT pg_catalog.count(*) FROM orchestration."ModelCalls" WHERE "AttemptId"=a."Id")>=10
        THEN RAISE EXCEPTION 'capacity' USING ERRCODE='P6029'; END IF;
    SELECT * INTO STRICT r FROM orchestration."WorkflowRuns" WHERE "Id"=a."RunId";
    IF p_input IS NOT NULL THEN
        price:=orchestration.price_for(p_provider,p_model,now);
        IF price."Provider" IS NOT NULL THEN
            cost:=pg_catalog.ceil((p_input::numeric*price."InputMicrosPerMillion"+p_output::numeric*price."OutputMicrosPerMillion")/1000000)::bigint;
        END IF;
    END IF;
    INSERT INTO orchestration."ModelCalls" ("Id","AttemptId","RunId","ProjectId","Provider","Model","InputTokens","OutputTokens",
        "DurationMs","Outcome","EstimatedCostMicros","PriceEffectiveFrom","CreatedAt")
        VALUES (pg_catalog.gen_random_uuid(),a."Id",r."Id",r."ProjectId",p_provider,p_model,p_input,p_output,p_duration,p_outcome,
            cost,CASE WHEN cost IS NULL THEN NULL ELSE price."EffectiveFrom" END,now);
END;
$$;

CREATE FUNCTION orchestration.read_usage(p_project text,p_issuer text,p_subject text,p_kind text,p_from timestamptz,p_to timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE calls jsonb;
BEGIN
    PERFORM lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind,true);
    IF p_from IS NULL OR p_to IS NULL OR p_from>=p_to OR p_to-p_from>interval '366 days'
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    SELECT pg_catalog.jsonb_agg(pg_catalog.to_jsonb(m)) INTO calls FROM orchestration."ModelCalls" m
        WHERE m."ProjectId"=p_project AND m."CreatedAt">=p_from AND m."CreatedAt"<p_to;
    RETURN pg_catalog.jsonb_build_object('projectId',p_project,'from',p_from,'to',p_to,'totals',orchestration.usage_totals(calls),
        'models',COALESCE((SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object('provider',g.provider,'model',g.model,'totals',orchestration.usage_totals(g.items))
                ORDER BY g.provider NULLS LAST,g.model NULLS LAST)
            FROM (SELECT c->>'Provider' AS provider,c->>'Model' AS model,pg_catalog.jsonb_agg(c) AS items
                FROM pg_catalog.jsonb_array_elements(COALESCE(calls,'[]'::jsonb)) c GROUP BY 1,2) g),'[]'::jsonb));
END;
$$;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA orchestration FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA orchestration FROM PUBLIC;
