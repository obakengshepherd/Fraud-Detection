-- =============================================================================
-- V005__schema_verification.sql
-- Fraud Detection — Final schema integrity verification
-- =============================================================================

DO $$
BEGIN
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'fraud_rules'), 'fraud_rules missing';
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'transaction_evaluations'), 'transaction_evaluations missing';
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'fraud_alerts'), 'fraud_alerts missing';
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'user_behaviour_profiles'), 'user_behaviour_profiles missing';
    RAISE NOTICE 'Fraud Detection schema verified successfully.';
END;
$$;
