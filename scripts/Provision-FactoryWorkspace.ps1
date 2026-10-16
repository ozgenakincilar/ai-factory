param([Parameter(Mandatory)][string]$Database, [string]$Project = 'factory-workspace', [string]$BinaryDirectory = "$PSScriptRoot/../artifacts/postgres/distribution/pgsql/bin")
# Operator-only: makes the local Keycloak human (factory-reviewer) a Generator/Reviewer of one
# project in an initialized isolated database, Owner of its organization, with a USD 0.25 lifetime
# project budget. Idempotent; existing rows are never changed or reactivated.
$ErrorActionPreference = 'Stop'
if ($Database -cnotmatch '\Afactory_[A-Za-z0-9_]+\z' -or $Database -eq 'factory_review_5a') { throw 'Invalid isolated database name.' }
if ($Project -cnotmatch '\A[a-z0-9-]{3,60}\z') { throw 'Invalid project id.' }
$repo = [IO.Path]::GetFullPath("$PSScriptRoot/..")
$db = Get-Content "$repo/artifacts/review-postgres/connection.json" -Raw | ConvertFrom-Json
$identity = Get-Content "$repo/artifacts/keycloak/development.json" -Raw | ConvertFrom-Json
$human = ([Guid]::Parse($identity.HumanSubject)).ToString()
$org = "$Project-org"
$oldPassword = $env:PGPASSWORD
try {
    $env:PGPASSWORD = $db.OwnerPassword
    @"
BEGIN;
INSERT INTO access."Projects" ("Id","IsActive","Revision") VALUES ('$Project',true,1) ON CONFLICT DO NOTHING;
INSERT INTO access."Principals" ("Id","Issuer","Subject","Kind","IsActive","Revision")
    VALUES ('$human','http://127.0.0.1:58080/realms/factory-dev','$human','Human',true,1) ON CONFLICT DO NOTHING;
-- An existing principal for this issuer/subject keeps its own id.
CREATE TEMP TABLE me ON COMMIT DROP AS SELECT "Id" FROM access."Principals"
    WHERE "Issuer"='http://127.0.0.1:58080/realms/factory-dev' AND "Subject"='$human' AND "Kind"='Human';
INSERT INTO access."ProjectMemberships" ("ProjectId","PrincipalId","PrincipalKind","Role","IsActive","Revision")
    SELECT '$Project',me."Id",'Human',r,true,1 FROM me CROSS JOIN (VALUES ('Generator'),('Reviewer')) v(r) ON CONFLICT DO NOTHING;
INSERT INTO tenancy."Organizations" ("Id","Name","IsActive","Revision") VALUES ('$org','Local workspace',true,1) ON CONFLICT DO NOTHING;
INSERT INTO tenancy."ProjectOwnership" ("ProjectId","OrganizationId") VALUES ('$Project','$org') ON CONFLICT DO NOTHING;
INSERT INTO tenancy."OrganizationMemberships" ("OrganizationId","PrincipalId","PrincipalKind","Role","IsActive","Revision")
    SELECT '$org',me."Id",'Human','Owner',true,1 FROM me ON CONFLICT DO NOTHING;
INSERT INTO orchestration."Budgets" ("Id","Scope","ScopeId","Period","LimitMicros","EffectiveFrom","Source")
    SELECT gen_random_uuid(),'Project','$Project','Lifetime',250000,clock_timestamp(),'Local workspace cap USD 0.25'
    WHERE NOT EXISTS (SELECT 1 FROM orchestration."Budgets" WHERE "Scope"='Project' AND "ScopeId"='$Project');
COMMIT;
"@ | & (Join-Path $BinaryDirectory 'psql.exe') -h 127.0.0.1 -p $db.Port -U factory_owner -d $Database -v ON_ERROR_STOP=1 -q -f - | Out-Null
    if ($LASTEXITCODE) { throw 'Workspace provisioning failed.' }
    Write-Output "Workspace project '$Project' provisioned in $Database for the local Keycloak human."
} finally { $env:PGPASSWORD = $oldPassword }
