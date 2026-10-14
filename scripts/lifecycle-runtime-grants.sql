-- Explicit provisioning AFTER the M6 migration, in a dedicated Factory database.
-- Provision factory_lifecycle_runtime LOGIN separately. Never use the owner login in the API.
BEGIN;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='factory_lifecycle_guard') THEN
        CREATE ROLE factory_lifecycle_guard NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname IN ('factory_lifecycle_guard','factory_lifecycle_runtime') AND
        (rolsuper OR rolinherit OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls OR (rolname='factory_lifecycle_guard' AND rolcanlogin)))
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m JOIN pg_catalog.pg_roles r ON r.oid IN (m.member,m.roleid)
            WHERE r.rolname IN ('factory_lifecycle_guard','factory_lifecycle_runtime')) THEN RAISE EXCEPTION 'unsafe_lifecycle_roles'; END IF;
END $$;
REVOKE ALL ON SCHEMA lifecycle FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA lifecycle FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA lifecycle FROM PUBLIC, factory_lifecycle_runtime;
REVOKE ALL ON ALL TABLES IN SCHEMA review, access FROM factory_lifecycle_runtime;
GRANT USAGE ON SCHEMA lifecycle TO factory_lifecycle_runtime, factory_lifecycle_guard, factory_review_writer, factory_decision_runtime;
GRANT USAGE ON SCHEMA review, access TO factory_lifecycle_guard;
GRANT SELECT ON access."Projects", access."Principals", access."ProjectMemberships" TO factory_lifecycle_guard;
GRANT UPDATE ("Revision") ON access."Projects", access."Principals", access."ProjectMemberships" TO factory_lifecycle_guard;
GRANT SELECT, INSERT ON lifecycle."WorkItems", lifecycle."GenerationCommands", review."ReviewCaseLineage",
    review."ReviewCases", review."ArtifactVersions", review."PublicationEvents" TO factory_lifecycle_guard;
GRANT UPDATE ("Revision","AttemptNumber","ActiveRunId","LatestRunId") ON lifecycle."WorkItems" TO factory_lifecycle_guard;
GRANT UPDATE ("Status","HttpStatus","FailureCode","ContentHash","ResultRevision") ON lifecycle."GenerationCommands" TO factory_lifecycle_guard;
GRANT UPDATE ("CurrentVersion","Revision","State") ON review."ReviewCases" TO factory_lifecycle_guard;
GRANT CREATE ON SCHEMA lifecycle TO factory_lifecycle_guard;
DO $$ DECLARE f record; BEGIN
    FOR f IN SELECT p.oid::regprocedure AS signature FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='lifecycle' LOOP
        EXECUTE pg_catalog.format('ALTER FUNCTION %s OWNER TO factory_lifecycle_guard',f.signature);
    END LOOP;
END $$;
REVOKE CREATE ON SCHEMA lifecycle FROM factory_lifecycle_guard;
GRANT EXECUTE ON FUNCTION lifecycle.admit(text,text,text,text,uuid,uuid,text,text,jsonb,bytea),
    lifecycle.publish(text,text,text,text,uuid,bytea,bytea), lifecycle.finish(text,text,text,text,uuid,bytea,text,text),
    lifecycle.read_work_item(text,text,text,text,uuid), lifecycle.read_run(text,text,text,text,uuid)
    TO factory_lifecycle_runtime;
REVOKE INSERT ON review."ReviewCases" FROM factory_review_writer;
DO $$ DECLARE s text; t record; BEGIN
    EXECUTE pg_catalog.format('REVOKE CREATE, TEMPORARY ON DATABASE %I FROM PUBLIC, factory_lifecycle_runtime, factory_lifecycle_guard',pg_catalog.current_database());
    FOREACH s IN ARRAY ARRAY['public','review','access','lifecycle'] LOOP
        EXECUTE pg_catalog.format('REVOKE CREATE ON SCHEMA %I FROM factory_lifecycle_runtime, factory_lifecycle_guard',s);
    END LOOP;
    FOR t IN SELECT schemaname,tablename,tableowner FROM pg_catalog.pg_tables WHERE schemaname IN ('review','access','lifecycle') LOOP
        IF t.tableowner IN ('factory_lifecycle_runtime','factory_lifecycle_guard') OR
            pg_catalog.has_table_privilege('factory_lifecycle_runtime',pg_catalog.format('%I.%I',t.schemaname,t.tablename),'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER') OR
            pg_catalog.has_any_column_privilege('factory_lifecycle_runtime',pg_catalog.format('%I.%I',t.schemaname,t.tablename),'SELECT,INSERT,UPDATE,REFERENCES')
            THEN RAISE EXCEPTION 'unsafe_lifecycle_grants'; END IF;
    END LOOP;
END $$;
COMMIT;
