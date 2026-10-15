-- Versioned M8.3 migration. Never edit after release. Adds budgets and pre-call cost reservations.
-- Owner provisioning is separate (orchestration-runtime-grants.sql, tenancy-runtime-grants.sql).

-- Operator-managed, append-only budgets. The latest row effective now for a scope applies;
-- LimitMicros NULL lifts the budget. Month = UTC calendar month; Lifetime = all time.
CREATE TABLE orchestration."Budgets" (
    "Id" uuid PRIMARY KEY,
    "Scope" text NOT NULL CHECK ("Scope" IN ('Organization','Project','Provider')),
    "ScopeId" varchar(210) NOT NULL CHECK (pg_catalog.length(pg_catalog.btrim("ScopeId"))>0),
    "Period" text NOT NULL CHECK ("Period" IN ('Month','Lifetime')),
    "LimitMicros" bigint CHECK ("LimitMicros">=0),
    "EffectiveFrom" timestamptz NOT NULL,
    "Source" varchar(500) NOT NULL CHECK (pg_catalog.length(pg_catalog.btrim("Source"))>0),
    UNIQUE ("Scope","ScopeId","EffectiveFrom")
);
CREATE TRIGGER budgets_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON orchestration."Budgets"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();

-- One worst-case reservation per call, made before the call; append-only. ReservedMicros is NULL
-- only when no budget applied and the model had no price.
CREATE TABLE orchestration."CostReservations" (
    "Id" uuid PRIMARY KEY,
    "AttemptId" uuid NOT NULL REFERENCES orchestration."StepAttempts"("Id"),
    "RunId" uuid NOT NULL REFERENCES orchestration."WorkflowRuns"("Id"),
    "ProjectId" varchar(200) NOT NULL REFERENCES access."Projects"("Id"),
    "OrganizationId" varchar(210) NOT NULL REFERENCES tenancy."Organizations"("Id"),
    "Provider" varchar(100) NOT NULL, "Model" varchar(200) NOT NULL,
    "InputTokens" integer NOT NULL CHECK ("InputTokens">=0), "MaxOutputTokens" integer NOT NULL CHECK ("MaxOutputTokens">0),
    "ReservedMicros" bigint CHECK ("ReservedMicros">=0),
    "CreatedAt" timestamptz NOT NULL
);
CREATE INDEX reservations_org_time ON orchestration."CostReservations"("OrganizationId","CreatedAt");
CREATE INDEX reservations_project_time ON orchestration."CostReservations"("ProjectId","CreatedAt");
CREATE INDEX reservations_provider_time ON orchestration."CostReservations"("Provider","CreatedAt");
CREATE TRIGGER reservations_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON orchestration."CostReservations"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();

ALTER TABLE orchestration."ModelCalls" ADD COLUMN "ReservationId" uuid UNIQUE REFERENCES orchestration."CostReservations"("Id");

CREATE FUNCTION orchestration.current_budget(p_scope text,p_id text,p_at timestamptz) RETURNS orchestration."Budgets"
LANGUAGE sql STABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT * FROM orchestration."Budgets" WHERE "Scope"=p_scope AND "ScopeId"=p_id AND "EffectiveFrom"<=p_at
    ORDER BY "EffectiveFrom" DESC LIMIT 1;
$$;

