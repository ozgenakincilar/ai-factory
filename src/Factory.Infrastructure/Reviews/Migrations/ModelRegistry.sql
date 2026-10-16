-- Versioned M8.4a migration. Never edit after release. Adds customer-registered models, project model
-- assignments and the model snapshot on durable runs. Owner provisioning is separate.

-- Credentials are stored only as application-encrypted ciphertext (AES-GCM, key outside the database).
CREATE TABLE tenancy."Models" (
    "Id" uuid PRIMARY KEY,
    "OrganizationId" varchar(210) NOT NULL REFERENCES tenancy."Organizations"("Id"),
    "Name" varchar(100) NOT NULL CHECK (pg_catalog.length(pg_catalog.btrim("Name"))>0),
    "Protocol" text NOT NULL CHECK ("Protocol" IN ('OpenAiCompatible')),
    "BaseUrl" varchar(500) NOT NULL CHECK ("BaseUrl" ~ '^https?://'),
    "Model" varchar(200) NOT NULL CHECK ("Model" ~ '^[A-Za-z0-9._:/-]{1,200}$'),
    "ApiKeyCiphertext" bytea NOT NULL CHECK (pg_catalog.octet_length("ApiKeyCiphertext") BETWEEN 30 AND 8192),
    "Status" text NOT NULL CHECK ("Status" IN ('Unverified','Verified','Failed')),
    "StructuredOutput" boolean, "VerifiedAt" timestamptz,
    "CreatedBy" uuid NOT NULL REFERENCES access."Principals"("Id"), "CreatedAt" timestamptz NOT NULL,
    UNIQUE ("OrganizationId","Name"),
    UNIQUE ("Id","OrganizationId"),
    CHECK (("Status"='Unverified') = ("VerifiedAt" IS NULL)),
    CHECK ("Status"='Verified' OR "StructuredOutput" IS NULL)
);
CREATE TRIGGER models_no_delete BEFORE DELETE OR TRUNCATE ON tenancy."Models"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();
CREATE FUNCTION tenancy.guard_model() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    IF (pg_catalog.to_jsonb(NEW)-ARRAY['Status','StructuredOutput','VerifiedAt']) IS DISTINCT FROM
       (pg_catalog.to_jsonb(OLD)-ARRAY['Status','StructuredOutput','VerifiedAt'])
        THEN RAISE EXCEPTION 'immutable_model' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER model_update BEFORE UPDATE ON tenancy."Models" FOR EACH ROW EXECUTE FUNCTION tenancy.guard_model();

-- Append-only assignment history; the latest row for a project applies. NULL model = Factory default.
CREATE TABLE tenancy."ProjectModels" (
    "Id" uuid PRIMARY KEY,
    "ProjectId" varchar(200) NOT NULL REFERENCES tenancy."ProjectOwnership"("ProjectId"),
    "ModelId" uuid REFERENCES tenancy."Models"("Id"),
    "AcceptedCapabilityRisk" boolean NOT NULL,
    "AssignedBy" uuid NOT NULL REFERENCES access."Principals"("Id"), "AssignedAt" timestamptz NOT NULL
);
CREATE INDEX project_models_latest ON tenancy."ProjectModels"("ProjectId","AssignedAt" DESC);
CREATE TRIGGER project_models_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON tenancy."ProjectModels"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();
CREATE FUNCTION tenancy.guard_project_model() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    -- A project may use only its own organization's models.
    IF NEW."ModelId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tenancy."Models" m JOIN tenancy."ProjectOwnership" o ON o."OrganizationId"=m."OrganizationId"
        WHERE m."Id"=NEW."ModelId" AND o."ProjectId"=NEW."ProjectId") THEN RAISE EXCEPTION 'cross_tenant_model' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER project_model_insert BEFORE INSERT ON tenancy."ProjectModels" FOR EACH ROW EXECUTE FUNCTION tenancy.guard_project_model();

