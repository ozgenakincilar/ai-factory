-- Versioned M6 migration. Never edit after release. Owner provisioning is separate.
LOCK TABLE review."ReviewCases" IN ACCESS EXCLUSIVE MODE;
-- A constant missing-value default classifies the locked historical cohort without UPDATE,
-- without firing case transition triggers, and without touching bytes/revisions/audit.
ALTER TABLE review."ReviewCases" ADD COLUMN "ManagementMode" text NOT NULL DEFAULT 'Legacy';
ALTER TABLE review."ReviewCases" ALTER COLUMN "ManagementMode" SET DEFAULT 'Managed';
ALTER TABLE review."ReviewCases" ADD CONSTRAINT case_management_mode CHECK ("ManagementMode" IN ('Legacy','Managed'));
CREATE SCHEMA lifecycle;
REVOKE ALL ON SCHEMA lifecycle FROM PUBLIC;

CREATE TABLE lifecycle."WorkItems" (
    "Id" uuid PRIMARY KEY, "ProjectId" varchar(200) NOT NULL REFERENCES access."Projects"("Id"),
    "CreatorId" uuid NOT NULL REFERENCES access."Principals"("Id"),
    "Revision" bigint NOT NULL CHECK ("Revision">0), "AttemptNumber" bigint NOT NULL CHECK ("AttemptNumber">0),
    "ActiveRunId" uuid, "LatestRunId" uuid NOT NULL,
    UNIQUE ("Id","ProjectId")
);
CREATE TABLE lifecycle."GenerationCommands" (
    "RunId" uuid PRIMARY KEY, "WorkItemId" uuid NOT NULL, "ProjectId" varchar(200) NOT NULL,
    "PrincipalId" uuid NOT NULL REFERENCES access."Principals"("Id"),
    "Kind" text NOT NULL CHECK ("Kind" IN ('Create','Generate')), "CommandVersion" integer NOT NULL DEFAULT 1 CHECK ("CommandVersion"=1),
    "Key" uuid NOT NULL, "Fingerprint" varchar(64) NOT NULL, "InputHash" varchar(64) NOT NULL,
    "TokenHash" bytea NOT NULL CHECK (octet_length("TokenHash")=32),
    "AttemptNumber" bigint NOT NULL CHECK ("AttemptNumber">0), "Deadline" timestamptz NOT NULL,
    "CaseId" uuid NOT NULL, "NextVersion" integer NOT NULL CHECK ("NextVersion">0), "ExpectedRevision" bigint NOT NULL CHECK ("ExpectedRevision">=0),
    "Status" text NOT NULL CHECK ("Status" IN ('Running','Published','GenerationFailed','Cancelled','PublicationFailed','Abandoned')),
    "HttpStatus" integer NOT NULL, "FailureCode" text, "ContentHash" varchar(64), "ResultRevision" bigint,
    UNIQUE ("ProjectId","PrincipalId","Kind","CommandVersion","Key"),
    UNIQUE ("WorkItemId","RunId"),
    FOREIGN KEY ("WorkItemId","ProjectId") REFERENCES lifecycle."WorkItems"("Id","ProjectId") DEFERRABLE INITIALLY DEFERRED,
    CHECK (("Status"='Published' AND "ContentHash" IS NOT NULL AND "ResultRevision" IS NOT NULL AND "ResultRevision"="ExpectedRevision"+1 AND "FailureCode" IS NULL)
        OR ("Status"<>'Published' AND "ContentHash" IS NULL AND "ResultRevision" IS NULL)),
    CHECK (("Status"='Running' AND "HttpStatus"=202 AND "FailureCode" IS NULL) OR
        ("Status"='Published' AND "HttpStatus" IN (200,201)) OR
        ("Status" IN ('GenerationFailed','Cancelled','PublicationFailed','Abandoned') AND "FailureCode" IS NOT NULL AND "HttpStatus">=400))
);
CREATE UNIQUE INDEX one_active_attempt ON lifecycle."GenerationCommands"("WorkItemId") WHERE "Status"='Running';
CREATE UNIQUE INDEX one_command_publication ON lifecycle."GenerationCommands"("CaseId","NextVersion") WHERE "Status"='Published';
ALTER TABLE lifecycle."WorkItems" ADD CONSTRAINT work_item_active FOREIGN KEY ("Id","ActiveRunId")
    REFERENCES lifecycle."GenerationCommands"("WorkItemId","RunId") DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE lifecycle."WorkItems" ADD CONSTRAINT work_item_latest FOREIGN KEY ("Id","LatestRunId")
    REFERENCES lifecycle."GenerationCommands"("WorkItemId","RunId") DEFERRABLE INITIALLY DEFERRED;
