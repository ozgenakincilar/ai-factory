-- Operator-maintained budgets (M8.3). Apply with the owner login to an initialized Factory database.
-- Append-only and idempotent: re-running inserts nothing new. Never edit a released row; change a
-- budget by adding a row with a later EffectiveFrom (LimitMicros NULL lifts it). Cite a source.
-- Units: USD millionths (micro-USD). Month = UTC calendar month; Lifetime = all time.
BEGIN;
INSERT INTO orchestration."Budgets" ("Id","Scope","ScopeId","Period","LimitMicros","EffectiveFrom","Source")
VALUES
    -- Anthropic demo: the user's USD 2 limit (2026-10-15). The Factory-side cap is USD 1.90 so the
    -- Factory stops before the Anthropic Console limit is reached. Calls with no Anthropic price in
    -- scripts/model-prices.sql cannot run under this cap; they wait for a human decision instead.
    ('6b1d9a8e-3c0f-4f6e-9a51-2d7c4e0b8f13','Provider','anthropic','Lifetime',1900000,'2026-10-16T00:00:00Z',
     'User decision 2026-10-15: Anthropic demo budget USD 2 (DECISIONS.md, Milestone 8 plan)')
ON CONFLICT DO NOTHING;
COMMIT;
