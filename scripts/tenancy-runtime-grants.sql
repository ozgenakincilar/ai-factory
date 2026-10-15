-- Explicit provisioning AFTER the M8.2 migration and orchestration-runtime-grants.sql, in the same
-- dedicated Factory database. Organizations, memberships and ownership are operator-managed with
-- the owner login; runtime roles never read or write tenancy tables directly.
BEGIN;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='factory_lifecycle_guard')
        OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='factory_lifecycle_runtime')
        OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='factory_orchestration_worker') THEN
        RAISE EXCEPTION 'tenancy_roles_missing';
    END IF;
END $$;
REVOKE ALL ON SCHEMA tenancy FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA tenancy FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA tenancy FROM PUBLIC, factory_lifecycle_runtime, factory_orchestration_worker;
GRANT USAGE ON SCHEMA tenancy TO factory_lifecycle_guard, factory_lifecycle_runtime;
GRANT SELECT ON tenancy."Organizations", tenancy."OrganizationMemberships", tenancy."ProjectOwnership" TO factory_lifecycle_guard;
-- UPDATE on one column is required only for FOR SHARE revocation locks; functions never update it.
GRANT UPDATE ("Revision") ON tenancy."Organizations", tenancy."OrganizationMemberships" TO factory_lifecycle_guard;
GRANT CREATE ON SCHEMA tenancy TO factory_lifecycle_guard;
DO $$ DECLARE f record; BEGIN
    FOR f IN SELECT p.oid::regprocedure AS signature FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='tenancy' LOOP
        EXECUTE pg_catalog.format('ALTER FUNCTION %s OWNER TO factory_lifecycle_guard',f.signature);
    END LOOP;
END $$;
REVOKE CREATE ON SCHEMA tenancy FROM factory_lifecycle_guard;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA tenancy FROM PUBLIC;
GRANT EXECUTE ON FUNCTION tenancy.read_usage(text,text,text,text,timestamptz,timestamptz),
    tenancy.read_budgets(text,text,text,text) TO factory_lifecycle_runtime;
DO $$ DECLARE t record; BEGIN
    EXECUTE 'REVOKE CREATE ON SCHEMA tenancy FROM factory_orchestration_worker, factory_lifecycle_runtime';
    FOR t IN SELECT schemaname,tablename FROM pg_catalog.pg_tables WHERE schemaname='tenancy' LOOP
        IF pg_catalog.has_table_privilege('factory_lifecycle_runtime',pg_catalog.format('%I.%I',t.schemaname,t.tablename),'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
            OR pg_catalog.has_any_column_privilege('factory_lifecycle_runtime',pg_catalog.format('%I.%I',t.schemaname,t.tablename),'SELECT,INSERT,UPDATE,REFERENCES')
            OR pg_catalog.has_table_privilege('factory_orchestration_worker',pg_catalog.format('%I.%I',t.schemaname,t.tablename),'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
            OR pg_catalog.has_any_column_privilege('factory_orchestration_worker',pg_catalog.format('%I.%I',t.schemaname,t.tablename),'SELECT,INSERT,UPDATE,REFERENCES')
            THEN RAISE EXCEPTION 'unsafe_tenancy_grants'; END IF;
    END LOOP;
END $$;
COMMIT;