CREATE TABLE review."ReviewCaseLineage" (
    "CaseId" uuid PRIMARY KEY, "WorkItemId" uuid NOT NULL UNIQUE, "ProjectId" varchar(200) NOT NULL,
    FOREIGN KEY ("CaseId","ProjectId") REFERENCES review."ReviewCases"("Id","ProjectId"),
    FOREIGN KEY ("WorkItemId","ProjectId") REFERENCES lifecycle."WorkItems"("Id","ProjectId")
);
CREATE TRIGGER lineage_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON review."ReviewCaseLineage"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();
CREATE TRIGGER commands_no_delete BEFORE DELETE OR TRUNCATE ON lifecycle."GenerationCommands"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();
CREATE TRIGGER work_items_no_delete BEFORE DELETE OR TRUNCATE ON lifecycle."WorkItems"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();

CREATE FUNCTION lifecycle.text_bytes(p text) RETURNS bytea LANGUAGE sql IMMUTABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT CASE WHEN p IS NULL THEN pg_catalog.int4send(-1)
        ELSE pg_catalog.int4send(pg_catalog.octet_length(pg_catalog.convert_to(p,'UTF8'))) || pg_catalog.convert_to(p,'UTF8') END;
$$;
CREATE FUNCTION lifecycle.input_bytes(p jsonb) RETURNS bytea LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog,pg_temp AS $$
DECLARE result bytea; source jsonb; sources jsonb:=p->'confirmedRequirements';
BEGIN
    IF pg_catalog.jsonb_typeof(p) IS DISTINCT FROM 'object' OR pg_catalog.jsonb_typeof(p->'task') IS DISTINCT FROM 'string'
        OR pg_catalog.jsonb_typeof(p->'context') IS DISTINCT FROM 'string' THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    result:=lifecycle.text_bytes(p->>'task') || lifecycle.text_bytes(p->>'context');
    IF sources IS NULL OR sources='null'::jsonb THEN RETURN result || pg_catalog.int4send(-1); END IF;
    IF pg_catalog.jsonb_typeof(sources)<>'array' THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    result:=result || pg_catalog.int4send(pg_catalog.jsonb_array_length(sources));
    FOR source IN SELECT value FROM pg_catalog.jsonb_array_elements(sources) LOOP
        IF pg_catalog.jsonb_typeof(source->'id') IS DISTINCT FROM 'string' OR pg_catalog.jsonb_typeof(source->'description') IS DISTINCT FROM 'string'
            THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
        result:=result || lifecycle.text_bytes(source->>'id') || lifecycle.text_bytes(source->>'description');
    END LOOP;
    RETURN result;
END;
$$;
CREATE FUNCTION lifecycle.input_hash(p_project text,p jsonb) RETURNS text LANGUAGE sql IMMUTABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to('UAF-M6-INPUT','UTF8') || pg_catalog.int4send(1)
        || lifecycle.text_bytes(p_project) || lifecycle.input_bytes(p)),'hex');
$$;
CREATE FUNCTION lifecycle.fingerprint(p_project text,p_work uuid,p_tag text,p_version text,p jsonb) RETURNS text
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to('UAF-M6-COMMAND','UTF8') || pg_catalog.int4send(1) || pg_catalog.int4send(1)
        || lifecycle.text_bytes(CASE WHEN p_work IS NULL THEN 'Create' ELSE 'Generate' END) || lifecycle.text_bytes(p_version)
        || lifecycle.text_bytes(p_project) || lifecycle.text_bytes(pg_catalog.replace(p_work::text,'-','')) || lifecycle.text_bytes(p_tag)
        || lifecycle.input_bytes(p)),'hex');
$$;

CREATE FUNCTION lifecycle.lock_actor(p_project text,p_issuer text,p_subject text,p_kind text,p_read boolean DEFAULT false) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE actor access."Principals"%ROWTYPE; member access."ProjectMemberships"%ROWTYPE;
BEGIN
    PERFORM 1 FROM access."Projects" WHERE "Id"=p_project AND "IsActive" FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION 'forbidden' USING ERRCODE='42501'; END IF;
    SELECT * INTO actor FROM access."Principals" WHERE "Issuer"=p_issuer AND "Subject"=p_subject AND "Kind"=p_kind AND "IsActive" FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION 'forbidden' USING ERRCODE='42501'; END IF;
    SELECT * INTO member FROM access."ProjectMemberships" WHERE "ProjectId"=p_project AND "PrincipalId"=actor."Id" AND "PrincipalKind"=p_kind
        AND "IsActive" AND ("Role"='Generator' OR (p_read AND p_kind='Human' AND "Role"='Reviewer')) ORDER BY "Role" LIMIT 1 FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION 'forbidden' USING ERRCODE='42501'; END IF;
    RETURN actor."Id";
