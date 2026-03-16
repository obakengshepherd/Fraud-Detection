-- =============================================================================
-- V002__create_transaction_evaluations.sql
-- Fraud Detection System — transaction_evaluations table
--
-- ROLLBACK:
--   DROP TABLE IF EXISTS transaction_evaluations CASCADE;
-- =============================================================================

CREATE TABLE transaction_evaluations (
    id               VARCHAR(36)    NOT NULL,
    transaction_id   VARCHAR(36)    NOT NULL,
    user_id          VARCHAR(36)    NOT NULL,
    risk_score       SMALLINT       NOT NULL,
    decision         fraud_decision NOT NULL,
    rules_fired      JSONB          NOT NULL DEFAULT '[]',
    model_version    VARCHAR(16)    NOT NULL DEFAULT '1.0.0',
    evaluated_at     TIMESTAMPTZ    NOT NULL DEFAULT NOW(),

    CONSTRAINT transaction_evaluations_pkey PRIMARY KEY (id),

    -- UNIQUE on transaction_id implements exactly-once evaluation semantics.
    -- The Kafka consumer commits offsets only after this row is written.
    -- If a message is re-processed (consumer crash + restart), the duplicate
    -- INSERT fails with a unique constraint violation — idempotent by design.
    CONSTRAINT transaction_evaluations_txn_unique UNIQUE (transaction_id),

    CONSTRAINT evaluations_risk_score_range
        CHECK (risk_score BETWEEN 0 AND 100)
);

COMMENT ON COLUMN transaction_evaluations.rules_fired IS
    'JSONB snapshot of which rules fired and their score contributions. '
    'Stored denormalised so rule updates/deletions do not corrupt the historical '
    'record of what was evaluated. Format: [{rule_id, name, score_contribution}]';

COMMENT ON COLUMN transaction_evaluations.transaction_id IS
    'UNIQUE constraint enforces exactly-once evaluation. '
    'Duplicate INSERT (from Kafka re-processing) raises constraint violation, '
    'which the consumer catches and treats as already-processed.';

-- =============================================================================
-- V003__create_fraud_alerts.sql
-- Fraud Detection System — fraud_alerts table
--
-- ROLLBACK:
--   DROP TABLE IF EXISTS fraud_alerts CASCADE;
-- =============================================================================

CREATE TABLE fraud_alerts (
    id               VARCHAR(36)     NOT NULL,
    transaction_id   VARCHAR(36)     NOT NULL,
    risk_score       SMALLINT        NOT NULL,
    decision         fraud_decision  NOT NULL,
    severity         alert_severity  NOT NULL,
    status           alert_status    NOT NULL DEFAULT 'open',
    notes            TEXT            NULL,
    resolved_by      VARCHAR(36)     NULL,
    resolution       VARCHAR(32)     NULL,
    created_at       TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    resolved_at      TIMESTAMPTZ     NULL,

    CONSTRAINT fraud_alerts_pkey PRIMARY KEY (id),

    CONSTRAINT fraud_alerts_txn_fk
        FOREIGN KEY (transaction_id)
        REFERENCES transaction_evaluations (transaction_id)
        ON DELETE RESTRICT,

    CONSTRAINT fraud_alerts_resolution_check
        CHECK (
            resolution IN ('false_positive', 'confirmed_fraud', 'under_review')
            OR resolution IS NULL
        ),

    CONSTRAINT fraud_alerts_resolved_consistency
        CHECK (
            (status = 'resolved' AND resolved_at IS NOT NULL AND resolved_by IS NOT NULL)
            OR
            (status = 'open' AND resolved_at IS NULL)
        ),

    CONSTRAINT fraud_alerts_score_range CHECK (risk_score BETWEEN 0 AND 100)
);

COMMENT ON TABLE fraud_alerts IS
    'Analyst work queue. Created for every REVIEW or BLOCK decision. '
    'resolved_consistency CHECK ensures resolved alerts always have a resolver and timestamp.';

