-- Run explicitly as migration owner AFTER migrations. Provision this LOGIN separately.
REVOKE ALL ON SCHEMA review FROM PUBLIC;
GRANT USAGE ON SCHEMA review TO factory_review_writer;
GRANT SELECT, INSERT ON review."ReviewCases" TO factory_review_writer;
GRANT UPDATE ("CurrentVersion", "Revision", "State") ON review."ReviewCases" TO factory_review_writer;
GRANT SELECT, INSERT ON review."ArtifactVersions", review."PublicationEvents" TO factory_review_writer;
-- No UPDATE/DELETE/TRUNCATE or schema ownership on immutable records; no DDL grants.