END;
$$;

CREATE FUNCTION lifecycle.outcome(p lifecycle."GenerationCommands") RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT pg_catalog.jsonb_build_object('workItemId',p."WorkItemId",'runId',p."RunId",'status',p."Status",'httpStatus',p."HttpStatus",'failureCode',p."FailureCode",
        'publication',CASE WHEN p."Status"='Published' THEN pg_catalog.jsonb_build_object('caseId',p."CaseId",'version',p."NextVersion",'revision',p."ResultRevision",'contentHash',p."ContentHash") ELSE NULL END);
$$;
CREATE FUNCTION lifecycle.snapshot(p_project text,p_id uuid) RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT pg_catalog.jsonb_build_object('workItemId',w."Id",'projectId',w."ProjectId",'revision',w."Revision",'activeRunId',w."ActiveRunId",
        'latestRunId',w."LatestRunId",'latestStatus',g."Status",'caseId',c."Id",'caseRevision',COALESCE(c."Revision",0),
        'artifactVersion',c."CurrentVersion",'contentHash',a."ContentHash",'reviewState',c."State")
    FROM lifecycle."WorkItems" w JOIN lifecycle."GenerationCommands" g ON g."RunId"=w."LatestRunId"
    LEFT JOIN review."ReviewCaseLineage" l ON l."WorkItemId"=w."Id"
    LEFT JOIN review."ReviewCases" c ON c."Id"=l."CaseId"
    LEFT JOIN review."ArtifactVersions" a ON a."CaseId"=c."Id" AND a."Version"=c."CurrentVersion"
    WHERE w."Id"=p_id AND w."ProjectId"=p_project;
$$;
CREATE FUNCTION lifecycle.etag(p jsonb) RETURNS text LANGUAGE sql IMMUTABLE SET search_path=pg_catalog,pg_temp AS $$
    SELECT '"wi-v1:' || pg_catalog.replace(p->>'workItemId','-','') || ':' || (p->>'revision') || ':' ||
        COALESCE(pg_catalog.replace(p->>'caseId','-',''),'none') || ':' || (p->>'caseRevision') || '"';
$$;

CREATE FUNCTION lifecycle.guard_case_mode() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    IF TG_OP='INSERT' THEN
        IF NEW."ManagementMode"<>'Managed' OR current_user<>'factory_lifecycle_guard' THEN RAISE EXCEPTION 'managed_publication_required' USING ERRCODE='23514'; END IF;
    ELSE
        IF NEW."ManagementMode"<>OLD."ManagementMode" THEN RAISE EXCEPTION 'immutable_case_mode' USING ERRCODE='23514'; END IF;
        IF NEW."ManagementMode"='Managed' AND NEW."CurrentVersion"<>OLD."CurrentVersion" AND current_user<>'factory_lifecycle_guard'
            THEN RAISE EXCEPTION 'managed_publication_required' USING ERRCODE='23514'; END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER case_management BEFORE INSERT OR UPDATE ON review."ReviewCases" FOR EACH ROW EXECUTE FUNCTION lifecycle.guard_case_mode();
CREATE FUNCTION lifecycle.guard_managed_insert() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM review."ReviewCases" WHERE "Id"=NEW."CaseId" AND "ManagementMode"='Managed') AND current_user<>'factory_lifecycle_guard'
        THEN RAISE EXCEPTION 'managed_publication_required' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER managed_artifact BEFORE INSERT ON review."ArtifactVersions" FOR EACH ROW EXECUTE FUNCTION lifecycle.guard_managed_insert();
CREATE TRIGGER managed_event BEFORE INSERT ON review."PublicationEvents" FOR EACH ROW EXECUTE FUNCTION lifecycle.guard_managed_insert();

