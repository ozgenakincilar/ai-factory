-- Versioned M8.4b migration. Never edit after release. Allows customer-registered Anthropic
-- (Claude Messages API) credentials next to OpenAI-compatible endpoints. Existing rows are unchanged.
ALTER TABLE tenancy."Models" DROP CONSTRAINT "Models_Protocol_check";
ALTER TABLE tenancy."Models" ADD CONSTRAINT "Models_Protocol_check" CHECK ("Protocol" IN ('OpenAiCompatible','Anthropic'));
