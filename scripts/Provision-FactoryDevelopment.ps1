param([string]$BinaryDirectory = "$PSScriptRoot/../artifacts/postgres/distribution/pgsql/bin")
# Operator-only provisioning of the two synthetic identities imported by Start-FactoryKeycloak.
# No API enrollment path. Existing grants are not silently changed or reactivated.
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/..")
$db=Get-Content "$repo/artifacts/review-postgres/connection.json" -Raw | ConvertFrom-Json
$identity=Get-Content "$repo/artifacts/keycloak/development.json" -Raw | ConvertFrom-Json
$human=([Guid]::Parse($identity.HumanSubject)).ToString(); $service=([Guid]::Parse($identity.ServiceSubject)).ToString()
$oldPassword=$env:PGPASSWORD
try {
    $env:PGPASSWORD=$db.OwnerPassword
    @"
BEGIN;
INSERT INTO access."Projects" ("Id","IsActive","Revision") VALUES ('factory-local',true,1) ON CONFLICT DO NOTHING;
INSERT INTO access."Principals" ("Id","Issuer","Subject","Kind","IsActive","Revision") VALUES
('$human','http://127.0.0.1:58080/realms/factory-dev','$human','Human',true,1),
('$service','http://127.0.0.1:58080/realms/factory-dev','$service','Service',true,1) ON CONFLICT DO NOTHING;
INSERT INTO access."ProjectMemberships" ("ProjectId","PrincipalId","PrincipalKind","Role","IsActive","Revision") VALUES
('factory-local','$human','Human','Generator',true,1),
('factory-local','$human','Human','Reviewer',true,1),
('factory-local','$service','Service','Generator',true,1) ON CONFLICT DO NOTHING;
COMMIT;
"@ | & (Join-Path $BinaryDirectory 'psql.exe') -h 127.0.0.1 -p $db.Port -U factory_owner -d factory_review_5a -v ON_ERROR_STOP=1 -f -
    if ($LASTEXITCODE) { throw 'Development project provisioning failed.' }
} finally { $env:PGPASSWORD=$oldPassword }