CREATE FUNCTION lifecycle.guard_command() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    IF TG_OP='INSERT' THEN
        IF NEW."Status"<>'Running' THEN RAISE EXCEPTION 'invalid_command_transition' USING ERRCODE='23514'; END IF;
    ELSE
        IF OLD."Status"<>'Running' OR NEW."Status"='Running' OR
            (pg_catalog.to_jsonb(NEW)-ARRAY['Status','HttpStatus','FailureCode','ContentHash','ResultRevision']) IS DISTINCT FROM
            (pg_catalog.to_jsonb(OLD)-ARRAY['Status','HttpStatus','FailureCode','ContentHash','ResultRevision'])
            THEN RAISE EXCEPTION 'invalid_command_transition' USING ERRCODE='23514'; END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER command_transition BEFORE INSERT OR UPDATE ON lifecycle."GenerationCommands" FOR EACH ROW EXECUTE FUNCTION lifecycle.guard_command();
CREATE FUNCTION lifecycle.guard_work_item() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    IF NEW."Id"<>OLD."Id" OR NEW."ProjectId"<>OLD."ProjectId" OR NEW."CreatorId"<>OLD."CreatorId" OR NEW."Revision"<>OLD."Revision"+1
        OR NEW."AttemptNumber" NOT IN (OLD."AttemptNumber",OLD."AttemptNumber"+1)
        THEN RAISE EXCEPTION 'invalid_work_item_transition' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER work_item_transition BEFORE UPDATE ON lifecycle."WorkItems" FOR EACH ROW EXECUTE FUNCTION lifecycle.guard_work_item();

