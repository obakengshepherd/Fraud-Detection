# Failure Scenarios — Real-Time Fraud Detection System

> **Status**: Complete — Days 25–27 implementation. Replaces Phase 1 skeleton.

---

## Scenario 1 — Kafka Consumer Lag (Transactions Pile Up)

**Trigger**
Transaction volume spikes (flash sale, market event). Consumer instances cannot
process transactions as fast as they are produced. The `transactions` Kafka topic
accumulates a backlog. Consumer group lag grows.

**Affected Components**
TransactionKafkaConsumer, `transactions` Kafka topic, all consumer instances.

**User-Visible Impact**
None directly. Fraud decisions are delayed. Transactions that should trigger a
BLOCK decision are allowed to proceed for the duration of the lag. This is a
known and accepted tradeoff: the payment pipeline cannot be gated on fraud
evaluation throughput.

**System Behaviour Without Mitigation**
Consumer lag grows unboundedly. Decisions arrive minutes or hours after the
transaction. By then, the funds may already have moved. Real-time fraud detection
becomes retrospective detection.

**Mitigation**

1. **Scale consumers up to partition count:** The `transactions` topic has 8
   partitions. Consumer group `fraud-engine` can have up to 8 instances before
   adding more does nothing (each partition has at most one consumer).
   Auto-scaling trigger: lag > 5,000 messages on any partition.

2. **Priority lanes:** High-risk transaction types (large amounts, new accounts)
   can be published to a separate high-priority topic with 16 partitions and a
   dedicated consumer group. This prevents low-priority volume from crowding out
   high-risk evaluations.

3. **Evaluation timeout budget:** Each evaluation has a 50ms timeout. Operations
   that exceed this (slow PostgreSQL INSERT, Redis unavailability) are retried
   on the next attempt. The consumer never stalls indefinitely on a single message.

**Detection**
- Alert: `kafka_consumer_group_lag{group="fraud-engine"} > 5000` → add consumer.
- Alert: lag > 10,000 → page on-call immediately.
- Metric: `evaluation_duration_p99` histogram — alert if > 45ms (approaching budget).

---

## Scenario 2 — Redis Velocity Counters Lost (Cache Restart)

**Trigger**
Redis is restarted without AOF persistence, or a `FLUSHALL` command is executed.
All `velocity:{userId}:{rule}:{window}` counter keys are deleted. The fraud engine
resumes but treats every user's velocity as zero for the next window duration.

**Affected Components**
FraudCacheService, all velocity-based fraud rules.

**User-Visible Impact**
None directly. Velocity-based rules fire no detections for up to `window_seconds`
after the Redis restart. During this window, velocity fraud (rapid-fire transactions)
goes undetected.

**System Behaviour Without Mitigation**
Every `INCR velocity:*` starts from 1 after the restart. The rule threshold is
never exceeded within the current window. Fraudsters who know the restart schedule
can exploit the detection gap.

**Mitigation**

1. **Fail open, not closed:** The fraud engine is designed to continue evaluating
   other rule types (amount anomaly, geo-velocity, new location) even when velocity
   counters are zero. A zero velocity score does not clear the other rules — a
   high-amount anomaly still triggers REVIEW regardless.

2. **Redis AOF persistence:** `appendonly yes` in Redis configuration. On restart,
   velocity counters are restored from the AOF log. Recovery time is proportional
   to the log size — typically < 10 seconds for a routine restart.

3. **Alert on Redis restart:** Monitoring detects `redis_restart_total > 0` and
   logs a FRAUD_DETECTION_DEGRADED event. Risk team is notified of the detection
   gap window.

4. **Velocity counter TTL ensures self-healing:** Even without AOF, counters
   rebuild naturally as transactions arrive. Within one window period (e.g., 5
   minutes for a 5-minute velocity rule), the counter reflects actual recent
   activity again.

**Detection**
- Alert: Redis `INFO persistence` shows AOF disabled → immediate remediation.
- Alert: `velocity_counters_total` metric drops to zero → indicates flush or restart.
- Metric: `fraud_rules_fired_by_type{type="velocity"}` rate — sudden drop indicates counter loss.

---

## Scenario 3 — Rule Update Race (Live Evaluation During Rule Change)

**Trigger**
An analyst updates a fraud rule in the database (e.g., changes the velocity
threshold from 5 to 3). The in-memory rule cache across all 8 consumer instances
refreshes asynchronously — not all instances refresh simultaneously.

**Affected Components**
RuleEngineService in-memory cache, all consumer instances.

**User-Visible Impact**
None — brief inconsistency in fraud detection thresholds during the 60-second
refresh window. Some instances use the old threshold, others use the new one.

