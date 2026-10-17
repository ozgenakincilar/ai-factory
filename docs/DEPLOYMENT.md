# Deployment — M13A

Status: **M13A.1 implemented and live** on 2026-10-17 (deployment engine, Azure module, release packages,
operator commands). The StockFlow release of ozgenakincilar/stockflow#1 runs on Azure staging at
https://stock-flow-staging-9b228545.azurewebsites.net (France Central), deployed through two
human-approved plans. The bootstrap (`scripts/Initialize-AzureStaging.ps1`) was run by the user: the
agent's permission policy does not allow it to grant cloud roles.
Durable deployment jobs with approval in the Product Workspace are **M13A.2**; the Factory-managed hosting
path is **M13A.3**.

## Target and cost (user decisions: own subscription, France Central, USD 5/month)

Subscription: the user's Azure free trial (`FreeTrial_2014-09-01`, spending limit on, verified
2026-10-17). The user first chose West Europe; creating the state storage account there failed with
`RequestDisallowedByAzure` ("the selected region is currently not accepting new customers"). ARM preflight
validation (`az deployment group validate`, nothing created) of a storage account, an F1 Linux plan and a
B1MS PostgreSQL server then passed in Sweden Central, France Central, Italy North, Poland Central,
Switzerland North and Spain Central, and failed in West Europe (disallowed) and North Europe, Germany West
Central, UK South and Norway East (`SubscriptionIsOverQuotaForSku`). The user chose **France Central**;
the two empty West Europe resource groups created before the failure were deleted. One staging target
per application, all in France Central:

| Resource | SKU | Expected cost |
| --- | --- | --- |
| Linux App Service plan + web app (.NET 10) | F1 (Free) | USD 0 (60 CPU minutes/day, no always-on, cold starts) |
| Azure Database for PostgreSQL flexible server | B1MS, 32 GB | USD 0 for 12 months on a free account (750 h/month), otherwise about USD 13–15/month |
| Key Vault (connection string) | Standard | cents (per 10,000 operations) |
| State storage account (one container per target) | Standard LRS | cents |

A subscription-wide **USD 5 monthly budget** alerts at 80 % actual and 100 % forecast. The free trial's
spending limit stops charges when the credit ends; after the trial the subscription must be upgraded to
keep the free-account services (user decision when it comes to that).

## Pieces

- **Module** `deploy/azure/app-service-postgres` 1.0.0 and `-1.1.0` (current), registered with a content hash in
  `deploy/registry.json` and refused when changed (`FileSystemModuleCatalog`). Terraform **1.16.5**
  (image pinned by digest), azurerm **5.8.0**, random **3.9.1**; `.terraform.lock.hcl` pins the provider
  hashes. The database password is an ephemeral value written only through write-only arguments
  (`administrator_password_wo`, Key Vault `value_wo`), so it is never stored in a plan or the state. The
  web app reads the connection string as a Key Vault reference. Key Vault uses access policies because the
  deployment identity may not create role assignments. **1.1.0** resolves the reference with a
  user-assigned identity whose access policy exists before the web app is created: with 1.0.0's
  system-assigned identity the policy could only follow the app, App Service kept the first, denied
  resolution (`AccessToKeyVaultDenied`), and a restart did not clear it. 1.1.0 keeps every 1.0.0
  resource, so the upgrade destroys nothing.
- **Release package** (`SandboxReleaseBuilder`): in the offline sandbox, the evidence's base and final
  commits are re-created from the hash-verified template and the evidence diff and must equal the
  evidence ids; then `dotnet publish` of the API. The output is untrusted: only regular files with safe
  relative paths, at most 2,000 files and 200 MB, are put into a deterministic zip (ordinal order, fixed
  timestamps). The package hash names the uploaded file, so a new release always changes the plan.
