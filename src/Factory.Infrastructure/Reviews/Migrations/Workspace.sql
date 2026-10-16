-- Versioned M12a migration. Never edit after release. Read-only listings for the Product Workspace.
-- No table changes. Owner provisioning is separate (orchestration- and tenancy-runtime-grants.sql).

-- The caller's readable projects (same audience as read_run: active Generator, or active Human
-- Reviewer), their organization and whether the caller may decide (active Human Owner/Admin of an
-- active organization). An unknown or inactive principal gets an empty list, never an error, so
-- the response does not reveal which identities exist.
CREATE FUNCTION tenancy.my_projects(p_issuer text,p_subject text,p_kind text) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
    WITH me AS (
        SELECT "Id" FROM access."Principals" WHERE "Issuer"=p_issuer AND "Subject"=p_subject AND "Kind"=p_kind AND "IsActive"),
    mine AS (
        SELECT m."ProjectId", pg_catalog.array_agg(DISTINCT m."Role" ORDER BY m."Role") AS roles, me."Id" AS principal
        FROM access."ProjectMemberships" m JOIN me ON m."PrincipalId"=me."Id"
        JOIN access."Projects" p ON p."Id"=m."ProjectId" AND p."IsActive"
        WHERE m."IsActive" AND m."PrincipalKind"=p_kind AND (m."Role"='Generator' OR (p_kind='Human' AND m."Role"='Reviewer'))
        GROUP BY m."ProjectId", me."Id")
    SELECT COALESCE(pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object('projectId',mine."ProjectId",'organizationId',o."OrganizationId",
            'roles',pg_catalog.to_jsonb(mine.roles),
            'canDecide',p_kind='Human' AND EXISTS (SELECT 1 FROM tenancy."OrganizationMemberships" om JOIN tenancy."Organizations" g ON g."Id"=om."OrganizationId"
                WHERE om."OrganizationId"=o."OrganizationId" AND om."PrincipalId"=mine.principal AND om."Role" IN ('Owner','Admin')
                    AND om."IsActive" AND g."IsActive"))
            ORDER BY mine."ProjectId"),'[]'::jsonb)
    FROM mine LEFT JOIN tenancy."ProjectOwnership" o ON o."ProjectId"=mine."ProjectId";
$$;

-- The 50 most recent runs of a project as safe summaries (no input, outputs or identities).
CREATE FUNCTION orchestration.list_runs(p_project text,p_issuer text,p_subject text,p_kind text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    PERFORM lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind,true);
    RETURN COALESCE((SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object('runId',r."Id",'status',r."Status",
            'workflowVersion',r."WorkflowVersion",'failureCode',r."FailureCode",'createdAt',r."CreatedAt",'updatedAt',r."UpdatedAt",
            'pendingDecisions',(SELECT pg_catalog.count(*) FROM orchestration."StepDecisions" d WHERE d."RunId"=r."Id" AND d."Status"='Pending'))
            ORDER BY r."CreatedAt" DESC, r."Id")
        FROM (SELECT * FROM orchestration."WorkflowRuns" WHERE "ProjectId"=p_project ORDER BY "CreatedAt" DESC, "Id" LIMIT 50) r),'[]'::jsonb);
END;
$$;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA orchestration FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA tenancy FROM PUBLIC;