CREATE FUNCTION tenancy.current_model(p_project text) RETURNS uuid LANGUAGE sql STABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT "ModelId" FROM tenancy."ProjectModels" WHERE "ProjectId"=p_project ORDER BY "AssignedAt" DESC, "Id" DESC LIMIT 1;
$$;

-- Each run snapshots its project's model at submission; later assignments never change a run.
ALTER TABLE orchestration."WorkflowRuns" ADD COLUMN "ModelId" uuid REFERENCES tenancy."Models"("Id");
CREATE FUNCTION orchestration.default_run_model() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    NEW."ModelId":=tenancy.current_model(NEW."ProjectId");
    RETURN NEW;
END;
$$;
CREATE TRIGGER run_model_default BEFORE INSERT ON orchestration."WorkflowRuns" FOR EACH ROW EXECUTE FUNCTION orchestration.default_run_model();

CREATE FUNCTION tenancy.model_view(m tenancy."Models") RETURNS jsonb LANGUAGE sql IMMUTABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT pg_catalog.jsonb_build_object('id',m."Id",'organizationId',m."OrganizationId",'name',m."Name",'protocol',m."Protocol",
        'baseUrl',m."BaseUrl",'model',m."Model",'status',m."Status",'structuredOutput',m."StructuredOutput",
        'verifiedAt',m."VerifiedAt",'createdAt',m."CreatedAt");
$$;

CREATE FUNCTION tenancy.register_model(p_org text,p_issuer text,p_subject text,p_kind text,p_id uuid,p_name text,p_protocol text,
    p_base_url text,p_model text,p_ciphertext bytea,p_input_price bigint,p_output_price bigint) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE actor uuid; m tenancy."Models"%ROWTYPE; now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    actor:=tenancy.lock_admin(p_org,p_issuer,p_subject,p_kind);
    IF p_id IS NULL OR (p_input_price IS NULL) <> (p_output_price IS NULL) OR p_input_price<0 OR p_output_price<0
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    IF EXISTS (SELECT 1 FROM tenancy."Models" WHERE "OrganizationId"=p_org AND "Name"=p_name) THEN RAISE EXCEPTION 'model_exists' USING ERRCODE='P6009'; END IF;
    INSERT INTO tenancy."Models" ("Id","OrganizationId","Name","Protocol","BaseUrl","Model","ApiKeyCiphertext","Status","CreatedBy","CreatedAt")
        VALUES (p_id,p_org,p_name,p_protocol,p_base_url,p_model,p_ciphertext,'Unverified',actor,now) RETURNING * INTO m;
    -- Customer-declared prices let budgets price the customer's own model; clearly sourced as such.
    IF p_input_price IS NOT NULL THEN
        INSERT INTO orchestration."ModelPrices" ("Provider","Model","EffectiveFrom","InputMicrosPerMillion","OutputMicrosPerMillion","Source")
            VALUES ('customer:' || pg_catalog.replace(p_id::text,'-',''),p_model,now,p_input_price,p_output_price,
                'customer-declared at registration by organization admin ' || actor::text);
    END IF;
    RETURN tenancy.model_view(m);
END;
$$;

