# StockFlow pilot — M14

Status: **live end to end** on 2026-10-17. The StockFlow Product catalog (requirements USER-1 to USER-5,
including the web screen) went through the Factory: request → analysis and architecture → human review →
Developer Agent → Factory verification → durable deployment with the user's approval of the exact plan →
Azure staging (France Central) → health, configuration and functional checks. The recovery exercise is still
open (see the end).

Status of the M14 acceptance: everything except the recovery exercise, which the user decided not to run
(procedure below, unverified).

Live application: https://stock-flow-staging-9b228545.azurewebsites.net (App Service F1, PostgreSQL B1MS).

## What runs

- Development job `879bcc1d-c46a-4ced-a2f2-84765d2de878` (template `dotnet-angular-postgres-ddd` **1.1.3**,
  gpt-5.4-mini, 11 turns, USD 0.0973, all Factory checks passed): `Product` aggregate (SKU, name, price rules),
  application service and repository port, EF Core mapping and repository, `[ApiController]` endpoints under
  `/api/products`, domain tests, and an Angular products screen (list, create, delete) served by the API.
- Deployment `88cfbf3a-dba5-48b7-91c0-23a9bd5bc877` (module 1.2.0): plan 0 add, 1 change, 0 destroy; binding
  `bd0da3acaa9793e899cd66c801518871f5e0ccf998d339d05b8e011b8acfe532`, release
  `b3bf48f6258c9ab98b0ea314eb3c4dfde434d51a46edb66ecc26e38d9c79fa2e`; **approved by the user in the browser**;
  Succeeded: `/health/ready` 200 (database reachable), Key Vault reference Resolved.

## Executed verification — 2026-10-17

1. API on Azure: create 201; get 200; update 200; delete 204 then 404; list; invalid SKU and negative price
   400 with `product_sku_invalid` / `product_price_negative`; duplicate SKU 400 `product_sku_not_unique`.
2. Web app on Azure in headless Edge: the Angular app renders the products screen; a product entered in the
   form appears in the list and in `GET /api/products`; the screen's Delete removes it (API list empty
   again). Static files are served as files (`main-*.js` as `text/javascript`); client routes fall back to
   `index.html`; unknown `/api` paths are 404. Only `favicon.ico` is missing (404).
3. Factory tests: full suite with `-IncludeKeycloak -IncludeDocker`: Keycloak **4 passed**, remaining
   **1039 passed, 0 failed, 1 skipped** (paid live test), including the offline sandbox verification of
   templates 1.0.0 and 1.1.3 and a byte-identical release rebuild; web **30 passed**, production build clean.

## What the pilot found and fixed

Every item was found by a live run, reproduced, fixed with a test, and verified again.

| Finding | Fix |
| --- | --- |
| The agent looped on reading: a lock file larger than the known-files budget was listed "read again", and reading it answered "already known" | Files too large to show are never "known"; "already known" only for files the last prompt showed |
| Template 1.1.0: `AggregateRoot.DomainEvents` broke the EF model (500 on readiness) and the agent only saw "500" | 1.1.0 ignores `DomainEvents`; a persistence test reports EF's own message |
| Release rebuilds differed (`release_mismatch` at apply; nothing was applied): build-time `Last-Modified` of compressed static assets | Fixed timestamps for the web build and `CompressionEnabled=false`; the release test now builds twice |
| Template 1.1.0 served `index.html` for every script (the fallback matched files; static files skip matched endpoints): a blank page | 1.1.1: `nonfile` route constraint, test that file requests never fall back |
| Domain rule violations answered 500 | 1.1.1: exception handler maps `DomainException` to 400 with its code |
| The agent wrote `[ApiController]` classes the template did not map (API 404) | 1.1.2: controllers and minimal APIs are both mapped |
| A job without any change for USER-5 (the web screen) was "Verified" | `requirement_coverage` is **blocking for confirmed requirements** (suggestions stay a warning) |
| Web unit tests passed while `ng build` failed on template type errors, seen only in the final verification | The agent's build action also runs `ng build` after web changes |
| The model treated new files as files it must read first and finished without writing, spending its corrections | Prompt: new files need no reading; finishing without changes is refused without spending a correction |
| A new release's table did not exist next to the previous release's table (`EnsureCreated` creates only into an empty database) | 1.1.3: the model's missing tables are created; existing tables are kept |
| An accidentally approved older release (0279f864) Succeeded although its product endpoints answered 500: `/health/ready` checks only the connection | Open: readiness should touch the application's own tables (see limits) |

Template versions 1.1.0–1.1.2 stay registered unchanged because verified jobs' evidence names them.

## Cost

Developer Agent this session: 12 jobs, USD 0.852 (4 verified; failures included). Total Developer spend
since M10: about **USD 1.11 of USD 2**. Azure: within the free trial (F1 free, B1MS free tier, Key Vault and
storage cents); budget alert USD 5/month.

## Limits and open items

- **Recovery exercise not executed** (M14 acceptance item; the user decided on 2026-10-17 not to run it).
  The procedure below is documented, not verified.
- No versioned EF migrations: a changed model on an existing table is not migrated (1.1.3 only creates
  missing tables; "Failed executing DbCommand" lines on later starts are expected and harmless).
- Readiness checks the connection, not the application's tables.
- The Developer Agent succeeded in 4 of 12 attempts here; Factory checks kept every incomplete result from
  being verified, but repeated paid attempts were needed.
- Pull requests for template 1.1.x jobs are not opened in `ozgenakincilar/stockflow`: its `main` holds the
  1.0.0 base commit, so a 1.1.x branch would have an unrelated history.
- Staging only; no custom domain, TLS certificate of our own, authentication or backups beyond the
  PostgreSQL defaults.

## Recovery procedure (documented, not executed)

The drill that was prepared: an operator corrupts the web app's `ConnectionStrings__Database` setting with
the Azure CLI (as a portal mistake would); `/health/ready` and the product endpoints fail; a new deployment of
the same verified job is requested; its plan shows the drift as one change to `azurerm_linux_web_app.api`;
an Owner/Admin approves; the apply restores the setting; health, the Key Vault reference and the product
data (created before the incident) are checked. The agent's permission policy refused both injecting the
fault and deleting the drill's test product (`RECOVERY-1`, left in staging); the user chose not to run it.

General procedure for this target:

- **Configuration or infrastructure drift**: request a deployment of the current verified job; Terraform
  plans the difference against the module; approve only if the plan reverts exactly the drift; verify.
- **Bad release**: request a deployment of an earlier verified job (a rollback is a forward deployment of
  older evidence); the release is rebuilt from that evidence and must reproduce its hash.
- **Failed or interrupted apply**: the deployment ends as `Failed`/`worker_lost`; request a new plan, which
  shows Terraform's partial state; a held state lock needs `terraform force-unlock` by the operator.
- **Data**: PostgreSQL flexible server point-in-time restore (7 days retention) creates a new server; this
  is outside the Factory and was not exercised; the free tier covers one B1MS server only.