- **Engine** (`TerraformDeploymentEngine` behind `IDeploymentEngine`): every Terraform command runs in a
  fresh container of the pinned image (non-root, read-only root, no capabilities, CPU/memory/PID limits,
  only the operation's working directory and the provider cache mounted). It needs the network. The
  deployment identity reaches Terraform as `ARM_*` variables forwarded by name, never on a command line,
  in files, plans, logs or evidence; output is additionally redacted. Init uses `-lockfile=readonly` and
  Entra ID access to the state (no storage keys).
- **Binding and approval** (`DeploymentRules`): one SHA-256 binds the exact plan bytes, the target, the
  module id, version and content hash, the development job, the release hash and source commit, and the
  module inputs. `apply` re-computes it, requires an approval of exactly that hash, re-reads the summary
  from the plan bytes, refuses destructive plans (any delete or replace) without an explicit
  destructive approval, and runs exactly the saved plan; Terraform itself refuses a saved plan whose
  state changed since (stale plan). The identity's tenant and subscription must equal the target's.
- **Health** (`HttpHealthChecker`): `GET https://<app>/health` must return 200 with `{"status":"ok"}`;
  up to 20 attempts 15 s apart (free-tier cold start).

## Operator flow (M13A.1)

1. Once per target, with the operator's own `az login` (subscription Owner):
   `scripts/Initialize-AzureStaging.ps1 -Application stock-flow -AlertEmail <address>` creates the target
   resource group `rg-stock-flow-staging-frc`, the state resource group, storage account and the
   container `tfstate-stock-flow-staging`, registers the resource providers, creates the service
   principal `factory-deploy-stock-flow-staging` with **Contributor on the target resource group and
   Storage Blob Data Contributor on that state container only**, writes its secret (expires after 30
   days) to the worker User Secrets through standard input, writes the non-secret target file
   `artifacts/deployments/targets/stock-flow-staging.json`, and creates the budget.
2. `scripts/Export-DevelopmentEvidence.ps1 -Database <db> -Job <id> -OutFile <file>` prints the evidence
   SHA-256 (or download it from `GET /api/projects/{id}/developments/{job}/evidence`; the ETag is the hash).
3. `scripts/Deploy-FactoryStaging.ps1 -Plan -Target <target.json> -Evidence <file> -EvidenceSha256 <sha>`
   builds the release, plans, saves `plan.tfplan`, `release.zip`, `plan.json` and `plan.log` under
   `artifacts/deployments/plans/<binding prefix>/`, and prints every planned change and the binding hash.
4. A human reads the plan and runs
   `scripts/Deploy-FactoryStaging.ps1 -Apply -PlanDirectory <dir> -Approve <binding sha> -ApprovedBy <name>`;
   the result and health check are written to `apply-<time>.json` next to the plan.

## Executed verification M13A.1 — 2026-10-17

1. Azure subscription inspected read-only after `az login`: Free Trial, spending limit on, enabled, no
   resource groups, resources or budgets; West Europe listed PostgreSQL flexible server Burstable
   (B1MS) and App Service Linux `DOTNETCORE|10.0`.
2. Terraform image pulled by tag and pinned by digest; `terraform validate` of the module (without the
   backend): valid; `terraform fmt -check`: clean after formatting; `providers lock -platform=linux_amd64`
   produced the committed lock file.
3. Unit tests: target validation, inputs, binding changes with every bound value, approval/tamper/stale
   checks, destructive plans, plan JSON parsing, release archive safety and determinism, module hash,
   container arguments without the secret, credentials never printed, unpinned image refused, wrong
   tenant refused before anything runs, health checks (cold start, wrong body, plain HTTP refused).
4. Docker test: a verified sample job's release is rebuilt from its evidence commits and published
   offline (`SampleShop.Api.dll` and the layer assemblies in the zip, no diff or `.git` files); a
   tampered diff is refused with `final_commit_mismatch` before anything is built.
5. The evidence of the live StockFlow job behind ozgenakincilar/stockflow#1 was exported with
   `Export-DevelopmentEvidence.ps1`; its SHA-256 equals the recorded one
   (`f60e31b2829460d404e11478a291ec602dcac4015546672ebf4b31367be284bf`).
6. Full suite with `-IncludeKeycloak -IncludeDocker` (fresh browser tokens): Keycloak pass **4 passed**,
   remaining pass **1024 passed, 0 failed, 1 skipped** (the paid live Developer Agent test).
7. **Bootstrap** (run by the user after three script fixes: a helper named `Az` recursed into itself
   because PowerShell names are case-insensitive; `az.cmd` runs through cmd.exe, which rejected a
   `length(@)` query; West Europe refused the storage account, see above): resource groups
   `rg-stock-flow-staging-frc` and `rg-factory-tfstate-frc`, storage account `stfactorytf084fd9cca72d`
   with container `tfstate-stock-flow-staging`, service principal `factory-deploy-stock-flow-staging`
   with exactly two role assignments (Contributor on the target resource group, Storage Blob Data
   Contributor on that container; checked with `az role assignment list`), secret until 2026-11-16, budget
   `factory-monthly` USD 5 (listed by `az consumption budget list`).
8. **Live plan 1** (module 1.0.0): release built from evidence commit `1bd942e` →
   `99d27dd27fe7d89d23b2fea3bd54a9750b54ad544731a3d89ebcc4825d0233e2` (2,520,439 bytes); state reached
   through Entra ID; 9 to add, 0 to change, 0 to destroy; write-only values shown only as
   "(write-only attribute)". Binding `0220be7bb2c1569b565c552130dc0038fdd518eb5b80aa1e03da6e96035ddb2d`
   approved by the user in chat; applied; `/health` 200 `{"status":"ok"}` on the first attempt. Independent
   check: the four resources exist in France Central, `FACTORY_RELEASE_SHA256` equals the release hash,
   PostgreSQL B1MS 17 Ready with 32 GB. **But** the Key Vault reference status was
   `AccessToKeyVaultDenied` (the health endpoint does not touch the database, so it could not notice).
9. **Live plan 2** (module 1.1.0, `Microsoft.ManagedIdentity` registered by the operator): the release was
   rebuilt from the same evidence with the **identical hash** (deterministic build); 2 to add, 5 to change
   (four only the module tag, the web app its identities), 0 to destroy. Binding
   `03a1018ab690809d00a2ac2b3806fbbe862ff773ed4296829d7a77407de3dc19` approved by the user; applied; health
   200 on the first attempt; the Key Vault reference status is **Resolved** (user-assigned identity).
   Both `apply-*.json` evidence files hold binding, approval, URL and health, and no credentials.

## Limits

- The free App Service plan has no virtual network integration, so the database firewall allows
  Azure-internal addresses (the `0.0.0.0` rule), with TLS required and a 32-character random password.
  Other Azure tenants' services are not excluded by that rule; a private network needs a paid plan.
- App Service publishing with `zip_deploy_file` needs basic publishing credentials on the SCM site
  (FTP is off); the provider stores the site credentials in the state, which only Entra ID principals
  with access to the target's container can read.
- The deployment identity uses a client secret (30 days) stored in the worker User Secrets, not a
  short-lived federated credential; workload identity federation needs a hosted runner (M15).
- Terraform containers have unrestricted outbound network access (Azure and the provider registry).
- The starter template and StockFlow have no EF migrations and no static web hosting yet: the
  deployment serves the API and `/health`; the Angular app and database schema creation are M14 work.
- The health check does not cover configuration: a database-backed health endpoint (M14) and a Key Vault
  reference status check in the deployment verification (M13A.2) are still missing.
- No automatic rollback: a failed apply leaves Terraform's partial state; a new plan reconciles it.
  Deleting the target is a manual, explicitly authorized operation.
