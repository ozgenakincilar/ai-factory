-- Versioned M11.3 migration. Never edit after release. Owner provisioning is separate
-- (scripts/orchestration-runtime-grants.sql).

-- One pull request outcome per verified development job, written once by the worker that published
-- it. Opened rows carry the PR number, URL and the pushed head commit, which must equal the commit
-- recorded in the job's evidence; Failed rows carry a failure code. Rows never change.
CREATE TABLE orchestration."DevelopmentPullRequests" (
    "JobId" uuid PRIMARY KEY REFERENCES orchestration."DevelopmentJobs"("Id"),
    "Repository" varchar(140) NOT NULL CHECK ("Repository" ~ '^[A-Za-z0-9_.-]{1,39}/[A-Za-z0-9_.-]{1,100}$'),
    "Branch" varchar(200) NOT NULL CHECK ("Branch" ~ '^factory/[a-z0-9-]{1,180}$'),
    "Status" text NOT NULL CHECK ("Status" IN ('Opened','Failed')),
    "Number" integer CHECK ("Number">0),
    "Url" varchar(300),
    "HeadCommit" varchar(40) CHECK ("HeadCommit" ~ '^[0-9a-f]{40}$'),
    "FailureCode" varchar(64) CHECK ("FailureCode" ~ '^[a-z_]{1,64}$'),
    "CreatedAt" timestamptz NOT NULL,
    CHECK (("Status"='Opened') = ("Number" IS NOT NULL AND "Url" IS NOT NULL AND "HeadCommit" IS NOT NULL)),
    CHECK (("Status"='Failed') = ("FailureCode" IS NOT NULL))
);
CREATE TRIGGER development_pull_requests_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON orchestration."DevelopmentPullRequests"
    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();

-- Worker: the oldest verified job without a pull request outcome, with its exact evidence bytes.
CREATE FUNCTION orchestration.next_pull_request() RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
    SELECT pg_catalog.jsonb_build_object('jobId',j."Id",'evidence',pg_catalog.encode(e."Content",'base64'),'evidenceSha256',e."ContentSha256")
    FROM orchestration."DevelopmentJobs" j JOIN orchestration."DevelopmentEvidence" e ON e."JobId"=j."Id"
    WHERE j."Status"='Verified' AND NOT EXISTS (SELECT 1 FROM orchestration."DevelopmentPullRequests" p WHERE p."JobId"=j."Id")
    ORDER BY j."UpdatedAt", j."Id" LIMIT 1;
$$;

-- Records the outcome once. An opened pull request must point at this repository's pull URL and at
-- the head commit the evidence recorded.
CREATE FUNCTION orchestration.record_pull_request(p_job uuid,p_repository text,p_branch text,p_status text,p_number integer,
    p_url text,p_head text,p_failure text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE head text;
BEGIN
    SELECT pg_catalog.convert_from(e."Content",'UTF8')::jsonb->>'finalCommit' INTO head
        FROM orchestration."DevelopmentJobs" j JOIN orchestration."DevelopmentEvidence" e ON e."JobId"=j."Id"
        WHERE j."Id"=p_job AND j."Status"='Verified' FOR SHARE OF j;
    IF NOT FOUND THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    IF p_status IS NULL OR p_status NOT IN ('Opened','Failed')
        OR (p_status='Opened' AND (p_head IS DISTINCT FROM head OR p_url IS NULL
            OR pg_catalog.left(p_url, pg_catalog.length('https://github.com/'||p_repository||'/pull/')) <> 'https://github.com/'||p_repository||'/pull/'))
        THEN RAISE EXCEPTION 'invalid_command' USING ERRCODE='22023'; END IF;
    INSERT INTO orchestration."DevelopmentPullRequests" ("JobId","Repository","Branch","Status","Number","Url","HeadCommit","FailureCode","CreatedAt")
        VALUES (p_job,p_repository,p_branch,p_status,CASE WHEN p_status='Opened' THEN p_number END,CASE WHEN p_status='Opened' THEN p_url END,
            CASE WHEN p_status='Opened' THEN p_head END,CASE WHEN p_status='Failed' THEN p_failure END,pg_catalog.clock_timestamp());
EXCEPTION WHEN unique_violation THEN RAISE EXCEPTION 'conflict' USING ERRCODE='P6009';
END;
$$;

-- Reads now include the pull request outcome.
CREATE OR REPLACE FUNCTION orchestration.read_development(p_project text,p_issuer text,p_subject text,p_kind text,p_job uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE result jsonb;
BEGIN
    PERFORM lifecycle.lock_actor(p_project,p_issuer,p_subject,p_kind,true);
    SELECT pg_catalog.jsonb_build_object('job',orchestration.development_view(j),'evidenceSha256',e."ContentSha256",
            'pullRequest',(SELECT pg_catalog.jsonb_build_object('repository',p."Repository",'branch',p."Branch",'status',p."Status",
                'number',p."Number",'url',p."Url",'headCommit',p."HeadCommit",'failureCode',p."FailureCode",'createdAt',p."CreatedAt")
                FROM orchestration."DevelopmentPullRequests" p WHERE p."JobId"=j."Id")) INTO result
        FROM orchestration."DevelopmentJobs" j LEFT JOIN orchestration."DevelopmentEvidence" e ON e."JobId"=j."Id"
        WHERE j."Id"=p_job AND j."ProjectId"=p_project;
    IF result IS NULL THEN RAISE EXCEPTION 'not_found' USING ERRCODE='P6004'; END IF;
    RETURN result;
END;
$$;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA orchestration FROM PUBLIC;