CREATE FUNCTION lifecycle.check_integrity() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE wid uuid; cid uuid; g lifecycle."GenerationCommands"%ROWTYPE; w lifecycle."WorkItems"%ROWTYPE; a review."ArtifactVersions"%ROWTYPE; e jsonb;
BEGIN
    -- Validate this transition, not all historical publications against today's case state.
    -- A missing ReviewCases UPDATE must not prevent this independent check from firing.
    IF TG_TABLE_NAME='GenerationCommands' AND TG_OP='UPDATE' THEN
        IF NEW."Status"='Published' AND NOT EXISTS (
            SELECT 1 FROM review."ReviewCases" c
            JOIN review."ReviewCaseLineage" l ON l."CaseId"=c."Id" AND l."ProjectId"=c."ProjectId"
            JOIN review."ArtifactVersions" av ON av."CaseId"=c."Id" AND av."Version"=c."CurrentVersion"
            JOIN review."PublicationEvents" pe ON pe."CaseId"=av."CaseId" AND pe."Version"=av."Version" AND pe."ContentHash"=av."ContentHash"
            WHERE c."ManagementMode"='Managed' AND c."Id"=NEW."CaseId" AND c."ProjectId"=NEW."ProjectId"
                AND l."WorkItemId"=NEW."WorkItemId" AND c."CurrentVersion"=NEW."NextVersion"
                AND c."Revision"=NEW."ResultRevision" AND NEW."ResultRevision"=NEW."ExpectedRevision"+1
                AND c."State"='PendingReview' AND av."ContentHash"=NEW."ContentHash" AND av."WorkflowId"=NEW."RunId")
            THEN RAISE EXCEPTION 'lifecycle_publication_integrity' USING ERRCODE='23514'; END IF;
    END IF;
    IF TG_TABLE_NAME='WorkItems' THEN wid:=NEW."Id";
    ELSIF TG_TABLE_NAME='GenerationCommands' OR TG_TABLE_NAME='ReviewCaseLineage' THEN wid:=NEW."WorkItemId";
    ELSE
        IF TG_TABLE_NAME='ReviewCases' THEN cid:=NEW."Id"; ELSE cid:=NEW."CaseId"; END IF;
        IF NOT EXISTS (SELECT 1 FROM review."ReviewCases" WHERE "Id"=cid AND "ManagementMode"='Managed') THEN RETURN NULL; END IF;
        SELECT "WorkItemId" INTO wid FROM review."ReviewCaseLineage" WHERE "CaseId"=cid;
        IF wid IS NULL THEN RAISE EXCEPTION 'lifecycle_integrity' USING ERRCODE='23514'; END IF;
    END IF;
    SELECT * INTO STRICT w FROM lifecycle."WorkItems" WHERE "Id"=wid;
    SELECT * INTO STRICT g FROM lifecycle."GenerationCommands" WHERE "RunId"=w."LatestRunId";
    IF g."WorkItemId"<>w."Id" OR g."AttemptNumber"<>w."AttemptNumber" OR
        (g."Status"='Running' AND w."ActiveRunId" IS DISTINCT FROM g."RunId") OR
        (g."Status"<>'Running' AND w."ActiveRunId" IS NOT NULL) OR
        EXISTS(SELECT 1 FROM lifecycle."GenerationCommands" x WHERE x."WorkItemId"=wid AND x."Status"='Running' AND x."RunId"<>g."RunId")
        THEN RAISE EXCEPTION 'lifecycle_integrity' USING ERRCODE='23514'; END IF;
    IF EXISTS (SELECT 1 FROM review."ReviewCaseLineage" l JOIN review."ReviewCases" c ON c."Id"=l."CaseId"
        WHERE l."WorkItemId"=wid AND (c."ManagementMode"<>'Managed' OR l."ProjectId"<>w."ProjectId"))
        THEN RAISE EXCEPTION 'lifecycle_integrity' USING ERRCODE='23514'; END IF;
    FOR a IN SELECT av.* FROM review."ArtifactVersions" av JOIN review."ReviewCaseLineage" l ON l."CaseId"=av."CaseId" WHERE l."WorkItemId"=wid LOOP
        SELECT * INTO g FROM lifecycle."GenerationCommands" WHERE "CaseId"=a."CaseId" AND "NextVersion"=a."Version" AND "Status"='Published';
        IF NOT FOUND OR g."WorkItemId"<>wid OR g."ProjectId"<>w."ProjectId" OR g."RunId"<>a."WorkflowId" OR g."ContentHash"<>a."ContentHash"
            OR NOT EXISTS(SELECT 1 FROM review."PublicationEvents" WHERE "CaseId"=a."CaseId" AND "Version"=a."Version" AND "ContentHash"=a."ContentHash")
            THEN RAISE EXCEPTION 'lifecycle_integrity' USING ERRCODE='23514'; END IF;
        e:=pg_catalog.convert_from(a."Content",'UTF8')::jsonb;
        IF e->>'workflowId' IS DISTINCT FROM g."RunId"::text OR e->>'caseId' IS DISTINCT FROM a."CaseId"::text
            OR (e->>'version')::integer IS DISTINCT FROM a."Version" OR e->'request'->>'projectId' IS DISTINCT FROM w."ProjectId"
            OR lifecycle.input_hash(w."ProjectId",e->'request') IS DISTINCT FROM g."InputHash"
            THEN RAISE EXCEPTION 'lifecycle_integrity' USING ERRCODE='23514'; END IF;
    END LOOP;
    IF EXISTS (SELECT 1 FROM lifecycle."GenerationCommands" x WHERE x."WorkItemId"=wid AND x."Status"='Published' AND NOT EXISTS
        (SELECT 1 FROM review."ReviewCaseLineage" l JOIN review."ArtifactVersions" av ON av."CaseId"=l."CaseId"
        WHERE l."WorkItemId"=wid AND av."CaseId"=x."CaseId" AND av."Version"=x."NextVersion" AND av."ContentHash"=x."ContentHash" AND av."WorkflowId"=x."RunId"))
        THEN RAISE EXCEPTION 'lifecycle_integrity' USING ERRCODE='23514'; END IF;
    IF TG_TABLE_NAME='ReviewCases' THEN
      IF TG_OP='INSERT' OR NEW."CurrentVersion"<>OLD."CurrentVersion" THEN
        IF NOT EXISTS(SELECT 1 FROM lifecycle."GenerationCommands" x WHERE x."CaseId"=NEW."Id" AND x."NextVersion"=NEW."CurrentVersion"
            AND x."Status"='Published' AND x."ResultRevision"=NEW."Revision" AND NEW."State"='PendingReview')
            THEN RAISE EXCEPTION 'lifecycle_integrity' USING ERRCODE='23514'; END IF;
      END IF;
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER lifecycle_work_item AFTER INSERT OR UPDATE ON lifecycle."WorkItems" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION lifecycle.check_integrity();
CREATE CONSTRAINT TRIGGER lifecycle_command AFTER INSERT OR UPDATE ON lifecycle."GenerationCommands" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION lifecycle.check_integrity();
CREATE CONSTRAINT TRIGGER lifecycle_lineage AFTER INSERT ON review."ReviewCaseLineage" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION lifecycle.check_integrity();
CREATE CONSTRAINT TRIGGER lifecycle_case AFTER INSERT OR UPDATE ON review."ReviewCases" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION lifecycle.check_integrity();
CREATE CONSTRAINT TRIGGER lifecycle_artifact AFTER INSERT ON review."ArtifactVersions" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION lifecycle.check_integrity();
CREATE CONSTRAINT TRIGGER lifecycle_event AFTER INSERT ON review."PublicationEvents" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION lifecycle.check_integrity();