**System Behaviour Without Mitigation**
A transaction evaluated on an old-cache instance receives a different score than
an identical transaction evaluated on a new-cache instance. This inconsistency
window is bounded by the refresh interval (60 seconds + jitter).

**Mitigation**

1. **Jitter-spread refresh:** Each instance adds `Random.Shared.Next(0, 10)` seconds
   to its 60-second refresh interval. This prevents all instances from querying
   the rule table simultaneously, but spreads the update across ~70 seconds.

2. **Rule version tracking:** The `fraud_rules` table tracks `updated_at`. The
   cache records the `updated_at` of each loaded rule. An instance can detect
   that its cache is stale before the full TTL expires by comparing timestamps.

3. **Accept eventual consistency:** Rule changes are configuration updates, not
   emergency responses. A 70-second propagation window is acceptable. If an
   emergency rule change is needed (e.g., blocking all transactions from a
   compromised merchant), it should be implemented via an application feature
   flag, not a rule update.

**Detection**
- Metric: `rule_cache_age_seconds` histogram. Alert if any instance exceeds 90s.
- Log: Rule cache refresh logged at DEBUG with rule count and version hash.

---

## Scenario 4 — Evaluation Result Write Failure (PostgreSQL INSERT Fails)

**Trigger**
The Kafka consumer successfully evaluates a transaction but fails to INSERT the
`transaction_evaluations` row into PostgreSQL (connection lost, table lock, disk full).

**Affected Components**
FraudRepository, `transaction_evaluations` table, Kafka offset management.

**User-Visible Impact**
None — the upstream payment system has already proceeded. Internal impact: the
evaluation result is lost and the `fraud.decisions` event is not published.

**System Behaviour Without Mitigation**
The consumer commits the Kafka offset even though the evaluation failed. The
transaction is never evaluated. Fraud detection has a silent gap.

**Mitigation**

1. **Manual offset commit after successful INSERT:** The consumer uses
   `EnableAutoCommit = false`. The Kafka offset is committed ONLY after the
   evaluation INSERT succeeds. If the INSERT fails, the consumer retries the
   message (up to 3 times with backoff). If all retries fail, the message is
   sent to the DLQ and the offset is committed.

2. **Idempotent INSERT:** The INSERT uses `ON CONFLICT (transaction_id) DO NOTHING`.
   If the message is re-processed after a crash (offset not committed), the
   second INSERT is silently ignored — no duplicate evaluation.

3. **DLQ for permanent failures:** After 3 failed INSERT attempts, the original
   message is written to `transactions.dlq` for manual investigation. The consumer
   moves on rather than blocking.

**Detection**
- Alert: `kafka_consumer_dlq_writes_total{topic="transactions.dlq"} > 0`
  → evaluation failures occurring; investigate PostgreSQL health.
- Metric: `evaluation_insert_failure_rate` — should be near zero.

---

## Scenario 5 — Alert Queue Overwhelm (Too Many REVIEW Decisions)

**Trigger**
A rule misconfiguration or a fraud wave causes the fraud engine to emit thousands
of REVIEW decisions per minute. The `fraud_alerts` table grows rapidly. Analysts
cannot work through the queue.

**Affected Components**
AlertService, `fraud_alerts` table, analyst workflow tools.

**User-Visible Impact**
Indirect — if genuine fraud alerts are buried in noise, real fraud may go
undetected. Analyst response time increases. Escalations may be missed.

**Mitigation**

1. **Alert deduplication:** Multiple evaluations for the same user within a 5-minute
   window that all result in REVIEW are collapsed into a single alert with an
   `occurrence_count` field updated, not a new row created.

2. **Severity-based routing:** `critical` and `high` severity alerts page on-call
   immediately. `medium` alerts enter an analyst queue. `low` alerts are auto-approved
   after 48 hours without action.

3. **Rule effectiveness monitoring:** Track `alerts_per_rule_per_hour`. A rule that
   generates > 1,000 alerts/hour is likely misconfigured. Alert the engineering team
   to review the threshold.

**Detection**
- Alert: `fraud_alerts_open_count > 5,000` → alert storm; investigate immediately.
- Metric: `alert_creation_rate_by_rule` — spike indicates rule misconfiguration.

---

## Universal Scenarios

### U1 — Kafka Consumer Lag (covered in detail above as Scenario 1)

### U2 — Database Connection Pool Exhaustion
**Specific impact:** Evaluation INSERTs queue up behind connection wait. Consumer
processing stalls. Kafka lag grows. Circuit breaker opens after 5 pool timeouts,
returning 503 to the health endpoint and signalling the orchestrator to pause new
consumer deployments until the issue resolves.

### U3 — Redis Unavailability
**Specific impact:** Profile cache misses fall back to PostgreSQL. Velocity counters
return 0 (fail open). All rules still execute. Detection quality is reduced but
the engine stays operational.
