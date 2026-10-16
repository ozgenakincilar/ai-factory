-- Operator-maintained model prices (M8.1). Apply with the owner login to an initialized Factory
-- database. Append-only and idempotent: re-running inserts nothing new. Never edit a released row;
-- add a new row with a later EffectiveFrom instead. Every row must cite a verified source.
-- Units: USD millionths (micro-USD) per 1,000,000 tokens. Cached-input discounts are not modeled;
-- estimates for cached input are therefore high, never low.
BEGIN;
INSERT INTO orchestration."ModelPrices"
    ("Provider","Model","EffectiveFrom","InputMicrosPerMillion","OutputMicrosPerMillion","Source")
VALUES
    -- $0.15 input / $0.60 output per 1M tokens; snapshot gpt-4o-mini-2024-07-18 matches by prefix.
    -- Verified 2026-10-15; earlier price history not verified, so earlier calls stay unpriced.
    ('openai','gpt-4o-mini','2026-10-15T00:00:00Z',150000,600000,
     'https://developers.openai.com/api/docs/models/gpt-4o-mini (verified 2026-10-15)'),
    -- Claude Haiku 5.5 is priced by prompt length: $0.10/$0.50 per 1M up to 100,000 prompt tokens,
    -- $0.50/$2.50 above. The price table is flat, so the higher tier is recorded: estimates and
    -- reservations are upper bounds (about 5x for ordinary prompts) and the USD 2 demo cap can
    -- never be overrun by long prompts. Verified 2026-10-16 (global routing, no inference_geo).
    ('anthropic','claude-haiku-5-5','2026-10-16T00:00:00Z',500000,2500000,
     'https://platform.claude.com/docs/en/about-claude/pricing (verified 2026-10-16; >100K-prompt tier as upper bound)'),
    -- Developer Agent model (M10 user decision): $0.75 input / $4.50 output per 1M tokens, standard
    -- tier, prompts under 272K tokens; snapshot gpt-5.4-mini-2026-03-17 matches by prefix.
    ('openai','gpt-5.4-mini','2026-10-16T00:00:00Z',750000,4500000,
     'https://developers.openai.com/api/docs/models/gpt-5.4-mini (verified 2026-10-16)')
ON CONFLICT DO NOTHING;
COMMIT;
