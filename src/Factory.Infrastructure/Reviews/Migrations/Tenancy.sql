-- Versioned M8.2 migration. Never edit after release. Adds organizations (tenants), human
-- organization roles and immutable project ownership. Owner provisioning is separate
-- (scripts/tenancy-runtime-grants.sql). Access tables and their EF model are unchanged.
CREATE SCHEMA tenancy;
REVOKE ALL ON SCHEMA tenancy FROM PUBLIC;

CREATE TABLE tenancy."Organizations" (
    "Id" varchar(210) PRIMARY KEY CHECK (pg_catalog.length(pg_catalog.btrim("Id"))>0),
    "Name" varchar(200) NOT NULL CHECK (pg_catalog.length(pg_catalog.btrim("Name"))>0),
    "IsActive" boolean NOT NULL,
    "Revision" bigint NOT NULL CHECK ("Revision">0)
);
-- Organization authority is human only; Service identities can never govern a tenant.
CREATE TABLE tenancy."OrganizationMemberships" (
    "OrganizationId" varchar(210) NOT NULL REFERENCES tenancy."Organizations"("Id"),
    "PrincipalId" uuid NOT NULL,
    "PrincipalKind" varchar(16) NOT NULL CHECK ("PrincipalKind"='Human'),
    "Role" text NOT NULL CHECK ("Role" IN ('Owner','Admin')),
    "IsActive" boolean NOT NULL,
    "Revision" bigint NOT NULL CHECK ("Revision">0),
    PRIMARY KEY ("OrganizationId","PrincipalId","Role"),
    FOREIGN KEY ("PrincipalId","PrincipalKind") REFERENCES access."Principals"("Id","Kind")
);
-- Every project belongs to exactly one organization; ownership never moves.
CREATE TABLE tenancy."ProjectOwnership" (
    "ProjectId" varchar(200) PRIMARY KEY REFERENCES access."Projects"("Id"),
    "OrganizationId" varchar(210) NOT NULL REFERENCES tenancy."Organizations"("Id")
);
CREATE INDEX ownership_by_organization ON tenancy."ProjectOwnership"("OrganizationId");
CREATE TRIGGER ownership_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON tenancy."ProjectOwnership"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();
CREATE TRIGGER organizations_no_delete BEFORE DELETE OR TRUNCATE ON tenancy."Organizations"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();
CREATE TRIGGER organization_memberships_no_delete BEFORE DELETE OR TRUNCATE ON tenancy."OrganizationMemberships"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();

-- Backfill: one separate legacy organization per existing project, so no two existing projects
-- are ever merged into one tenant. Existing active Human project Reviewers become organization
-- Admins, preserving who could decide waiting steps (the M7.2 interim authority).
INSERT INTO tenancy."Organizations" ("Id","Name","IsActive","Revision")
    SELECT 'legacy-' || "Id", 'Legacy ' || "Id", true, 1 FROM access."Projects";
INSERT INTO tenancy."ProjectOwnership" ("ProjectId","OrganizationId")
    SELECT "Id", 'legacy-' || "Id" FROM access."Projects";
INSERT INTO tenancy."OrganizationMemberships" ("OrganizationId","PrincipalId","PrincipalKind","Role","IsActive","Revision")
    SELECT DISTINCT 'legacy-' || m."ProjectId", m."PrincipalId", 'Human', 'Admin', true, 1
    FROM access."ProjectMemberships" m WHERE m."Role"='Reviewer' AND m."PrincipalKind"='Human' AND m."IsActive";

CREATE FUNCTION tenancy.check_project_owned() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM tenancy."ProjectOwnership" WHERE "ProjectId"=NEW."Id")
        THEN RAISE EXCEPTION 'project_owner_required' USING ERRCODE='23514'; END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER project_owned AFTER INSERT ON access."Projects" DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION tenancy.check_project_owned();

