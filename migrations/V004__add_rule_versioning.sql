-- =============================================================================
-- V004__add_rule_versioning.sql
-- Fraud Detection — Rule version tracking
-- Adds deactivated_at to know when a rule was turned off.
--
-- ROLLBACK:
--   ALTER TABLE fraud_rules DROP COLUMN IF EXISTS deactivated_at;
--   ALTER TABLE fraud_rules DROP COLUMN IF EXISTS updated_at;
-- =============================================================================

ALTER TABLE fraud_rules
    ADD COLUMN IF NOT EXISTS updated_at     TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS deactivated_at TIMESTAMPTZ NULL;

COMMENT ON COLUMN fraud_rules.deactivated_at IS
    'Set when is_active is changed to FALSE. '
    'Allows analysis of which rules were active during a specific historical period.';