-- Functions below are the only runtime write surface. Ownership/grants are explicit operator work.
CREATE FUNCTION lifecycle.admit(p_project text,p_issuer text,p_subject text,p_kind text,p_key uuid,p_work uuid,p_tag text,p_version text,p_input jsonb,p_token bytea)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE actor uuid; fp text; kind text; g lifecycle."GenerationCommands"%ROWTYPE; w lifecycle."WorkItems"%ROWTYPE; snap jsonb;
    wid uuid; rid uuid; cid uuid; expected bigint:=0; ver integer:=1; replay boolean:=false;
BEGIN
    actor:=lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind);
    IF p_key IS NULL OR p_key='00000000-0000-0000-0000-000000000000'::uuid OR pg_catalog.octet_length(p_token)<>32 OR p_token IS NULL
        OR p_version IS DISTINCT FROM 'analysis-architecture-v1' OR (p_work IS NULL AND p_tag IS NOT NULL)
        OR p_input->>'projectId' IS DISTINCT FROM p_project OR pg_catalog.length(pg_catalog.btrim(p_input->>'task'))=0
        OR pg_catalog.octet_length(lifecycle.input_bytes(p_input))>65536 THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    kind:=CASE WHEN p_work IS NULL THEN 'Create' ELSE 'Generate' END;
    fp:=lifecycle.fingerprint(p_project,p_work,p_tag,p_version,p_input);
    -- Transaction advisory locks bound admission concurrency across API processes. They never span generation.
    -- A fixed global admission lock also makes the database-wide four-running-attempt cap atomic.
    PERFORM pg_catalog.pg_advisory_xact_lock(6042001);
    SELECT * INTO g FROM lifecycle."GenerationCommands" WHERE "ProjectId"=p_project AND "PrincipalId"=actor AND "Kind"=kind AND "CommandVersion"=1 AND "Key"=p_key;
    IF FOUND THEN
        IF g."Fingerprint"<>fp THEN RAISE EXCEPTION 'command_conflict' USING ERRCODE='P6009'; END IF;
        replay:=true;
    ELSE
        wid:=COALESCE(p_work,pg_catalog.gen_random_uuid()); rid:=pg_catalog.gen_random_uuid(); cid:=pg_catalog.gen_random_uuid();
        IF p_work IS NOT NULL THEN
            SELECT * INTO w FROM lifecycle."WorkItems" WHERE "Id"=wid AND "ProjectId"=p_project FOR UPDATE;
            IF NOT FOUND THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
            SELECT l."CaseId" INTO cid FROM review."ReviewCaseLineage" l WHERE l."WorkItemId"=wid;
            IF cid IS NOT NULL THEN PERFORM 1 FROM review."ReviewCases" WHERE "Id"=cid FOR SHARE; END IF;
            snap:=lifecycle.snapshot(p_project,wid);
            IF p_tag IS DISTINCT FROM lifecycle.etag(snap) THEN RAISE EXCEPTION 'stale_revision' USING ERRCODE='P6012'; END IF;
            expected:=(snap->>'caseRevision')::bigint; ver:=COALESCE((snap->>'artifactVersion')::integer,0)+1; cid:=COALESCE(cid,pg_catalog.gen_random_uuid());
            IF w."ActiveRunId" IS NOT NULL THEN
                SELECT * INTO g FROM lifecycle."GenerationCommands" WHERE "RunId"=w."ActiveRunId" FOR UPDATE;
                IF g."Deadline">pg_catalog.clock_timestamp() THEN RAISE EXCEPTION 'active_command' USING ERRCODE='P6009'; END IF;
                UPDATE lifecycle."GenerationCommands" SET "Status"='Abandoned',"HttpStatus"=409,"FailureCode"='generation_abandoned' WHERE "RunId"=g."RunId";
            END IF;
        END IF;
        -- Expired Running commands still consume capacity until explicitly abandoned; no automatic scheduling/sweeper.
        IF (SELECT count(*) FROM lifecycle."GenerationCommands" WHERE "Status"='Running')>=4 THEN RAISE EXCEPTION 'capacity' USING ERRCODE='P6029'; END IF;
        INSERT INTO lifecycle."GenerationCommands" ("RunId","WorkItemId","ProjectId","PrincipalId","Kind","Key","Fingerprint","InputHash","TokenHash","AttemptNumber","Deadline","CaseId","NextVersion","ExpectedRevision","Status","HttpStatus")
            VALUES (rid,wid,p_project,actor,kind,p_key,fp,lifecycle.input_hash(p_project,p_input),pg_catalog.sha256(p_token),COALESCE(w."AttemptNumber",0)+1,
                pg_catalog.clock_timestamp()+interval '180 seconds',cid,ver,expected,'Running',202) RETURNING * INTO g;
        IF p_work IS NULL THEN
            INSERT INTO lifecycle."WorkItems" VALUES(wid,p_project,actor,1,1,rid,rid);
        ELSE
            UPDATE lifecycle."WorkItems" SET "Revision"="Revision"+1,"AttemptNumber"="AttemptNumber"+1,"ActiveRunId"=rid,"LatestRunId"=rid WHERE "Id"=wid;
        END IF;
    END IF;
    RETURN pg_catalog.jsonb_build_object('outcome',lifecycle.outcome(g),'replayed',replay,'caseId',g."CaseId",'nextVersion',g."NextVersion",'expectedRevision',g."ExpectedRevision",'deadline',g."Deadline");
