-- =============================================================================
-- V003__add_gin_indexes.sql
-- Fraud Detection — GIN indexes on JSONB columns for analyst queries
--
-- ROLLBACK:
--   DROP INDEX IF EXISTS evaluations_rules_fired_gin_idx;
--   DROP INDEX IF EXISTS profiles_common_locations_gin_idx;
-- =============================================================================

-- GIN index on rules_fired JSONB allows querying:
-- "Which evaluations had rule X fire?" — useful for rule effectiveness analysis
-- Query: SELECT * FROM transaction_evaluations WHERE rules_fired @> '[{"rule_id":"rule_vel_01"}]'
CREATE INDEX evaluations_rules_fired_gin_idx
    ON transaction_evaluations USING GIN (rules_fired);

COMMENT ON INDEX evaluations_rules_fired_gin_idx IS
    'GIN index allows efficient JSONB containment queries: '
    'find evaluations where a specific rule fired. '
    'Used for rule effectiveness analytics.';

-- GIN index on common_locations for geospatial containment queries
CREATE INDEX profiles_common_locations_gin_idx
    ON user_behaviour_profiles USING GIN (common_locations);

COMMENT ON INDEX profiles_common_locations_gin_idx IS
    'GIN index on common_locations JSONB array. '
    'Enables: find all users who transact in a specific area.';
