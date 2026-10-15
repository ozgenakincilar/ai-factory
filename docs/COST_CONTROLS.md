# Milestone 8 — Tenant Isolation & AI Cost Controls

Status: plan approved 2026-10-15 (see [Architectural Decisions](DECISIONS.md#milestone-8-plan-2026-10-15-user-approved)).
**M8.1 usage metering committed and pushed as `7da963f`. M8.2 tenancy implemented as an
uncommitted local candidate** ([M8.2](#m82--tenancy)). M8.3 budgets, M8.4 customer-provided models
and M8.5 decomposition/routing/parallel steps are not started.
This document describes inspected code and executed local verification, not production readiness.

## M8.1 — Usage metering

- **Provider contract.** `ModelResponse` gains an optional `ModelUsage(Provider, Model, InputTokens,
  OutputTokens)`; `IModelProvider` is unchanged. `OpenAiModelProvider` reports `openai`, the model
  the provider says served the call (`ChatCompletion.Model`, often a dated snapshot) and
  `ChatCompletion.Usage.InputTokenCount/OutputTokenCount`, for completed, refused, incomplete and
  empty responses alike, since all are billed. The properties were confirmed against the OpenAI
  2.14.0 package's API documentation and by compilation.
- **Recording.** The durable worker wraps its provider per step and records every call through the
  worker-only `record_model_call` function. Failed and cancelled calls are recorded with unknown
  tokens, never zero. A call that returns after its lease was lost is still recorded, because it was
  made; ownership is proven by the attempt's lease token. At most 10 calls per attempt; recording is
  accepted for one day after the attempt started.
- **Prices.** `orchestration."ModelPrices"` holds operator-maintained USD prices in millionths per
  million tokens with `EffectiveFrom` and a mandatory `Source`. It is append-only; corrections are
  new rows. The most specific match wins (exact model, or a dated snapshot `<model>-…`), then the
  latest price effective at the call time. **The table starts empty and the Factory never invents
  prices.** Operators insert verified prices with the owner login, for example:

  ```sql
  INSERT INTO orchestration."ModelPrices"
      ("Provider","Model","EffectiveFrom","InputMicrosPerMillion","OutputMicrosPerMillion","Source")
  VALUES ('openai','<model>','<timestamptz>',<input micro-USD per 1M>,<output micro-USD per 1M>,'<verified source URL and date>');
  ```

- **Price data.** `scripts/model-prices.sql` is the versioned, idempotent operator script with cited
  sources. As of 2026-10-15 it contains `openai`/`gpt-4o-mini` at $0.15 input and $0.60 output per
  1M tokens (official model page, verified 2026-10-15), effective from 2026-10-15 because earlier
  price history was not verified. Applied to the local `factory_m7_verify_20261015` database; the
  snapshot `gpt-4o-mini-2024-07-18` matches it, and a call recorded before the row existed stays
  unpriced by design.
- **Cost.** Each call's estimated cost is computed once, at recording, as
  `ceil(input × inputPrice + output × outputPrice) / 1,000,000` micro-USD, and stored with the price
  row's `EffectiveFrom`; later price rows never change it. Totals report `estimatedCostMicros` for
  priced calls, `unpricedCalls` (tokens but no price) and `unknownUsageCalls` (no reported tokens)
  separately, so missing data is never shown as zero cost. Provider-billed actual cost is not
  available per call and is not claimed.
- **Reads.** `RunView.usage` on `GET /api/projects/{projectId}/runs/{runId}` and
  `GET /api/projects/{projectId}/usage?from=&to=` (half-open, at most 366 days, default last 30
  days; per provider/model breakdown). Same audience as run reads: active Generator or Human Reviewer.
- **Least privilege.** `record_model_call` is executable only by the worker role, `read_usage` only
  by the API runtime role; neither can read or change `ModelCalls` or `ModelPrices` directly.
  Both tables are append-only even for the owner.

### Executed verification — 2026-10-15

SDK 10.0.103, uncommitted working tree on top of `73f83ab`; no live LLM calls.

1. Migration `20261016010000_UsageMetering` replayed inside `BEGIN … ROLLBACK` on the M7.3
   database, then applied by the initializer (upgrade), plus fresh `factory_m81_verify_20261016`
   and `-BaselineOnly` `factory_m81_verify_20261016_legacy`.
2. Unit tests: 29 passed. Orchestration + migration filter: 64 passed after one assertion fix: an
   M7.1 HTTP test asserted that run status JSON contained no "token" substring; the new
   `inputTokens`/`outputTokens` counters matched it, so it now checks lease material specifically.
   The intermediate failing runs left no TRX because the operator invocation piped stderr into a
   `Stop` error preference and aborted the test host; this was an invocation error, not a test result.
3. Real headless Edge PKCE/state login, then the full suite with `-IncludeKeycloak`: build
   **0 warnings, 0 errors**; **763 passed, 0 failed, 0 skipped** (754 existing + 9 new), including
   four live Keycloak cases. TRX (ignored): `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-15_23_19_12.trx`,
   SHA-256 `c4dcca744afeb982bb9f877b3ff654e0778cd625ce164f169eb8a925b156a999`.
4. Keycloak and PostgreSQL stopped; data preserved.

New cases cover exact cost arithmetic with snapshot-name price matching and immutability of past
costs after a new price; unpriced and unknown usage reported separately; failed, cancelled and
refused calls metered; usage-write failures not changing step outcomes; ownership, validation and
the per-attempt bound; late calls after a lost lease; read authorization and range bounds; role
separation and append-only tables.

### M8.1 limits

- Only durable worker runs are metered. The M6 synchronous lifecycle route and the ephemeral
  agent/workflow endpoints are not; they should be retired or metered before budgets are enforced.
- A worker crash during a call loses that call's record (the provider may still bill it); a failed
  usage write is an undercount. Budgets (M8.3) must reserve before calls rather than rely on records.
- One user-approved live run (2026-10-15, after commit `7da963f`) recorded a real OpenAI call:
  `gpt-4o-mini-2024-07-18`, 464 input / 322 output tokens, 4815 ms, Completed, unpriced at the
  time. The run itself failed `invalid_output` (model output did not pass Analyst validation) and
  was not retried, by design. One sample shows the path works; it is not a reliability measure.
- USD only; no tenant attribution until M8.2.

## M8.2 — Tenancy

Additive migration `20261016020000_Tenancy` (embedded `Tenancy.sql`) and
`scripts/tenancy-runtime-grants.sql` (run by the initializer after the orchestration grants).
Access tables and their EF model are unchanged.

- **Model (Tenancy bounded context).** `tenancy."Organizations"`; `OrganizationMemberships` with
  roles `Owner`/`Admin`, human only (composite foreign key to the principal's id and kind, kind
  fixed to `Human`); `ProjectOwnership`, one organization per project, immutable (no update or
  delete, even for the owner login). A deferred constraint trigger rejects any new
  `access."Projects"` row without ownership at COMMIT.
- **Backfill.** Each pre-existing project gets its own `legacy-<projectId>` organization, so no two
  existing projects are merged into one tenant. Active Human project Reviewers become Admins of
  their project's legacy organization, preserving who could decide waiting steps. On the local
  live-data database: 137 projects, 137 owned, 132 Admin memberships after upgrade.
- **Decision authority.** `orchestration.decide` now requires an active Human Owner/Admin of the
  project's organization (`tenancy.lock_project_admin`, locked against concurrent revocation),
  replacing the M7.2 interim project-Reviewer authority. A project role is neither required nor
  sufficient; Service identities are always refused.
- **Organization usage.** `GET /api/organizations/{organizationId}/usage?from=&to=` (Owner/Admin
  only; same range rules as project usage) totals usage across the organization's projects with
  per-project and per-model breakdowns. Executable by the API runtime only.
- **Least privilege.** Tenancy tables are operator-managed with the owner login; the API runtime
  and worker roles cannot read or write them, and the worker cannot call tenancy functions.

### Executed verification — 2026-10-15

SDK 10.0.103, uncommitted working tree on top of `d2e16f4`; no live LLM calls.

1. Migration replayed inside `BEGIN … ROLLBACK` on the M8.1 database, then applied by the
   initializer to it and to the live-data M7 database (upgrade), plus fresh
   `factory_m82_verify_20261016` and `-BaselineOnly` `factory_m82_verify_20261016_legacy`.
2. Orchestration + lifecycle filter: 142 passed, 1 not executed (the Keycloak case).
3. Real headless Edge PKCE/state login, then the full suite with `-IncludeKeycloak`: build
   **0 warnings, 0 errors**; **767 passed, 0 failed, 0 skipped** (763 existing + 4 new), including
   four live Keycloak cases. TRX (ignored): `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-15_23_37_57.trx`,
   SHA-256 `74019a8383670c8b0f926d844c509a724e5db607ac0707dc881fababf97deb5b`.
4. Keycloak and PostgreSQL stopped; data preserved.

New and changed cases cover ownership required at COMMIT and immutable; organization roles human
only; a project Reviewer without an organization role and another tenant's Owner both refused,
while an organization Admin without a project role decides; organization usage limited to its own
projects and to Owner/Admin; tenancy tables and functions unreachable by runtime roles; and the
migration backfill. The M7.2 decision test now revokes the organization role instead of the
project Reviewer role, and the test fixture creates an owning organization for every project.

### M8.2 limits

- No API yet to create organizations, transfer membership or manage roles; operators use SQL.
- Legacy organizations whose project had no active Human Reviewer have no Admin; an operator must
  assign one before waiting decisions on those projects can be made.
- Project reads still use project membership; organization Admins do not automatically read
  project content.