CREATE FUNCTION orchestration.period_start(p_period text,p_at timestamptz) RETURNS timestamptz
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT CASE WHEN p_period='Month' THEN pg_catalog.date_trunc('month',p_at AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'
        ELSE '-infinity'::timestamptz END;
$$;

-- Conservative spend: a reserved call counts at its priced actual estimate only when both tokens
-- and price are known; otherwise at its reserved worst case. Unreserved (pre-M8.3 or unbudgeted)
-- calls count at their estimate when priced.
CREATE FUNCTION orchestration.spend(p_scope text,p_id text,p_from timestamptz) RETURNS bigint
LANGUAGE sql STABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT COALESCE((SELECT pg_catalog.sum(CASE WHEN c."InputTokens" IS NOT NULL AND c."EstimatedCostMicros" IS NOT NULL
                THEN c."EstimatedCostMicros" ELSE COALESCE(r."ReservedMicros",0) END)
            FROM orchestration."CostReservations" r LEFT JOIN orchestration."ModelCalls" c ON c."ReservationId"=r."Id"
            WHERE r."CreatedAt">=p_from AND CASE p_scope WHEN 'Organization' THEN r."OrganizationId"=p_id
                WHEN 'Project' THEN r."ProjectId"=p_id ELSE r."Provider"=p_id END),0)
        + COALESCE((SELECT pg_catalog.sum(c."EstimatedCostMicros")
            FROM orchestration."ModelCalls" c LEFT JOIN tenancy."ProjectOwnership" o ON o."ProjectId"=c."ProjectId"
            WHERE c."ReservationId" IS NULL AND c."CreatedAt">=p_from AND CASE p_scope WHEN 'Organization' THEN o."OrganizationId"=p_id
                WHEN 'Project' THEN c."ProjectId"=p_id ELSE c."Provider"=p_id END),0)::bigint;
$$;

CREATE FUNCTION orchestration.reserve_cost(p_attempt uuid,p_token bytea,p_provider text,p_model text,p_input integer,p_max_output integer) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE a orchestration."StepAttempts"%ROWTYPE; r orchestration."WorkflowRuns"%ROWTYPE; org text; b orchestration."Budgets"%ROWTYPE;
    price orchestration."ModelPrices"%ROWTYPE; cost bigint; now timestamptz:=pg_catalog.clock_timestamp(); scope record; applies boolean:=false;
    rid uuid:=pg_catalog.gen_random_uuid();
BEGIN
    a:=orchestration.lock_attempt(p_attempt,p_token);
    IF p_provider IS NULL OR NOT (p_provider ~ '^[A-Za-z0-9._:/-]{1,100}$') OR p_model IS NULL OR NOT (p_model ~ '^[A-Za-z0-9._:/-]{1,200}$')
        OR p_input IS NULL OR p_input NOT BETWEEN 0 AND 2000000 OR p_max_output IS NULL OR p_max_output NOT BETWEEN 1 AND 1000000
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    SELECT * INTO STRICT r FROM orchestration."WorkflowRuns" WHERE "Id"=a."RunId";
    SELECT "OrganizationId" INTO STRICT org FROM tenancy."ProjectOwnership" WHERE "ProjectId"=r."ProjectId";
    price:=orchestration.price_for(p_provider,p_model,now);
    IF price."Provider" IS NOT NULL THEN
        cost:=pg_catalog.ceil((p_input::numeric*price."InputMicrosPerMillion"+p_max_output::numeric*price."OutputMicrosPerMillion")/1000000)::bigint;
    END IF;
    -- Serialize reservations per applicable scope (fixed order) so concurrent workers cannot overspend.
    FOR scope IN SELECT * FROM (VALUES (1,'Organization',org),(2,'Project',r."ProjectId"),(3,'Provider',p_provider)) v(n,kind,id) ORDER BY n LOOP
        b:=orchestration.current_budget(scope.kind,scope.id,now);
        IF b."Id" IS NOT NULL AND b."LimitMicros" IS NOT NULL THEN
            applies:=true;
            PERFORM pg_catalog.pg_advisory_xact_lock(6044001,pg_catalog.hashtext(scope.kind || ':' || scope.id));
            IF cost IS NULL THEN RAISE EXCEPTION 'price_unknown' USING ERRCODE='P7021'; END IF;
            IF orchestration.spend(scope.kind,scope.id,orchestration.period_start(b."Period",now))+cost>b."LimitMicros"
                THEN RAISE EXCEPTION 'budget_exceeded' USING ERRCODE='P7020'; END IF;
        END IF;
    END LOOP;
    INSERT INTO orchestration."CostReservations" ("Id","AttemptId","RunId","ProjectId","OrganizationId","Provider","Model","InputTokens","MaxOutputTokens","ReservedMicros","CreatedAt")
        VALUES (rid,a."Id",r."Id",r."ProjectId",org,p_provider,p_model,p_input,p_max_output,cost,now);
    RETURN rid;
END;
$$;

-- Replaces the M8.1 signature: usage may now settle one reservation of the same attempt/provider.
DROP FUNCTION orchestration.record_model_call(uuid,bytea,text,text,integer,integer,integer,text);
CREATE FUNCTION orchestration.record_model_call(p_attempt uuid,p_token bytea,p_provider text,p_model text,
    p_input integer,p_output integer,p_duration integer,p_outcome text,p_reservation uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE a orchestration."StepAttempts"%ROWTYPE; r orchestration."WorkflowRuns"%ROWTYPE; price orchestration."ModelPrices"%ROWTYPE;
    res orchestration."CostReservations"%ROWTYPE; now timestamptz:=pg_catalog.clock_timestamp(); cost bigint;
BEGIN
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
    IF p_reservation IS NOT NULL THEN
        SELECT * INTO res FROM orchestration."CostReservations" WHERE "Id"=p_reservation;
        IF NOT FOUND OR res."AttemptId"<>a."Id" OR (p_provider IS NOT NULL AND p_provider<>res."Provider")
            OR EXISTS (SELECT 1 FROM orchestration."ModelCalls" WHERE "ReservationId"=p_reservation)
            THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    END IF;
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
        "DurationMs","Outcome","EstimatedCostMicros","PriceEffectiveFrom","CreatedAt","ReservationId")
        VALUES (pg_catalog.gen_random_uuid(),a."Id",r."Id",r."ProjectId",p_provider,p_model,p_input,p_output,p_duration,p_outcome,
            cost,CASE WHEN cost IS NULL THEN NULL ELSE price."EffectiveFrom" END,now,p_reservation);
END;
$$;

-- Current organization and project budgets of one organization, for its Owner/Admin.
CREATE FUNCTION tenancy.read_budgets(p_org text,p_issuer text,p_subject text,p_kind text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE now timestamptz:=pg_catalog.clock_timestamp(); result jsonb;
BEGIN
    PERFORM tenancy.lock_admin(p_org,p_issuer,p_subject,p_kind);
    SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object('scope',s.kind,'scopeId',s.id,'period',b."Period",
            'periodStart',CASE WHEN b."Period"='Month' THEN orchestration.period_start(b."Period",now) END,
            'limitMicros',b."LimitMicros",'spentMicros',x.spent,'remainingMicros',GREATEST(b."LimitMicros"-x.spent,0)) ORDER BY s.n,s.id)
        INTO result
        FROM (SELECT 1 AS n,'Organization' AS kind,p_org AS id
              UNION ALL SELECT 2,'Project',o."ProjectId" FROM tenancy."ProjectOwnership" o WHERE o."OrganizationId"=p_org) s
        CROSS JOIN LATERAL (SELECT * FROM orchestration.current_budget(s.kind,s.id,now)) b
        CROSS JOIN LATERAL (SELECT orchestration.spend(s.kind,s.id,orchestration.period_start(b."Period",now)) AS spent) x
        WHERE b."Id" IS NOT NULL AND b."LimitMicros" IS NOT NULL;
    RETURN COALESCE(result,'[]'::jsonb);
END;
$$;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA orchestration FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA tenancy FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA orchestration FROM PUBLIC;
