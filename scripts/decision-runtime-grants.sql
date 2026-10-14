-- Explicit operator provisioning after the 5B.2 migration. Never run in API startup.
BEGIN;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'factory_review_guard') THEN
        CREATE ROLE factory_review_guard NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname IN ('factory_review_guard','factory_decision_runtime') AND
        (rolsuper OR rolinherit OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls OR
         (rolname = 'factory_review_guard' AND rolcanlogin))) OR
       EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m JOIN pg_catalog.pg_roles r ON r.oid IN (m.member,m.roleid)
               WHERE r.rolname IN ('factory_review_guard','factory_decision_runtime')) THEN
        RAISE EXCEPTION 'unsafe_decision_role';
    END IF;
END $$;
-- This is a dedicated factory database. Neither runtime needs database-level DDL.
DO $$ BEGIN
    EXECUTE pg_catalog.format('REVOKE CREATE, TEMPORARY ON DATABASE %I FROM PUBLIC, factory_decision_runtime, factory_review_guard', pg_catalog.current_database());
END $$;
REVOKE CREATE ON SCHEMA public FROM PUBLIC, factory_decision_runtime, factory_review_guard;
REVOKE ALL ON SCHEMA review, access FROM PUBLIC;
REVOKE ALL ON review."DecisionEvents", review."CommandReceipts" FROM PUBLIC;
GRANT USAGE ON SCHEMA review, access TO factory_review_guard, factory_decision_runtime;
GRANT SELECT ON access."Projects", access."Principals", access."ProjectMemberships" TO factory_review_guard;
-- Required only by PostgreSQL FOR SHARE, never exposed to runtime credentials.
GRANT UPDATE ("Revision") ON access."Projects", access."Principals", access."ProjectMemberships" TO factory_review_guard;
GRANT SELECT ON review."ReviewCases", review."DecisionEvents", review."CommandReceipts" TO factory_review_guard;
-- PostgreSQL requires CREATE temporarily to transfer function ownership.
GRANT CREATE ON SCHEMA review TO factory_review_guard;
ALTER FUNCTION review.lock_reviewer(text,text,text) OWNER TO factory_review_guard;
ALTER FUNCTION review.prepare_decision() OWNER TO factory_review_guard;
ALTER FUNCTION review.check_case_decision() OWNER TO factory_review_guard;
ALTER FUNCTION review.check_decision_complete() OWNER TO factory_review_guard;
ALTER FUNCTION review.decision_fingerprint(text,uuid,uuid,bigint,integer,text,text,text) OWNER TO factory_review_guard;
REVOKE CREATE ON SCHEMA review FROM factory_review_guard;
REVOKE ALL ON FUNCTION review.lock_reviewer(text,text,text), review.prepare_decision(), review.check_case_decision(),
    review.check_decision_complete(), review.decision_fingerprint(text,uuid,uuid,bigint,integer,text,text,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION review.lock_reviewer(text,text,text),
    review.decision_fingerprint(text,uuid,uuid,bigint,integer,text,text,text) TO factory_decision_runtime;
GRANT SELECT ON review."ReviewCases", review."ArtifactVersions", review."DecisionEvents", review."CommandReceipts" TO factory_decision_runtime;
GRANT UPDATE ("State", "Revision") ON review."ReviewCases" TO factory_decision_runtime;
GRANT INSERT ON review."DecisionEvents", review."CommandReceipts" TO factory_decision_runtime;
DO $$ BEGIN
    IF pg_catalog.has_schema_privilege('factory_decision_runtime','review','CREATE') OR
       pg_catalog.has_schema_privilege('factory_decision_runtime','access','CREATE') OR
       EXISTS (SELECT 1 FROM pg_catalog.pg_tables WHERE schemaname IN ('review','access') AND tableowner IN ('factory_decision_runtime','factory_review_guard')) OR
       EXISTS (SELECT 1 FROM pg_catalog.pg_tables WHERE schemaname = 'access' AND
           pg_catalog.has_table_privilege('factory_decision_runtime', format('%I.%I',schemaname,tablename), 'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')) OR
       EXISTS (SELECT 1 FROM pg_catalog.pg_tables WHERE schemaname = 'access' AND
           pg_catalog.has_any_column_privilege('factory_decision_runtime', format('%I.%I',schemaname,tablename), 'UPDATE')) THEN
        RAISE EXCEPTION 'unsafe_decision_grants';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_tables t WHERE t.schemaname = 'review' AND
        (pg_catalog.has_table_privilege('factory_decision_runtime', format('%I.%I',t.schemaname,t.tablename), 'UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER') OR
         (t.tablename NOT IN ('DecisionEvents','CommandReceipts') AND pg_catalog.has_table_privilege('factory_decision_runtime', format('%I.%I',t.schemaname,t.tablename), 'INSERT')))) OR
       EXISTS (SELECT 1 FROM information_schema.columns c WHERE c.table_schema IN ('access','review') AND
        pg_catalog.has_column_privilege('factory_decision_runtime', format('%I.%I',c.table_schema,c.table_name),c.column_name,'UPDATE') AND
        NOT (c.table_schema = 'review' AND c.table_name = 'ReviewCases' AND c.column_name IN ('State','Revision'))) OR
       EXISTS (SELECT 1 FROM pg_catalog.pg_tables t WHERE t.schemaname IN ('review','access') AND
        pg_catalog.has_table_privilege('factory_review_guard', format('%I.%I',t.schemaname,t.tablename), 'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')) OR
       EXISTS (SELECT 1 FROM information_schema.columns c WHERE c.table_schema IN ('access','review') AND
        pg_catalog.has_column_privilege('factory_review_guard', format('%I.%I',c.table_schema,c.table_name),c.column_name,'UPDATE') AND
        NOT (c.table_schema = 'access' AND c.column_name = 'Revision')) THEN
        RAISE EXCEPTION 'unsafe_decision_column_grants';
    END IF;
END $$;
COMMIT;
