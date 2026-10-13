-- Apply as migration owner. Provision a dedicated, non-inheriting login separately.
REVOKE ALL ON SCHEMA access FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA access FROM PUBLIC;
GRANT USAGE ON SCHEMA access TO factory_authorization_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA access TO factory_authorization_reader;
-- No memberships, writes, ownership, schema CREATE or review grants for this login.
DO $$
DECLARE reader oid;
BEGIN
    SELECT oid INTO reader FROM pg_roles WHERE rolname = 'factory_authorization_reader';
    IF EXISTS (SELECT 1 FROM pg_roles WHERE oid = reader AND
        (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls OR rolinherit))
        OR EXISTS (SELECT 1 FROM pg_auth_members WHERE member = reader)
        OR has_schema_privilege('factory_authorization_reader', 'access', 'CREATE')
        OR EXISTS (SELECT 1 FROM pg_tables WHERE schemaname IN ('access','review') AND
            (tableowner = 'factory_authorization_reader' OR
             has_table_privilege('factory_authorization_reader', format('%I.%I',schemaname,tablename), 'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')))
    THEN RAISE EXCEPTION 'authorization_reader_has_unexpected_privileges'; END IF;
END;
$$;
