# M12a — Product Workspace slice

Status: implemented and verified on 2026-10-16 under the user's delegated authority (request: continue
with the UI slice while M9 waits for the container-runtime decision). This is the first slice of
[M12](ROADMAP.md#m12--angular-product-workspace), not the full Product Workspace. This document
describes inspected code and executed local verification, not production readiness.

## Scope

- **Web app** `src/Factory.Web`: Angular 22.2 (standalone components, signals, zoneless), English UI.
  Pages: sign-in, projects, project (run list and new-run form for both workflows), run detail
  (steps, routed tier and model per step, usage, plan, publication, cancel).
- **Blocking decision popup**: shown on every page while a run of the open project has a pending
  decision (plan approval, budget, unpriced model, unavailable customer model). Modal, no close
  control, Escape ignored, no timeout. Plan approval shows the parts, their classification and
  routed tier. Members without decision authority see that an organization administrator must decide.
- **Sign-in**: Authorization Code + PKCE against Keycloak with the new public client `factory-web`
  (`angular-oauth2-oidc` 22.0.2). Plain HTTP is allowed only for a loopback issuer. Tokens are kept
  in session storage and attached only to same-origin `/api` calls.
- **Live updates**: `@microsoft/signalr` 10.0.11 over long polling (the hub accepts tokens only in
  the Authorization header, which browser WebSockets cannot send). Hints trigger HTTP re-reads.
- **API (additive)**: `GET /api/me/projects` (caller's readable projects, organization, roles, decision
  authority; unknown identities get an empty list) and `GET /api/projects/{projectId}/runs` (50 most
  recent runs as safe summaries; same audience as a run). Migration `20261016130000_Workspace` adds
  the read-only functions `tenancy.my_projects` and `orchestration.list_runs` (runtime role only).
- **Authentication**: optional `Authentication:WebClient`; its tokens identify humans exactly like
  `HumanClient` tokens and must carry the hardcoded `factory_kind=Human` claim. It must differ from
  the other clients and the audience; any other client is rejected at authentication (401).

## Running locally

1. `scripts/Start-ReviewPostgres.ps1`, `scripts/Start-FactoryKeycloak.ps1` (fresh realms include
   `factory-web`; for an existing realm run `scripts/Register-FactoryWebClient.ps1` once).
2. `scripts/Initialize-FactoryLifecycle.ps1 -Database <db>` and
   `scripts/Provision-FactoryWorkspace.ps1 -Database <db>` (project `factory-workspace` for the local
   Keycloak user, organization Owner, USD 0.25 lifetime project budget).
3. `scripts/Run-FactoryWorkspace.ps1 -Database <db>` (API on 127.0.0.1:5168),
   `scripts/Run-FactoryWorker.ps1 -Database <db>`, and `npm start` in `src/Factory.Web`
   (http://127.0.0.1:4200; `/api` and `/hubs` are proxied to the API, so no CORS is involved).

## Executed verification — 2026-10-16

SDK 10.0.103, Node 24.19.0, npm 11.17.0; local candidate on top of `f992eab`.

1. Migration replayed inside `BEGIN … ROLLBACK`, then applied by the initializer to
   `factory_m86_verify_20261016` and `factory_m7_verify_20261015`.
2. While building: a test expected 403 for unsupported token profiles where the existing
   authentication correctly answers 401; a provisioning script assumed the Keycloak principal had
   not been created with another id; the sign-in page stayed blank because the OIDC library's
   `remoteOnly` HTTPS rule does not treat `127.0.0.1` as local (now an explicit loopback rule, and an
   unavailable sign-in service is shown instead of a blank page). All were fixed before the runs below.
3. Web unit tests (Vitest): **8 passed** (requirement parsing, pending-decision selection, popup
   content and controls, authority message, signed-out page, loopback rule). Production build
   succeeded (440 kB initial, 112 kB transferred); `npm audit`: 0 vulnerabilities.
4. End-to-end in real headless Edge (`scripts/Test-FactoryWorkspace.mjs`) against Keycloak, the API,
   the dev server and a worker on `gpt-4o-mini`: sign-in page only when signed out → Keycloak PKCE
   sign-in → project listed → decomposed run submitted from the form → blocking popup with a 1-part
   plan, only `approve`/`stop`, Escape ignored → approve recorded → live update to `succeeded` → run
   detail with 6 steps, tiers standard/strong/economy and the publication. Run
   `ab7324cc-6139-4824-9de0-b12d028de6c5`: 3 calls, 2515 input / 1145 output tokens, 1065 micro-USD.
   Worker logs: 22 lines, no errors; no credential- or token-like strings in worker or API logs.
5. Full .NET suite with `-IncludeKeycloak`: build **0 warnings, 0 errors**; **885 passed, 0 failed,
   0 skipped** (881 + 4 new), including four live Keycloak cases. TRX (ignored):
   `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-16_10_04_15.trx`, SHA-256
   `0dd9a21be28a4e0cf3a18edc3482d78b318577b4826bd1e99d62da95a6cb7240`.

## Limits

- Local development only: loopback HTTP issuer and dev-server proxy; no production hosting, HTTPS
  setup, content security policy or token-storage hardening yet.
- No organization/model/budget administration, review-case approval, roadmap or PR views (rest of
  M12). Runs are listed by time and status only; run views never show task text by design.
- Long polling only; one browser end-to-end sample is evidence of the path, not a reliability measure.
- The e2e script is run manually; it is not part of the .NET test suite.
