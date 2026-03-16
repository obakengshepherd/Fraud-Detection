-- =============================================================================
-- V001__create_fraud_rules.sql
-- Fraud Detection System — Custom types + fraud_rules table
--
-- ROLLBACK:
--   DROP TABLE IF EXISTS fraud_rules CASCADE;
--   DROP TYPE IF EXISTS rule_type;
--   DROP TYPE IF EXISTS fraud_decision;
--   DROP TYPE IF EXISTS alert_severity;
--   DROP TYPE IF EXISTS alert_status;
-- =============================================================================

CREATE TYPE rule_type      AS ENUM ('velocity', 'amount_anomaly', 'geo_velocity', 'new_location');
CREATE TYPE fraud_decision  AS ENUM ('allow', 'review', 'block');
CREATE TYPE alert_severity  AS ENUM ('low', 'medium', 'high', 'critical');
CREATE TYPE alert_status    AS ENUM ('open', 'resolved');

CREATE TABLE fraud_rules (
    id           VARCHAR(36)  NOT NULL,
    name         VARCHAR(128) NOT NULL,
    type         rule_type    NOT NULL,
    rule_logic   JSONB        NOT NULL,
    score        SMALLINT     NOT NULL,
    priority     SMALLINT     NOT NULL,
    is_active    BOOLEAN      NOT NULL DEFAULT TRUE,
    description  TEXT         NULL,
    created_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    created_by   VARCHAR(36)  NOT NULL,

    CONSTRAINT fraud_rules_pkey PRIMARY KEY (id),
    CONSTRAINT fraud_rules_score_range CHECK (score BETWEEN 1 AND 100),
    CONSTRAINT fraud_rules_priority_range CHECK (priority BETWEEN 1 AND 100)
);

COMMENT ON TABLE fraud_rules IS
    'Configurable fraud detection rules. is_active = FALSE to disable without deletion. '
    'Loaded into application memory and refreshed every 60 seconds. '
    'New rules activate on next refresh cycle — no deployment required.';

COMMENT ON COLUMN fraud_rules.rule_logic IS
    'JSONB parameters specific to the rule type. Examples: '
    'velocity: {"threshold": 5, "window_seconds": 300} '
    'amount_anomaly: {"multiplier": 3.0} '
    'geo_velocity: {"max_speed_kmh": 900} '
    'new_location: {"min_known_transactions": 3}';
