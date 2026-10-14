namespace Factory.Infrastructure.Reviews.Migrations;

// Versioned migration SQL: do not edit after release; use a later migration.
internal static class SecureHumanApprovalSql
{
    internal const string Up = """
CREATE FUNCTION review.lock_reviewer(p_project text, p_issuer text, p_subject text)
RETURNS TABLE ("PrincipalId" uuid, "ProjectRevision" bigint, "PrincipalRevision" bigint, "MembershipRevision" bigint)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE pr access."Projects"%ROWTYPE; actor access."Principals"%ROWTYPE; member access."ProjectMemberships"%ROWTYPE;
BEGIN
    SELECT p.* INTO pr FROM access."Projects" p WHERE p."Id" = p_project FOR SHARE;
    IF NOT FOUND OR NOT pr."IsActive" THEN RAISE EXCEPTION 'review_forbidden' USING ERRCODE = '42501'; END IF;
    SELECT p.* INTO actor FROM access."Principals" p WHERE p."Issuer" = p_issuer AND p."Subject" = p_subject FOR SHARE;
    IF NOT FOUND OR NOT actor."IsActive" OR actor."Kind" <> 'Human' THEN
        RAISE EXCEPTION 'review_forbidden' USING ERRCODE = '42501'; END IF;
    SELECT m.* INTO member FROM access."ProjectMemberships" m
        WHERE m."ProjectId" = p_project AND m."PrincipalId" = actor."Id" AND m."Role" = 'Reviewer' FOR SHARE;
    IF NOT FOUND OR NOT member."IsActive" OR member."PrincipalKind" <> 'Human' THEN
        RAISE EXCEPTION 'review_forbidden' USING ERRCODE = '42501'; END IF;
    RETURN QUERY SELECT actor."Id", pr."Revision", actor."Revision", member."Revision";
END;
$$;
REVOKE ALL ON FUNCTION review.lock_reviewer(text,text,text) FROM PUBLIC;

CREATE FUNCTION review.decision_fingerprint(p_project text, p_actor uuid, p_case uuid, p_revision bigint,
    p_version integer, p_hash text, p_decision text, p_reason text) RETURNS text
LANGUAGE sql IMMUTABLE SET search_path = pg_catalog, pg_temp AS $$
    SELECT pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
        pg_catalog.jsonb_build_array(1, p_project, p_actor, p_case, p_revision, p_version, p_hash, p_decision, p_reason)::text, 'UTF8')), 'hex');
$$;
REVOKE ALL ON FUNCTION review.decision_fingerprint(text,uuid,uuid,bigint,integer,text,text,text) FROM PUBLIC;

CREATE FUNCTION review.prepare_decision() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE auth record;
BEGIN
    SELECT * INTO STRICT auth FROM review.lock_reviewer(NEW."ProjectId", NEW."Issuer", NEW."Subject");
    IF NEW."PrincipalId" <> auth."PrincipalId" THEN RAISE EXCEPTION 'review_forbidden' USING ERRCODE = '42501'; END IF;
    NEW."ProjectRevision" := auth."ProjectRevision";
    NEW."PrincipalRevision" := auth."PrincipalRevision";
    NEW."MembershipRevision" := auth."MembershipRevision";
    NEW."CreatedAt" := pg_catalog.clock_timestamp();
    NEW."Fingerprint" := review.decision_fingerprint(NEW."ProjectId", NEW."PrincipalId", NEW."CaseId",
        NEW."ExpectedRevision", NEW."Version", NEW."ContentHash", NEW."Decision", NEW."Reason");
    RETURN NEW;
END;
$$;
REVOKE ALL ON FUNCTION review.prepare_decision() FROM PUBLIC;
CREATE TRIGGER decision_prepare BEFORE INSERT ON review."DecisionEvents" FOR EACH ROW EXECUTE FUNCTION review.prepare_decision();

CREATE FUNCTION review.guard_case_transition() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW."State" <> 'PendingReview' OR NEW."CurrentVersion" <> 1 OR NEW."Revision" <> 1 THEN
            RAISE EXCEPTION 'invalid_review_transition' USING ERRCODE = '23514'; END IF;
    ELSE
        IF NEW."Id" <> OLD."Id" OR NEW."ProjectId" <> OLD."ProjectId" OR NEW."Revision" <> OLD."Revision" + 1 THEN
            RAISE EXCEPTION 'invalid_review_transition' USING ERRCODE = '23514'; END IF;
        IF NOT ((NEW."CurrentVersion" = OLD."CurrentVersion" + 1 AND NEW."State" = 'PendingReview') OR
            (NEW."CurrentVersion" = OLD."CurrentVersion" AND OLD."State" = 'PendingReview' AND NEW."State" IN ('Approved','Rejected','ChangesRequested'))) THEN
            RAISE EXCEPTION 'invalid_review_transition' USING ERRCODE = '23514'; END IF;
    END IF;
    RETURN NEW;
END;
$$;
REVOKE ALL ON FUNCTION review.guard_case_transition() FROM PUBLIC;
CREATE TRIGGER case_transition BEFORE INSERT OR UPDATE ON review."ReviewCases" FOR EACH ROW EXECUTE FUNCTION review.guard_case_transition();

CREATE FUNCTION review.check_case_decision() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
BEGIN
    IF NEW."State" <> 'PendingReview' AND NOT EXISTS (
        SELECT 1 FROM review."DecisionEvents" d JOIN review."CommandReceipts" r ON r."DecisionId" = d."Id"
        WHERE d."CaseId" = NEW."Id" AND d."ProjectId" = NEW."ProjectId" AND d."Version" = NEW."CurrentVersion"
          AND d."ExpectedRevision" = OLD."Revision" AND d."ResultRevision" = NEW."Revision" AND d."ResultState" = NEW."State") THEN
        RAISE EXCEPTION 'review_decision_integrity' USING ERRCODE = '23514';
    END IF;
    RETURN NULL;
END;
$$;
REVOKE ALL ON FUNCTION review.check_case_decision() FROM PUBLIC;
CREATE CONSTRAINT TRIGGER case_decision_complete AFTER UPDATE ON review."ReviewCases"
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION review.check_case_decision();

CREATE FUNCTION review.check_decision_complete() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM review."ReviewCases" c JOIN review."CommandReceipts" r ON r."DecisionId" = NEW."Id"
        WHERE c."Id" = NEW."CaseId" AND c."ProjectId" = NEW."ProjectId" AND c."CurrentVersion" = NEW."Version"
          AND c."Revision" = NEW."ResultRevision" AND c."State" = NEW."ResultState") THEN
        RAISE EXCEPTION 'review_decision_integrity' USING ERRCODE = '23514';
    END IF;
    RETURN NULL;
END;
$$;
REVOKE ALL ON FUNCTION review.check_decision_complete() FROM PUBLIC;
CREATE CONSTRAINT TRIGGER decision_complete AFTER INSERT ON review."DecisionEvents"
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION review.check_decision_complete();
CREATE TRIGGER decisions_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON review."DecisionEvents"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();
CREATE TRIGGER receipts_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON review."CommandReceipts"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();
""";
}
