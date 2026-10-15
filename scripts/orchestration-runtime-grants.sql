-- Explicit provisioning AFTER the M7 migration and lifecycle-runtime-grants.sql, in the same
-- dedicated Factory database. Provision factory_orchestration_worker LOGIN separately.
-- Never use the owner login in the API or worker.
BEGIN;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='factory_lifecycle_guard')
        OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='factory_orchestration_worker') THEN
        RAISE EXCEPTION 'orchestration_roles_missing';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='factory_orchestration_worker' AND
        (rolsuper OR rolinherit OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls))
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m JOIN pg_catalog.pg_roles r ON r.oid IN (m.member,m.roleid)
            WHERE r.rolname IN ('factory_orchestration_worker','factory_lifecycle_guard')) THEN RAISE EXCEPTION 'unsafe_orchestration_roles'; END IF;
END $$;
REVOKE ALL ON SCHEMA orchestration FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA orchestration FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA orchestration FROM PUBLIC, factory_lifecycle_runtime, factory_orchestration_worker;
REVOKE ALL ON ALL TABLES IN SCHEMA review, access, lifecycle FROM factory_orchestration_worker;
GRANT USAGE ON SCHEMA orchestration TO factory_lifecycle_runtime, factory_orchestration_worker, factory_lifecycle_guard;
GRANT SELECT, INSERT ON orchestration."WorkflowRuns", orchestration."WorkflowSteps", orchestration."StepAttempts",
    orchestration."StepDecisions" TO factory_lifecycle_guard;
GRANT UPDATE ("Status","FailureCode","WorkItemId","UpdatedAt","CancelledBy","CancelledAt") ON orchestration."WorkflowRuns" TO factory_lifecycle_guard;
GRANT UPDATE ("Status","Choice","DecidedBy","DecidedAt") ON orchestration."StepDecisions" TO factory_lifecycle_guard;
GRANT SELECT, INSERT ON orchestration."ModelCalls" TO factory_lifecycle_guard;
GRANT SELECT ON orchestration."ModelPrices", orchestration."Budgets" TO factory_lifecycle_guard;
GRANT SELECT, INSERT ON orchestration."CostReservations" TO factory_lifecycle_guard;
GRANT UPDATE ("Status","Attempts","Output") ON orchestration."WorkflowSteps" TO factory_lifecycle_guard;
GRANT UPDATE ("Outcome","FailureCode","FinishedAt","LeaseExpiresAt") ON orchestration."StepAttempts" TO factory_lifecycle_guard;
-- The guard owns the functions so managed publication passes the M6 case-mode guard.
GRANT CREATE ON SCHEMA orchestration TO factory_lifecycle_guard;
DO $$ DECLARE f record; BEGIN
    FOR f IN SELECT p.oid::regprocedure AS signature FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='orchestration' LOOP
        EXECUTE pg_catalog.format('ALTER FUNCTION %s OWNER TO factory_lifecycle_guard',f.signature);
    END LOOP;
END $$;
REVOKE CREATE ON SCHEMA orchestration FROM factory_lifecycle_guard;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA orchestration FROM PUBLIC;
GRANT EXECUTE ON FUNCTION orchestration.submit(text,text,text,text,uuid,text,jsonb),
    orchestration.submit_regeneration(text,text,text,text,uuid,uuid,text,text,jsonb),
    orchestration.read_run(text,text,text,text,uuid), orchestration.cancel(text,text,text,text,uuid),
    orchestration.decide(text,text,text,text,uuid,uuid,text), orchestration.authorize_watch(text,text,text,text),
    orchestration.read_usage(text,text,text,text,timestamptz,timestamptz) TO factory_lifecycle_runtime;
GRANT EXECUTE ON FUNCTION orchestration.claim(text,bytea), orchestration.renew(uuid,bytea),
    orchestration.complete(uuid,bytea,jsonb), orchestration.fail(uuid,bytea,text),
    orchestration.publish(uuid,bytea,bytea), orchestration.request_decision(uuid,bytea,text,text[]),
    orchestration.record_model_call(uuid,bytea,text,text,integer,integer,integer,text,uuid),
    orchestration.reserve_cost(uuid,bytea,text,text,integer,integer) TO factory_orchestration_worker;
DO $$ DECLARE s text; t record; BEGIN
    EXECUTE pg_catalog.format('REVOKE CREATE, TEMPORARY ON DATABASE %I FROM factory_orchestration_worker',pg_catalog.current_database());
    FOREACH s IN ARRAY ARRAY['public','review','access','lifecycle','orchestration'] LOOP
        EXECUTE pg_catalog.format('REVOKE CREATE ON SCHEMA %I FROM factory_orchestration_worker, factory_lifecycle_runtime, factory_lifecycle_guard',s);
    END LOOP;
    FOR t IN SELECT schemaname,tablename,tableowner FROM pg_catalog.pg_tables WHERE schemaname IN ('review','access','lifecycle','orchestration') LOOP
        IF t.tableowner IN ('factory_orchestration_worker','factory_lifecycle_runtime','factory_lifecycle_guard') OR
            pg_catalog.has_table_privilege('factory_orchestration_worker',pg_catalog.format('%I.%I',t.schemaname,t.tablename),'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER') OR
            pg_catalog.has_any_column_privilege('factory_orchestration_worker',pg_catalog.format('%I.%I',t.schemaname,t.tablename),'SELECT,INSERT,UPDATE,REFERENCES') OR
            (t.schemaname='orchestration' AND (
                pg_catalog.has_table_privilege('factory_lifecycle_runtime',pg_catalog.format('%I.%I',t.schemaname,t.tablename),'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER') OR
                pg_catalog.has_any_column_privilege('factory_lifecycle_runtime',pg_catalog.format('%I.%I',t.schemaname,t.tablename),'SELECT,INSERT,UPDATE,REFERENCES')))
            THEN RAISE EXCEPTION 'unsafe_orchestration_grants'; END IF;
    END LOOP;
END $$;
COMMIT;
