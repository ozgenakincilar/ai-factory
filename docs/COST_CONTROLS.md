# Milestone 8 — Tenant Isolation & AI Cost Controls

Status: plan approved 2026-10-15 (see [Architectural Decisions](DECISIONS.md#milestone-8-plan-2026-10-15-user-approved)).
**M8.1 usage metering implemented as an uncommitted local candidate.** M8.2 tenancy, M8.3 budgets,
M8.4 customer-provided models and M8.5 decomposition/routing/parallel steps are not started.
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
- Real OpenAI token counts have not been observed through this path yet (no live call in M8.1).
- USD only; no tenant attribution until M8.2.
