# Data Model — Real-Time Fraud Detection System

---

## Database Technology Choices

### PostgreSQL (Rules, evaluations, alerts)
The fraud rules, evaluation results, and alert records require strong consistency and
reliable querying for analyst workflows. PostgreSQL's `JSONB` type stores rule parameters
and evaluation payloads with efficient key-based indexing — analysts can query which
rules fired most frequently without deserialising every row in the application layer.

### Redis (Velocity counters and behaviour profiles)
Velocity counters (`INCR` + `EXPIRE` per user per time window) and user behaviour profiles
are held in Redis for sub-millisecond access on the hot evaluation path. These are not
stored in PostgreSQL — a database roundtrip for every velocity counter check would make
the 50ms p99 evaluation latency target impossible to hit. Redis data is ephemeral and
self-healing: a counter reset after a Redis restart causes a missed detection at worst,
not a data integrity failure.

---

## Entity Relationship Overview

A **FraudRule** defines a detection pattern (velocity, amount anomaly, geo-velocity, new
location) with its parameters, priority, and score contribution. Rules are loaded into
memory by the evaluation engine and refreshed every 60 seconds.

A **TransactionEvaluation** is the output of evaluating one transaction against all active
rules. It records the final risk score, decision, and the complete `rules_fired` trace as
JSONB — enabling full explainability without joining multiple tables.

A **FraudAlert** is created when a transaction receives a `REVIEW` or `BLOCK` decision.
Alerts are the analyst-facing work queue. One evaluation produces at most one alert.

A **UserBehaviourProfile** is a materialised summary of each user's transaction history
(average amount, velocity baseline, known locations). It is the primary input to anomaly
detection rules. It is updated after every evaluation and cached in Redis.

---

## Table Definitions

### `fraud_rules`

| Column        | Type          | Constraints                         | Description                                          |
|---------------|---------------|-------------------------------------|------------------------------------------------------|
| `id`          | `VARCHAR(36)` | PRIMARY KEY                         | Prefixed UUID: `rule_<uuid>`                         |
| `name`        | `VARCHAR(128)`| NOT NULL                            | Human-readable rule name                             |
| `type`        | `rule_type`   | NOT NULL                            | Enum: `velocity`, `amount_anomaly`, `geo_velocity`, `new_location` |
| `rule_logic`  | `JSONB`       | NOT NULL                            | Rule parameters (threshold, window, multiplier, etc.)|
| `score`       | `SMALLINT`    | NOT NULL, CHECK (score BETWEEN 1 AND 100) | Score contribution when this rule fires         |
| `priority`    | `SMALLINT`    | NOT NULL, CHECK (priority BETWEEN 1 AND 100) | Lower value = evaluated first               |
| `is_active`   | `BOOLEAN`     | NOT NULL, DEFAULT TRUE              | Inactive rules are loaded but not evaluated          |
| `description` | `TEXT`        | NULL                                | Analyst-facing description                           |
| `created_at`  | `TIMESTAMPTZ` | NOT NULL, DEFAULT NOW()             | Immutable                                            |
| `created_by`  | `VARCHAR(36)` | NOT NULL                            | Admin user who created the rule                      |

### `transaction_evaluations`

| Column             | Type           | Constraints                          | Description                                     |
|--------------------|----------------|--------------------------------------|-------------------------------------------------|
| `id`               | `VARCHAR(36)`  | PRIMARY KEY                          | Prefixed UUID: `eval_<uuid>`                    |
| `transaction_id`   | `VARCHAR(36)`  | NOT NULL, UNIQUE                     | One evaluation per transaction (idempotent)     |
| `user_id`          | `VARCHAR(36)`  | NOT NULL                             | Denormalised for query efficiency               |
| `risk_score`       | `SMALLINT`     | NOT NULL, CHECK (risk_score BETWEEN 0 AND 100) | Aggregated score                    |
| `decision`         | `fraud_decision`| NOT NULL                            | Enum: `allow`, `review`, `block`                |
| `rules_fired`      | `JSONB`        | NOT NULL, DEFAULT '[]'               | Array of `{rule_id, name, score_contribution}`  |
| `model_version`    | `VARCHAR(16)`  | NOT NULL, DEFAULT '1.0.0'            | Rule set version at evaluation time             |
| `evaluated_at`     | `TIMESTAMPTZ`  | NOT NULL, DEFAULT NOW()              | Immutable                                       |

**Why `rules_fired` is JSONB (denormalised)?** An evaluation is immutable after writing.
If we stored fired rules as a separate join table, a future rule update (name change,
deletion) would corrupt the historical record of what was evaluated. Denormalising the
rule trace into JSONB at evaluation time preserves an exact snapshot of what fired and
why — essential for dispute resolution and regulatory audit.

**Why UNIQUE on `transaction_id`?** Ensures exactly-once evaluation semantics at the
database level. If the Kafka consumer crashes and re-processes a message, the duplicate
evaluation INSERT fails with a constraint violation rather than creating a second row.

### `fraud_alerts`