-- Locks organization, principal and role rows against concurrent revocation.
CREATE FUNCTION tenancy.lock_admin(p_org text,p_issuer text,p_subject text,p_kind text) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE actor uuid;
BEGIN
    IF p_kind IS DISTINCT FROM 'Human' THEN RAISE EXCEPTION 'forbidden' USING ERRCODE='42501'; END IF;
    PERFORM 1 FROM tenancy."Organizations" WHERE "Id"=p_org AND "IsActive" FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION 'forbidden' USING ERRCODE='42501'; END IF;
    SELECT "Id" INTO actor FROM access."Principals" WHERE "Issuer"=p_issuer AND "Subject"=p_subject AND "Kind"='Human' AND "IsActive" FOR SHARE;
    IF actor IS NULL THEN RAISE EXCEPTION 'forbidden' USING ERRCODE='42501'; END IF;
    PERFORM 1 FROM tenancy."OrganizationMemberships" WHERE "OrganizationId"=p_org AND "PrincipalId"=actor
        AND "Role" IN ('Owner','Admin') AND "IsActive" FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION 'forbidden' USING ERRCODE='42501'; END IF;
    RETURN actor;
END;
$$;

CREATE FUNCTION tenancy.lock_project_admin(p_project text,p_issuer text,p_subject text,p_kind text) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE org text;
BEGIN
    SELECT o."OrganizationId" INTO org FROM tenancy."ProjectOwnership" o JOIN access."Projects" p ON p."Id"=o."ProjectId"
        WHERE o."ProjectId"=p_project AND p."IsActive" FOR SHARE OF p;
    IF org IS NULL THEN RAISE EXCEPTION 'forbidden' USING ERRCODE='42501'; END IF;
    RETURN tenancy.lock_admin(org,p_issuer,p_subject,p_kind);
END;
$$;

-- Decisions now require an active Owner/Admin of the project's organization (replaces the
-- M7.2 interim Human project Reviewer authority). Service identities are always refused.
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
        UPDATE orchestration."WorkflowRuns" SET "Status"='Running',"UpdatedAt"=now WHERE "Id"=r."Id";
    END IF;
    SELECT * INTO STRICT r FROM orchestration."WorkflowRuns" WHERE "Id"=p_run;
    RETURN orchestration.view(r);
END;
$$;

-- Organization usage across its projects, for Owner/Admin only.
CREATE FUNCTION tenancy.read_usage(p_org text,p_issuer text,p_subject text,p_kind text,p_from timestamptz,p_to timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE calls jsonb;
BEGIN
    PERFORM tenancy.lock_admin(p_org,p_issuer,p_subject,p_kind);
    IF p_from IS NULL OR p_to IS NULL OR p_from>=p_to OR p_to-p_from>interval '366 days'
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    SELECT pg_catalog.jsonb_agg(pg_catalog.to_jsonb(m)) INTO calls FROM orchestration."ModelCalls" m
        JOIN tenancy."ProjectOwnership" o ON o."ProjectId"=m."ProjectId"
        WHERE o."OrganizationId"=p_org AND m."CreatedAt">=p_from AND m."CreatedAt"<p_to;
    RETURN pg_catalog.jsonb_build_object('organizationId',p_org,'from',p_from,'to',p_to,'totals',orchestration.usage_totals(calls),
        'projects',COALESCE((SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object('projectId',g.project,'totals',orchestration.usage_totals(g.items)) ORDER BY g.project)
            FROM (SELECT c->>'ProjectId' AS project,pg_catalog.jsonb_agg(c) AS items FROM pg_catalog.jsonb_array_elements(COALESCE(calls,'[]'::jsonb)) c GROUP BY 1) g),'[]'::jsonb),
        'models',COALESCE((SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object('provider',g.provider,'model',g.model,'totals',orchestration.usage_totals(g.items))
                ORDER BY g.provider NULLS LAST,g.model NULLS LAST)
            FROM (SELECT c->>'Provider' AS provider,c->>'Model' AS model,pg_catalog.jsonb_agg(c) AS items
                FROM pg_catalog.jsonb_array_elements(COALESCE(calls,'[]'::jsonb)) c GROUP BY 1,2) g),'[]'::jsonb));
END;
$$;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA tenancy FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA tenancy FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA orchestration FROM PUBLIC;
