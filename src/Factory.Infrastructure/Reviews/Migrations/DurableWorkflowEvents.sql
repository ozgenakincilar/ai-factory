-- Versioned M7.3 migration. Never edit after release. Adds commit-time run change notifications
-- and project watch authorization. Owner provisioning is separate (orchestration-runtime-grants.sql).

-- Notifications are hints delivered only at COMMIT: identifiers and status, never request content,
-- model output, decisions or identities. Any database login may LISTEN, so the payload stays minimal.
CREATE FUNCTION orchestration.notify_run() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    PERFORM pg_catalog.pg_notify('orchestration_runs', pg_catalog.jsonb_build_object(
        'projectId',NEW."ProjectId",'runId',NEW."Id",'status',NEW."Status")::text);
    RETURN NULL;
END;
$$;
CREATE TRIGGER run_changed AFTER INSERT OR UPDATE ON orchestration."WorkflowRuns" FOR EACH ROW EXECUTE FUNCTION orchestration.notify_run();

-- Same audience as read_run: active Generator or Human Reviewer of the project.
CREATE FUNCTION orchestration.authorize_watch(p_project text,p_issuer text,p_subject text,p_kind text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    PERFORM lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind,true);
    RETURN 'true'::jsonb;
END;
$$;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA orchestration FROM PUBLIC;