| Column           | Type          | Constraints                       | Description                               |
|------------------|---------------|-----------------------------------|-------------------------------------------|
| `id`             | `VARCHAR(36)` | PRIMARY KEY                       | Prefixed UUID: `alrt_<uuid>`              |
| `transaction_id` | `VARCHAR(36)` | NOT NULL, FK → transaction_evaluations(`transaction_id`) | Reference to evaluation |
| `risk_score`     | `SMALLINT`    | NOT NULL                          | Denormalised for alert list queries       |
| `decision`       | `fraud_decision`| NOT NULL                        | Denormalised                              |
| `severity`       | `alert_severity`| NOT NULL                        | Enum: `low`, `medium`, `high`, `critical` |
| `status`         | `alert_status`| NOT NULL, DEFAULT 'open'          | Enum: `open`, `resolved`                  |
| `notes`          | `TEXT`        | NULL                              | Analyst resolution notes                  |
| `resolved_by`    | `VARCHAR(36)` | NULL                              | Analyst user ID                           |
| `resolution`     | `VARCHAR(32)` | NULL                              | `false_positive`, `confirmed_fraud`, `under_review` |
| `created_at`     | `TIMESTAMPTZ` | NOT NULL, DEFAULT NOW()           | When the alert was created                |
| `resolved_at`    | `TIMESTAMPTZ` | NULL                              | When the analyst resolved it              |

### `user_behaviour_profiles`

| Column                    | Type           | Constraints              | Description                                    |
|---------------------------|----------------|--------------------------|------------------------------------------------|
| `user_id`                 | `VARCHAR(36)`  | PRIMARY KEY              | One profile per user                           |
| `avg_transaction_amount`  | `DECIMAL(19,4)`| NOT NULL, DEFAULT 0      | Rolling average of transaction amounts         |
| `transaction_count`       | `INTEGER`      | NOT NULL, DEFAULT 0      | Total transaction count for averaging          |
| `common_locations`        | `JSONB`        | NOT NULL, DEFAULT '[]'   | Array of `{lat, lng, count}` — top N locations |
| `last_transaction_lat`    | `DECIMAL(10,7)`| NULL                     | For geo-velocity calculations                  |
| `last_transaction_lng`    | `DECIMAL(10,7)`| NULL                     | For geo-velocity calculations                  |
| `last_transaction_at`     | `TIMESTAMPTZ`  | NULL                     | For geo-velocity and velocity calculations     |
| `updated_at`              | `TIMESTAMPTZ`  | NOT NULL, DEFAULT NOW()  | Updated after each evaluation                  |

---

## Index Strategy

| Index Name                               | Table                     | Columns                         | Type    | Query Pattern                              |
|------------------------------------------|---------------------------|---------------------------------|---------|--------------------------------------------|
| `evaluations_transaction_id_uniq`        | `transaction_evaluations` | `(transaction_id)`              | UNIQUE  | Idempotency guard + lookup by transaction  |
| `evaluations_user_id_evaluated_at_idx`   | `transaction_evaluations` | `(user_id, evaluated_at DESC)`  | B-tree  | User evaluation history                    |
| `evaluations_evaluated_at_idx`           | `transaction_evaluations` | `(evaluated_at DESC)`           | B-tree  | Time-range queries for analytics           |
| `alerts_status_created_at_idx`           | `fraud_alerts`            | `(status, created_at DESC)`     | B-tree  | Analyst alert queue — open alerts by time  |
| `alerts_transaction_id_idx`              | `fraud_alerts`            | `(transaction_id)`              | B-tree  | Lookup alert for a given transaction       |
| `rules_is_active_priority_idx`           | `fraud_rules`             | `(is_active, priority)`         | B-tree  | Load active rules in priority order        |

---

## Relationship Types

- **FraudRule** is standalone — it has no foreign key relationships (it is a configuration entity).
- **TransactionEvaluation** is standalone on `transaction_id` (the transaction lives in the Payment/Wallet system, not this database).
- **FraudAlert → TransactionEvaluation**: one-to-one via `transaction_id`.
- **UserBehaviourProfile** is keyed by `user_id` — one row per user, upserted after each evaluation.

---

## Soft Delete Strategy

Fraud rules are never deleted — they are deactivated by setting `is_active = FALSE`.
Deleting a rule would lose the audit trail of which rules were active during historical
evaluations (though the evaluation's `rules_fired` JSONB preserves the snapshot).

Alerts and evaluations are never deleted. They form the regulatory audit trail.

---

## Audit Trail

| Table                     | `created_at`   | `updated_at` | Notes                                           |
|---------------------------|----------------|--------------|-------------------------------------------------|
| `fraud_rules`             | ✓              | ✗            | Immutable; only `is_active` changes             |
| `transaction_evaluations` | `evaluated_at` | ✗            | Completely immutable after insert               |
| `fraud_alerts`            | ✓              | `resolved_at`| `resolved_at` set once on resolution            |
| `user_behaviour_profiles` | ✗              | ✓            | Updated after every evaluation                  |