END;
$$;

CREATE FUNCTION lifecycle.publish(p_project text,p_issuer text,p_subject text,p_kind text,p_run uuid,p_token bytea,p_bytes bytea)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE actor uuid; g lifecycle."GenerationCommands"%ROWTYPE; wid uuid; c review."ReviewCases"%ROWTYPE; e jsonb; hash text; created timestamptz;
BEGIN
    actor:=lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind);
    SELECT "WorkItemId" INTO wid FROM lifecycle."GenerationCommands" WHERE "RunId"=p_run AND "ProjectId"=p_project AND "PrincipalId"=actor;
    IF wid IS NULL THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    PERFORM 1 FROM lifecycle."WorkItems" WHERE "Id"=wid FOR UPDATE;
    SELECT * INTO STRICT g FROM lifecycle."GenerationCommands" WHERE "RunId"=p_run FOR UPDATE;
    IF p_token IS NULL OR g."TokenHash" IS DISTINCT FROM pg_catalog.sha256(p_token) THEN RAISE EXCEPTION 'forbidden' USING ERRCODE='42501'; END IF;
    IF g."Status"='Published' THEN RETURN lifecycle.outcome(g); END IF;
    IF g."Status"<>'Running' OR NOT EXISTS(SELECT 1 FROM lifecycle."WorkItems" WHERE "Id"=wid AND "ActiveRunId"=p_run AND "AttemptNumber"=g."AttemptNumber")
        THEN RAISE EXCEPTION 'command_conflict' USING ERRCODE='P6009'; END IF;
    IF g."ExpectedRevision">0 THEN
        SELECT * INTO c FROM review."ReviewCases" WHERE "Id"=g."CaseId" AND "ProjectId"=p_project FOR UPDATE;
        IF NOT FOUND OR c."Revision"<>g."ExpectedRevision" THEN RAISE EXCEPTION 'stale_revision' USING ERRCODE='P6012'; END IF;
    END IF;
    IF g."Deadline"<=pg_catalog.clock_timestamp() THEN RAISE EXCEPTION 'expired' USING ERRCODE='P6010'; END IF;
    e:=pg_catalog.convert_from(p_bytes,'UTF8')::jsonb; hash:=pg_catalog.encode(pg_catalog.sha256(p_bytes),'hex'); created:=(e->>'createdAt')::timestamptz;
    IF e->>'workflowId' IS DISTINCT FROM p_run::text OR e->>'caseId' IS DISTINCT FROM g."CaseId"::text OR (e->>'version')::integer IS DISTINCT FROM g."NextVersion"
        OR (e->>'formatVersion')::integer IS DISTINCT FROM 1 OR e->>'validatorVersion' IS DISTINCT FROM 'milestone3-v1'
        OR e->'request'->>'projectId' IS DISTINCT FROM p_project OR lifecycle.input_hash(p_project,e->'request') IS DISTINCT FROM g."InputHash"
        THEN RAISE EXCEPTION 'lifecycle_integrity' USING ERRCODE='23514'; END IF;
    IF g."ExpectedRevision"=0 THEN
        INSERT INTO review."ReviewCases" ("Id","ProjectId","CurrentVersion","Revision","State") VALUES(g."CaseId",p_project,1,1,'PendingReview');
        INSERT INTO review."ReviewCaseLineage" VALUES(g."CaseId",wid,p_project);
    ELSE
        UPDATE review."ReviewCases" SET "CurrentVersion"="CurrentVersion"+1,"Revision"="Revision"+1,"State"='PendingReview' WHERE "Id"=g."CaseId";
    END IF;
    INSERT INTO review."ArtifactVersions" ("CaseId","Version","WorkflowId","Content","ContentHash","CreatedAt") VALUES(g."CaseId",g."NextVersion",p_run,p_bytes,hash,created);
    INSERT INTO review."PublicationEvents" ("Id","CaseId","Version","ContentHash","CreatedAt") VALUES(pg_catalog.gen_random_uuid(),g."CaseId",g."NextVersion",hash,created);
    UPDATE lifecycle."GenerationCommands" SET "Status"='Published',"HttpStatus"=CASE WHEN "Kind"='Create' THEN 201 ELSE 200 END,"ContentHash"=hash,"ResultRevision"="ExpectedRevision"+1 WHERE "RunId"=p_run RETURNING * INTO g;
    UPDATE lifecycle."WorkItems" SET "ActiveRunId"=NULL,"Revision"="Revision"+1 WHERE "Id"=wid;
    RETURN lifecycle.outcome(g);
