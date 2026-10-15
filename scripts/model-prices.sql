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
     'https://developers.openai.com/api/docs/models/gpt-4o-mini (verified 2026-10-15)')
ON CONFLICT DO NOTHING;
COMMIT;