-- =============================================================================
-- V004__create_user_behaviour_profiles.sql
-- Fraud Detection System — user_behaviour_profiles table
--
-- ROLLBACK:
--   DROP TABLE IF EXISTS user_behaviour_profiles CASCADE;
-- =============================================================================

CREATE TABLE user_behaviour_profiles (
    user_id                  VARCHAR(36)    NOT NULL,
    avg_transaction_amount   DECIMAL(19, 4) NOT NULL DEFAULT 0,
    transaction_count        INTEGER        NOT NULL DEFAULT 0,
    common_locations         JSONB          NOT NULL DEFAULT '[]',
    last_transaction_lat     DECIMAL(10, 7) NULL,
    last_transaction_lng     DECIMAL(10, 7) NULL,
    last_transaction_at      TIMESTAMPTZ    NULL,
    updated_at               TIMESTAMPTZ    NOT NULL DEFAULT NOW(),

    CONSTRAINT user_behaviour_profiles_pkey PRIMARY KEY (user_id),
    CONSTRAINT profile_avg_amount_non_negative CHECK (avg_transaction_amount >= 0),
    CONSTRAINT profile_transaction_count_non_negative CHECK (transaction_count >= 0),

    CONSTRAINT profile_location_consistency
        CHECK (
            (last_transaction_lat IS NULL AND last_transaction_lng IS NULL)
            OR
            (last_transaction_lat IS NOT NULL AND last_transaction_lng IS NOT NULL)
        )
);

COMMENT ON TABLE user_behaviour_profiles IS
    'Materialised user behaviour summary. Primary input to anomaly detection rules. '
    'Upserted after every evaluation. Cached in Redis (TTL 30min); '
    'this table is the persistent backing store.';

COMMENT ON COLUMN user_behaviour_profiles.common_locations IS
    'Top N known transaction locations. Format: [{lat, lng, count}]. '
    'Used by new_location rule to flag transactions from unknown areas.';

-- =============================================================================
-- V005__add_indexes.sql
-- Fraud Detection System — All performance indexes
--
-- ROLLBACK (reverse order):
--   DROP INDEX IF EXISTS rules_active_priority_idx;
--   DROP INDEX IF EXISTS alerts_txn_id_idx;
--   DROP INDEX IF EXISTS alerts_status_created_at_idx;
--   DROP INDEX IF EXISTS evaluations_user_evaluated_at_idx;
--   DROP INDEX IF EXISTS evaluations_evaluated_at_idx;
-- =============================================================================

-- Query: Time-range analytics — all evaluations in last N hours
CREATE INDEX evaluations_evaluated_at_idx
    ON transaction_evaluations (evaluated_at DESC);

-- Query: User evaluation history — "Has this user been flagged recently?"
CREATE INDEX evaluations_user_evaluated_at_idx
    ON transaction_evaluations (user_id, evaluated_at DESC);

-- Query: Analyst alert queue — open alerts, newest first
CREATE INDEX alerts_status_created_at_idx
    ON fraud_alerts (status, created_at DESC);

COMMENT ON INDEX alerts_status_created_at_idx IS
    'Primary analyst query: GET /alerts?status=open ordered by created_at DESC. '
    'Compound index allows index scan without filtering the full table.';

-- Query: "Is there an alert for transaction X?"
CREATE INDEX alerts_txn_id_idx
    ON fraud_alerts (transaction_id);

-- Query: Load active rules in priority order for evaluation engine cache refresh
CREATE INDEX rules_active_priority_idx
    ON fraud_rules (is_active, priority ASC)
    WHERE is_active = TRUE;

COMMENT ON INDEX rules_active_priority_idx IS
    'Partial index: only active rules in priority order. '
    'Used by the 60-second rule cache refresh — returns only evaluatable rules.';

ANALYZE fraud_rules;
ANALYZE transaction_evaluations;
ANALYZE fraud_alerts;
ANALYZE user_behaviour_profiles;