END;
$$;

CREATE FUNCTION lifecycle.finish(p_project text,p_issuer text,p_subject text,p_kind text,p_run uuid,p_token bytea,p_status text,p_failure text)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE actor uuid; g lifecycle."GenerationCommands"%ROWTYPE; wid uuid; status_code integer;
BEGIN
    actor:=lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind);
    SELECT "WorkItemId" INTO wid FROM lifecycle."GenerationCommands" WHERE "RunId"=p_run AND "ProjectId"=p_project AND "PrincipalId"=actor;
    IF wid IS NULL THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    PERFORM 1 FROM lifecycle."WorkItems" WHERE "Id"=wid FOR UPDATE;
    SELECT * INTO STRICT g FROM lifecycle."GenerationCommands" WHERE "RunId"=p_run FOR UPDATE;
    IF p_token IS NULL OR g."TokenHash" IS DISTINCT FROM pg_catalog.sha256(p_token) THEN RAISE EXCEPTION 'forbidden' USING ERRCODE='42501'; END IF;
    IF g."Status"<>'Running' THEN RETURN lifecycle.outcome(g); END IF;
    IF NOT EXISTS(SELECT 1 FROM lifecycle."WorkItems" WHERE "Id"=wid AND "ActiveRunId"=p_run AND "AttemptNumber"=g."AttemptNumber") THEN RAISE EXCEPTION 'command_conflict' USING ERRCODE='P6009'; END IF;
    status_code:=CASE WHEN p_status='Cancelled' AND p_failure='generation_cancelled' THEN 499
        WHEN p_status='GenerationFailed' AND p_failure='generation_capacity' THEN 429
        WHEN p_status='GenerationFailed' AND p_failure IN ('generation_failed','generation_timeout','generation_invalid') THEN 502
        WHEN p_status='PublicationFailed' AND p_failure='publication_stale' THEN 412
        WHEN p_status='PublicationFailed' AND p_failure IN ('publication_failed','publication_integrity') THEN 500 ELSE NULL END;
    IF status_code IS NULL THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    UPDATE lifecycle."GenerationCommands" SET "Status"=p_status,"HttpStatus"=status_code,"FailureCode"=p_failure WHERE "RunId"=p_run RETURNING * INTO g;
    UPDATE lifecycle."WorkItems" SET "ActiveRunId"=NULL,"Revision"="Revision"+1 WHERE "Id"=wid;
    RETURN lifecycle.outcome(g);
END;
$$;
CREATE FUNCTION lifecycle.read_work_item(p_project text,p_issuer text,p_subject text,p_kind text,p_id uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE result jsonb;
BEGIN
    PERFORM lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind,true);
    result:=lifecycle.snapshot(p_project,p_id);
    IF result IS NULL THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    RETURN result;
END;
$$;
CREATE FUNCTION lifecycle.read_run(p_project text,p_issuer text,p_subject text,p_kind text,p_id uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE result jsonb;
BEGIN
    PERFORM lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind,true);
    SELECT lifecycle.outcome(g) INTO result FROM lifecycle."GenerationCommands" g WHERE "RunId"=p_id AND "ProjectId"=p_project;
    IF result IS NULL THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    RETURN result;
END;
$$;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA lifecycle FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA lifecycle FROM PUBLIC;