CREATE FUNCTION tenancy.list_models(p_org text,p_issuer text,p_subject text,p_kind text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    PERFORM tenancy.lock_admin(p_org,p_issuer,p_subject,p_kind);
    RETURN COALESCE((SELECT pg_catalog.jsonb_agg(tenancy.model_view(m) ORDER BY m."CreatedAt",m."Id") FROM tenancy."Models" m WHERE m."OrganizationId"=p_org),'[]'::jsonb);
END;
$$;

CREATE FUNCTION tenancy.endpoint_json(m tenancy."Models") RETURNS jsonb LANGUAGE sql IMMUTABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT pg_catalog.jsonb_build_object('modelId',m."Id",'organizationId',m."OrganizationId",'protocol',m."Protocol",'baseUrl',m."BaseUrl",
        'model',m."Model",'encryptedApiKey',pg_catalog.encode(m."ApiKeyCiphertext",'base64'));
$$;

-- For verification by an organization admin through the API (ciphertext only).
CREATE FUNCTION tenancy.model_endpoint(p_org text,p_issuer text,p_subject text,p_kind text,p_model uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE m tenancy."Models"%ROWTYPE;
BEGIN
    PERFORM tenancy.lock_admin(p_org,p_issuer,p_subject,p_kind);
    SELECT * INTO m FROM tenancy."Models" WHERE "Id"=p_model AND "OrganizationId"=p_org;
    IF NOT FOUND THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    RETURN tenancy.endpoint_json(m);
END;
$$;

CREATE FUNCTION tenancy.record_verification(p_org text,p_issuer text,p_subject text,p_kind text,p_model uuid,p_status text,p_structured boolean) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE m tenancy."Models"%ROWTYPE;
BEGIN
    PERFORM tenancy.lock_admin(p_org,p_issuer,p_subject,p_kind);
    IF p_status NOT IN ('Verified','Failed') OR (p_status='Failed' AND p_structured IS NOT NULL) OR (p_status='Verified' AND p_structured IS NULL)
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    UPDATE tenancy."Models" SET "Status"=p_status,"StructuredOutput"=p_structured,"VerifiedAt"=pg_catalog.clock_timestamp()
        WHERE "Id"=p_model AND "OrganizationId"=p_org RETURNING * INTO m;
    IF NOT FOUND THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    RETURN tenancy.model_view(m);
END;
$$;

CREATE FUNCTION tenancy.assign_model(p_project text,p_issuer text,p_subject text,p_kind text,p_model uuid,p_accept boolean) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE actor uuid; m tenancy."Models"%ROWTYPE; now timestamptz:=pg_catalog.clock_timestamp();
BEGIN
    actor:=tenancy.lock_project_admin(p_project,p_issuer,p_subject,p_kind);
    IF p_accept IS NULL THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    IF p_model IS NOT NULL THEN
        SELECT m2.* INTO m FROM tenancy."Models" m2 JOIN tenancy."ProjectOwnership" o ON o."OrganizationId"=m2."OrganizationId"
            WHERE m2."Id"=p_model AND o."ProjectId"=p_project FOR SHARE OF m2;
        IF NOT FOUND THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
        -- Never restricted, but an unverified or non-structured model needs the customer's explicit acceptance.
        IF (m."Status"<>'Verified' OR m."StructuredOutput" IS NOT TRUE) AND NOT p_accept
            THEN RAISE EXCEPTION 'capability_unverified' USING ERRCODE='P7030'; END IF;
    END IF;
    INSERT INTO tenancy."ProjectModels" ("Id","ProjectId","ModelId","AcceptedCapabilityRisk","AssignedBy","AssignedAt")
        VALUES (pg_catalog.gen_random_uuid(),p_project,p_model,p_accept,actor,now);
    RETURN pg_catalog.jsonb_build_object('projectId',p_project,'modelId',p_model,'acceptedCapabilityRisk',p_accept,'assignedAt',now);
END;
$$;

-- Worker access to its run's model endpoint, authorized by the attempt lease.
CREATE FUNCTION orchestration.model_for_attempt(p_attempt uuid,p_token bytea) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE a orchestration."StepAttempts"%ROWTYPE; m tenancy."Models"%ROWTYPE;
BEGIN
    a:=orchestration.lock_attempt(p_attempt,p_token);
    SELECT md.* INTO m FROM orchestration."WorkflowRuns" r JOIN tenancy."Models" md ON md."Id"=r."ModelId" WHERE r."Id"=a."RunId";
    IF NOT FOUND THEN RETURN NULL; END IF;
    RETURN tenancy.endpoint_json(m);
END;
$$;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA tenancy FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA orchestration FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA tenancy FROM PUBLIC;
