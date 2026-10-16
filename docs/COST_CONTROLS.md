# Milestone 8 — Tenant Isolation & AI Cost Controls

Status: plan approved 2026-10-15 (see [Architectural Decisions](DECISIONS.md#milestone-8-plan-2026-10-15-user-approved)).
**M8.1 usage metering committed and pushed as `7da963f`; M8.2 tenancy as `4c7789d`; M8.3 budgets,
retired synchronous generation and durable regeneration as `2564a85`; M8.4a customer-provided
OpenAI-compatible models as `c7eb11d`; M8.4b Anthropic provider as `8d0f3d6`; M8.5a model routing
policy as `fe83818`. M8.5b request decomposition with plan approval and M8.5c parallel parts are
implemented as a local candidate** ([M8.5b/c](#m85bm85c--request-decomposition-and-parallel-parts)).
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

## M8.3 — Budgets and reservations

Additive migration `20261016030000_Budgets` (embedded `Budgets.sql`); operator script
`scripts/budgets.sql`.

- **Budgets.** `orchestration."Budgets"`: scope `Organization`, `Project` or `Provider` (a
  Factory-level cap on the Factory's own credential for a provider), period `Month` (UTC calendar
  month) or `Lifetime`, limit in micro-USD, `EffectiveFrom` and a mandatory `Source`. Append-only;
  the latest row effective now applies, and a row with no limit lifts the budget. Operator-managed
  with the owner login in M8.3.
- **Reservations before calls.** The worker bounds every call with `MaxOutputTokens` (16,000;
  `gpt-4o-mini` allows 16,384 per its official model page, verified 2026-10-16) and reserves its
  worst case before calling: a conservative input estimate (about two characters per token plus
  overhead) plus the full output bound, priced with the current price row. Reservations are
  serialized per applicable scope with transaction advisory locks, so concurrent workers cannot
  overspend. `OpenAiModelProvider` passes the bound as `ChatCompletionOptions.MaxOutputTokenCount`
  (confirmed in the OpenAI 2.14.0 package documentation).
- **Conservative spend.** A reserved call counts at its actual estimate only when tokens and price
  are both known; otherwise (crash, failure, cancellation, unknown usage) at its reserved worst
  case. Spend is never under-reported. Each reservation settles at most once, only for its own
  attempt and provider.
- **Refusals become customer decisions.** If a reservation would exceed any applicable budget
  (`budget_exceeded`), or a budget applies but the model has no price (`model_price_unknown`), no
  call is made and the step waits for a decision with options `continue` and `stop`. Raising a
  budget does not resume anything by itself; an organization Owner/Admin must decide.
- **No budget, no enforcement.** Without an applicable budget, unpriced models still run; their
  reservation records a null amount and usage is reported as unpriced.
- **Visibility.** `GET /api/organizations/{organizationId}/budgets` (Owner/Admin) lists current
  organization and project budgets with limit, conservative spend and remaining. Provider caps are
  Factory operator data and are not shown to tenants.
- **Least privilege.** `reserve_cost` and the new `record_model_call` signature are worker-only;
  `read_budgets` is runtime-only; budgets and reservations are append-only even for the owner.
- **Anthropic demo cap.** `scripts/budgets.sql` sets a `Provider`/`anthropic` lifetime cap of
  USD 1.90, below the user's USD 2 Anthropic Console limit. Applied to the local live-data database.
  Until a verified Anthropic price is added, Anthropic calls cannot run under this cap.

### Executed verification — 2026-10-16

SDK 10.0.103, uncommitted working tree on top of `4c7789d`; no live LLM calls.

1. Migration replayed inside `BEGIN … ROLLBACK` on the M8.2 database, then applied by the
   initializer to it and to the live-data M7 database (upgrade), plus fresh
   `factory_m83_verify_20261016` and `-BaselineOnly` legacy database.
2. Unit tests: 33 passed. Orchestration + lifecycle filter: one M8.1 role-separation assertion
   called the replaced 8-argument `record_model_call` and received 42883 (function does not exist)
   instead of 42501; updated to the 9-argument signature, then 151 passed, 1 not executed (Keycloak).
3. Real headless Edge PKCE/state login, then the full suite with `-IncludeKeycloak`: build
   **0 warnings, 0 errors**; **776 passed, 0 failed, 0 skipped** (767 existing + 9 new), including
   four live Keycloak cases. TRX (ignored): `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-16_02_02_32.trx`,
   SHA-256 `f357f702a344bf805f186d3068c4db3f265e64ca364598603ae520be5d41901b`.
4. Keycloak and PostgreSQL stopped; data preserved.

New cases cover a budget refusal pausing the run with no model call, nothing resuming until an
organization admin decides, and exact settled spend afterwards; unpriced models blocked under a
budget but free without one; a provider lifetime cap applying across tenants but not to other
providers; unsettled reservations counted at worst case, settlement replacing them, concurrent
reservations unable to overspend, and single settlement per reservation; budget visibility for
organization admins only, lifting a budget, append-only tables and role separation; worst-case
reservation per call, bounded output and decisions instead of failures in the processor.

### M8.3 limits

- The synchronous generation routes are retired (see below); durable regeneration replaces the
  retired M6 regeneration route.

### Retired synchronous generation (user decision, 2026-10-16)

The five unmetered, unbudgeted generation routes are closed by default in every environment:
`POST /api/agents/analyst`, `POST /api/agents/architect`, `POST /api/workflows/analysis-architecture`,
`POST /api/projects/{projectId}/work-items` and
`POST /api/projects/{projectId}/work-items/{workItemId}/analysis-architecture`. Authenticated callers
receive **410 Gone** (`generation_route_retired`, pointing to `POST /api/projects/{projectId}/runs`)
and no model is called; anonymous callers still receive 401. `Generation:EnableLegacyEndpoints=true`
re-enables them only in Development or Testing, where the existing regression suites use it; any
other environment refuses to start with it set. M6 read routes, review endpoints and durable runs
are unaffected.

Verification (2026-10-16, SDK 10.0.103, no live LLM calls): build 0 warnings/errors; suite without
integrations 398 passed, 107 not executed; full suite with PostgreSQL and `-IncludeKeycloak`
**781 passed, 0 failed, 0 skipped** (776 + 5 retirement cases: 410 without model calls in Testing
and Development defaults, startup refusal in Production and Staging, durable runs and lifecycle
reads still mapped). TRX (ignored): `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-16_02_16_46.trx`,
SHA-256 `e877700d896535d1c7c013d4e4d11a350cd9c47b4c3fcaf11a7057ac7f859e96`.
- Budgets and prices are operator-managed SQL; there is no tenant-facing budget management API.
- The input estimate is a heuristic upper bound, not a tokenizer count; output is reserved at the
  full bound, so reservations can temporarily block work that would actually fit.
- USD only; spend is a Factory estimate, not the provider's invoice.

### Durable regeneration (user decision, 2026-10-16)

Additive migration `20261016040000_DurableRegeneration` (embedded `DurableRegeneration.sql`).

- `POST /api/projects/{projectId}/work-items/{workItemId}/runs` (Generator) with `Idempotency-Key`,
  the work item's current strong ETag in `If-Match` (missing 428, malformed 400, stale 412) and the
  same strict body as run submission; returns 202 like `POST …/runs`.
- At submission the database checks the ETag against the current work item snapshot, captures the
  case revision and next artifact version, and refuses a second active regeneration of the same
  work item (409). Creation runs still own a fresh case each.
- Publication adds the next version of the same managed case (or the first case of a work item
  that has none) through the M6 lineage, command and integrity guards, as a `Generate` command
  (HTTP-equivalent status 200). If the work item or case changed after submission, for example a
  human decision, publication fails `publication_stale` and nothing is overwritten.
- The run benefits from metering, budgets, cancellation, waiting decisions and notifications.

Verification (2026-10-16, SDK 10.0.103, no live LLM calls): migration replayed inside
`BEGIN … ROLLBACK` on the M8.3 database, then applied by the initializer (upgrade of two databases,
fresh `factory_m84_verify_20261016`). Targeted run: one new test built an ETag for a different work
item when probing the not-found path and correctly received InvalidCommand; the test was corrected
to use a matching id and now also asserts that mismatch. Full suite with PostgreSQL and
`-IncludeKeycloak`: build **0 warnings, 0 errors**; **786 passed, 0 failed, 0 skipped** (781 + 5
regeneration cases). TRX (ignored): `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-16_02_34_40.trx`,
SHA-256 `e75699de3c9a0d11ba0bb16dc1ae7720d63219674f7684d64e6ba7569f5def2e`.

## M8.4a — Customer-provided models (OpenAI-compatible)

Additive migration `20261016050000_ModelRegistry` (embedded `ModelRegistry.sql`). The Anthropic
adapter followed in [M8.4b](#m84b--anthropic-provider).

- **Credentials.** `ISecretProtector` / `AesGcmSecretProtector`: AES-256-GCM, 32-byte master key from
  `Secrets:MasterKey` (base64; User Secrets or environment), never stored in the database. The
  authenticated context binds each ciphertext to its organization and model, so a copied ciphertext
  does not decrypt. Keys are write-only: accepted once at registration, never returned or logged.
  Without the master key, registration and verification answer 503 and workers treat customer
  models as unavailable. An Azure Key Vault implementation follows with M13A.
- **Registry (organization Owner/Admin).** `POST /api/organizations/{organizationId}/models`
  (name, `OpenAiCompatible`, base URL, model, API key, optional customer-declared prices),
  `GET …/models`, `POST …/models/{modelId}/verify`. Customer-declared prices are inserted into the
  price table under provider `customer:<modelId>` with a source naming the declaring admin.
- **Capability test.** One tiny strict-schema call on the customer's credential: valid structured
  output → `Verified`/structured; reachable without it (including HTTP 400) → `Verified`/not
  structured; authentication, network or protocol failure → `Failed`. Verification calls are not
  metered (they have no run).
- **Assignment.** `PUT /api/projects/{projectId}/model` with `modelId` (null = Factory default) and
  `acceptCapabilityRisk`. Any model of the project's organization may be chosen; an unverified or
  non-structured model requires explicit acceptance (otherwise 409 `model_capability_unverified`).
  Assignments are append-only; a database trigger rejects cross-tenant assignment even for the owner
  login. Each run snapshots its project's model at submission.
- **Execution.** The worker resolves the run's model through `RegistryModelResolver`; a lease-
  authorized function returns the endpoint with the ciphertext only. Usage and reservations use
  provider `customer:<modelId>`. If the customer model cannot be used (endpoint error, no master
  key, undecryptable credential) the step waits for a `customer_model_unavailable` decision;
  there is never a silent fallback to a Factory model.
- **Egress (SSRF) policy.** Absolute HTTPS URLs without credentials, query or fragment. At connect
  time every resolved address is checked; any loopback, private (RFC 1918, CGNAT), link-local
  (including 169.254.169.254), unique-local, multicast, reserved, unspecified, or NAT64/IPv4-mapped
  equivalent refuses the connection, which also defeats DNS rebinding. No redirects or proxies;
  90 s timeout; 4 MiB response buffer. `ModelEndpoints:AllowLoopbackForDevelopment` permits loopback
  (HTTP or HTTPS) only in Development/Testing; other environments refuse to start with it set.
  Private-network endpoints remain out of scope until a separate mechanism is approved.
- **Least privilege.** Registry functions are runtime-only; `model_for_attempt` is worker-only and
  lease-checked; neither role can read `tenancy."Models"` or assignments directly.

### Executed verification — 2026-10-16

SDK 10.0.103, uncommitted working tree on top of `2564a85`; no live LLM calls (a loopback fake
OpenAI-compatible server stands in for customer endpoints).

1. Migration replayed inside `BEGIN … ROLLBACK` on the M8.3 database, then applied by the initializer
   to it and to the live-data database (upgrade), plus fresh `factory_m85_verify_20261016`.
2. While building tests, an analyzer warning (ASP0016) showed the fake server's handler was bound as
   a `RequestDelegate`, discarding its response body; it was rebound as a route handler before any
   test run was counted. Targeted run: 198 passed, 1 not executed (Keycloak).
3. Real headless Edge PKCE/state login, then the full suite with `-IncludeKeycloak`: build
   **0 warnings, 0 errors**; **828 passed, 0 failed, 0 skipped** (786 + 42 new), including four live
   Keycloak cases. TRX (ignored): `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-16_03_01_50.trx`,
   SHA-256 `68ef25267306d46eb9ad1bc61bdc92110dad1b7acefb1728bbf5a4131756719c`.
   Solution-wide vulnerable-package scan: none.
4. Keycloak and PostgreSQL stopped; data preserved.

New cases cover AES-GCM round trip, context binding, tamper and wrong-key refusal; URL rules;
the address table; connect-time refusal of loopback including via `localhost`; loopback allowed only
when enabled; registration, verification, assignment and a full run on the customer's endpoint with
the customer's own key, customer-declared pricing under an organization budget and attribution to
`customer:<id>`; explicit acceptance for unverified/non-structured models; unreachable models and
keyless workers waiting without fallback; run model snapshots; tenant isolation, the cross-tenant
trigger, encrypted storage, lease-checked endpoint reads and role separation; HTTP write-only keys,
URL rejection, admin-only access and 503 without a master key.

### M8.4a limits

- Only the OpenAI-compatible protocol; Anthropic is M8.4b. Private-network endpoints are refused.
- Models cannot yet be updated, rotated or deactivated through the API (rotation = register anew
  and reassign). The master key has no rotation procedure yet.
- `Run-FactoryApi.ps1`/`Run-FactoryWorker.ps1` do not provision a master key; operators set
  `Secrets:MasterKey` themselves.
- Verification calls are not metered; customer-declared prices are trusted as declared.

## M8.4b — Anthropic provider

Additive migration `20261016070000_AnthropicModels` (embedded `AnthropicModels.sql`) widens the
registry protocol check to `Anthropic`; existing rows are unchanged.

- **Adapter.** `AnthropicModelProvider` uses the official Anthropic .NET SDK (`Anthropic` 12.58.0)
  and the Messages API: system prompt, one user message, `max_tokens` = the reserved bound, and
  structured output through `output_config.format` with the agent's unchanged JSON schema.
  `end_turn` → completed; `refusal` → refused; any other stop reason → incomplete. Usage counts all
  prompt tokens (input plus cache writes and reads) as input; the served model name is recorded.
  The key is always explicit (no environment/profile credential resolution), SDK retries are 0,
  timeout 90 s.
- **Factory provider.** The worker chooses its default provider with `Generation:Provider`
  (`openai` default, `anthropic`); `Anthropic:ApiKey` (User Secrets) and optional `Anthropic:Model`
  (default `claude-haiku-5-5`). `Run-FactoryWorker.ps1 -Provider anthropic` sets it. Reservations
  and usage use provider `anthropic`, so the existing Provider/Lifetime budget of USD 1.90 applies.
- **Price.** `scripts/model-prices.sql` adds `anthropic`/`claude-haiku-5-5`, verified 2026-10-16 on
  the official pricing page. Haiku 5.5 costs $0.10/$0.50 per 1M tokens up to 100,000 prompt tokens
  and $0.50/$2.50 above; the flat table records the higher tier, so estimates are upper bounds.
  Applied to `factory_m7_verify_20261015`.
- **Customer Anthropic credentials.** Protocol `Anthropic` in model registration (base URL e.g.
  `https://api.anthropic.com`); the same encryption, egress policy, verification (HTTP 400 →
  reachable but not structured), risk acceptance and no-fallback rules as M8.4a.

### Executed verification — 2026-10-16

SDK 10.0.103, local candidate on top of `c7eb11d`; no live generation calls.

1. Migration replayed inside `BEGIN … ROLLBACK` on `factory_m85_verify_20261016`, then applied by
   the initializer to it and to `factory_m7_verify_20261015` (upgrade), plus fresh
   `factory_m86_verify_20261016` and `_legacy` (baseline).
2. A new unit test showed the SDK's `Model.ToString()` returns the JSON form with quotes, which
   would have broken price matching; the adapter now reads the raw wire value. Targeted run after the
   fix: 48 passed.
3. Real headless Edge PKCE/state login, then the full suite with `-IncludeKeycloak`: build
   **0 warnings, 0 errors**; **841 passed, 0 failed, 0 skipped** (828 + 13 new), including four live
   Keycloak cases. TRX (ignored): `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-16_03_34_30.trx`,
   SHA-256 `1b7f0c142d18a82f5f9a9f1a8890108d62e47348e575d7dd691b81d8dc2882c3`.
   Solution-wide vulnerable-package scan (including `Anthropic` 12.58.0): none.
4. The Anthropic key in User Secrets authenticated against the free `GET /v1/models` endpoint
   (HTTP 200); no generation call was made because the account has no credit yet.

New cases (loopback fake Messages API) cover the request shape (key header, version header, model,
bound, system, user message, unchanged schema), usage including cache counters and served model,
stop reasons, no client retries on 429/500/529, customer verification through the egress policy
(structured, not structured, 400, 401, connect-time refusal), key validation without echo, and a
full durable run on a customer's Anthropic key with organization pricing and attribution.

### M8.4b limits

- No live Anthropic generation has been verified; real structured-output behaviour of Claude Haiku
  5.5 on the Factory's schemas (including enum casing) is untested until the account has credit.
- The flat price table cannot express prompt-length tiers; Haiku estimates are upper bounds.
- Thinking/effort are left at model defaults (Haiku 5.5: adaptive, effort medium); thinking tokens
  count toward the reserved output bound and are billed as output.
- Prompt caching, streaming and Anthropic fallbacks are not used.

## M8.5a — Model routing policy

Additive migration `20261016090000_ModelRouting` (embedded `ModelRouting.sql`). Decisions and the
answers to the routing questions: [M8.5a decision](DECISIONS.md#model-routing-policy--m85a-2026-10-16-delegated).

- **Domain policy.** `ModelRoutingPolicy` (`routing-v1`) is a deterministic domain service:
  tier = max(role minimum, complexity tier, importance tier) with `Economy < Standard < Strong`.
  Role minimums: Analysis Standard, Architecture Strong. Complexity Low/Medium/High maps to
  Economy/Standard/Strong; importance High forces Strong. Reason codes: `role_minimum`,
  `complexity`, `high_importance`, `customer_model`. Today's workflow has no part classification,
  so Analysis routes to Standard and Architecture to Strong; classification arrives with M8.5b.
- **Catalog.** Operators map tiers with `Generation:Tiers:{Economy|Standard|Strong}` =
  `<provider>:<model>` (`openai` or `anthropic`); tiers without a mapping use the
  `Generation:Provider` default. Only keys of providers in use are required.
- **Customer models.** A project's assigned customer model serves every step; the route records
  the policy tier with reason `customer_model`.
- **Evidence.** Before the first provider call of each model attempt the worker records one route
  (`orchestration.record_route`, lease-checked, worker-only): tier, reason, policy version and the
  provider/model actually used. `orchestration."StepRoutes"` is append-only (one row per attempt);
  runtime roles cannot read it directly. Run views include `routes`.
- **Unchanged.** Budgets still reserve per call with the routed model's identity, so a tier mapped
  to an unpriced model under a budget parks the step for a decision (M8.3); no automatic escalation.

### Executed verification — 2026-10-16

SDK 10.0.103, local candidate on top of `8d0f3d6`; no live LLM calls.

1. Migration replayed inside `BEGIN … ROLLBACK` on `factory_m86_verify_20261016`, then applied by
   the initializer to it and to `factory_m7_verify_20261015` (upgrade).
2. Targeted orchestration, registry and migration run: 82 passed. An expected-SQLSTATE mistake in a
   new test (`23514` instead of the immutable-record `55000`) was corrected before this run.
3. Real headless Edge PKCE/state login, then the full suite with `-IncludeKeycloak`: build
   **0 warnings, 0 errors**; **853 passed, 0 failed, 0 skipped** (841 + 12 new), including four live
   Keycloak cases. TRX (ignored): `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-16_03_43_40.trx`,
   SHA-256 `41147f8f80ef0a4f6c602785d0b527fde5998aa71f96bccd04d23695fb65d4bf`.

New cases cover the policy table, rejection of unrouted kinds and unknown classifications,
single-model catalogs, per-tier provider selection with the route recorded before any reservation
or call, release without a model call when the lease is lost while recording, routes in the run
view, lease/token/validation checks, write-once and immutable evidence, and role separation.

### M8.5a limits

- No classification exists yet for whole requests; Economy is reachable only with M8.5b parts.
- No tenant- or project-level policy overrides; no routing among several customer models.
- Tier mappings are per worker process; workers with different mappings route differently (each
  route records what was used).

## M8.5b/M8.5c — Request decomposition and parallel parts

Additive migration `20261016110000_Decomposition` (embedded `Decomposition.sql`). Decisions:
[M8.5b/c decision](DECISIONS.md#request-decomposition--m85bc-2026-10-16-delegated).

- **Workflow `decomposition-v1`.** Selected per run with `"workflowVersion": "decomposition-v1"` on
  `POST /api/projects/{projectId}/runs` (input bounds unchanged; `analysis-architecture-v1` stays the
  default and unchanged). Steps: Analysis (0) → Planning (3) → PlanApproval (4) → Part steps (5–16)
  → merge (Architecture, 1) → Publication (2). Ordinals 0–2 keep their kinds in every version, so the
  review artifact format and publication are unchanged; execution order comes from explicit
  dependencies (`orchestration."StepDependencies"`, append-only).
- **Planning.** `PlannerAgent` (role minimum Strong) returns a strict-schema plan: 1–12 parts keyed
  P1..Pn, each with title, scope, the functional requirement ids it covers, complexity
  (low/medium/high), importance (normal/high) and dependencies on earlier parts only (acyclic by
  construction). Every requirement must be covered; unknown ids are rejected. C# and the database
  (`orchestration.valid_plan`) validate the same rules; an invalid plan fails the run explicitly.
- **Plan approval.** The run waits on decision `plan_approval` (`approve` / `stop`) — the existing
  blocking popup with no timeout, decided by an organization Owner/Admin. `GET
  /api/projects/{projectId}/runs/{runId}/plan` (same audience as the run) returns the plan and each
  part's routed tier. Run views never include plan text. `stop` cancels the run before any part runs.
  `approve` lets the worker complete the approval step, which creates every part step and its edges
  in one transaction from the stored, revalidated plan.
- **Parts in parallel.** A part becomes claimable when its dependencies succeeded; at most four
  attempts of one run run at once (database-enforced; analysis-architecture-v1 keeps one). Each part
  runs the Architect on the part's requirements only, with its dependencies' proposal summaries as
  context, on the tier `ModelRoutingPolicy` gives its classification (Economy is reachable here).
  Parts write only their own step checkpoint; no aggregate is modified concurrently.
- **Merge.** After every part succeeded, the Architecture step deterministically merges the part
  proposals (ids prefixed with the part key, provenance unchanged) and validates the result against
  the full analysis; failure is `integration_failed`. Publication then runs as before.
- **Failures and decisions.** A failed part fails the run, ends running siblings' leases and cancels
  pending decisions. Budget or customer-model decisions are per step: while one part waits, running
  siblings may finish but nothing new starts until every pending decision is made.

### Executed verification — 2026-10-16

SDK 10.0.103, local candidate on top of `fe83818`; no live LLM calls.

1. Migration replayed inside `BEGIN … ROLLBACK` on `factory_m86_verify_20261016`, then applied by
   the initializer to it and to `factory_m7_verify_20261015` (upgrade).
2. While writing tests: three assertion mistakes in new unit tests (tuple comparison of collections
   by reference, and expecting `InvalidCommand` where a second approval correctly returns
   `Conflict`) were corrected; then 66 unit tests and a targeted orchestration run of 125 passed.
3. Real headless Edge PKCE/state login, then the full suite with `-IncludeKeycloak`: build
   **0 warnings, 0 errors**; **881 passed, 0 failed, 0 skipped** (853 + 28 new), including four live
   Keycloak cases. TRX (ignored): `artifacts/m6-tests/Özgen_DESKTOP-TQO89E0_2026-10-16_04_03_32.trx`,
   SHA-256 `a2cd3a86af52a414ba000cf1fde0fcd9d0ea0e88229c750bf94c89c841c94bc4`.
   Solution-wide vulnerable-package scan: none.

New cases cover plan validation (keys, dependencies, coverage, limits, classifications), part
scoping, merging into a proposal valid for the full analysis, the Planner, the generalized
aggregate (parallel start, concurrency bound, approval atomicity, failure cancelling siblings,
waiting with running siblings), approval and merge in the processor, part routing per
classification, and against PostgreSQL: an end-to-end decomposed run with two parts held by two
workers at once and the merged proposal published under the unchanged artifact format; stop before
any part; an invalid plan failing without a decision; a failing part ending its sibling's lease;
database plan revalidation, approval without an "approve" decision refused, plan access limited to
the project and role separation; HTTP selection and the plan endpoint.

### M8.5b/c limits

- Durable regeneration still runs `analysis-architecture-v1` from the API (the database accepts
  both versions).
- The customer can approve or stop a plan, not edit it. Parts run only the Architect, not the Analyst.
- No live run of the Planner or of parts has been verified; real model plans may be rejected more
  often than the deterministic test model's.
- A worker claim skips a run whose row another claim holds at that instant; it finds the run on its
  next poll (parallelism is eventual, not instantaneous).
