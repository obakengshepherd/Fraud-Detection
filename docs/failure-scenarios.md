# Failure Scenarios — Real-Time Fraud Detection System

> **Status**: Skeleton — stubs defined on Day 2. Full mitigations and implementations added on Day 27.

---

## Scenario 1 — Consumer Lag — Transactions Piling Up

**Trigger**: Consumer instances cannot process transactions as fast as they are produced.
Kafka topic lag grows. Fraud decisions are delayed.

**Component that fails**: Consumer instance throughput / upstream transaction spike.

**Impact**: Internal — fraud decisions are delayed but not lost. Upstream payment systems
continue to allow transactions without a fraud signal during the lag period.

**Mitigation strategy**: TBD Day 27 — involves consumer instance auto-scaling up to
partition count, lag alerting, and documented SLA for when lag constitutes an incident.

---

## Scenario 2 — Redis Velocity Counters Lost

**Trigger**: Redis is restarted without persistence. All velocity counter keys are lost.
The fraud engine resumes but treats every user's velocity as zero.

**Component that fails**: Redis (ephemeral data loss).

**Impact**: Internal — velocity-based rules fire no results until counters are rebuilt
naturally through subsequent transactions. Fraud engine continues operating in a degraded
detection state for the counter TTL window.

**Mitigation strategy**: TBD Day 27 — involves Redis AOF persistence, graceful degradation
logic (missing counters treated as zero, not as errors), and alerting on Redis restart events.

---

## Scenario 3 — Rule Update Takes Effect on Live Transactions

**Trigger**: An analyst updates a fraud rule in the database (changes a velocity threshold).
The in-memory rule cache has not yet refreshed. Some consumer instances are running the old
rule; others have refreshed and run the new rule.

**Component that fails**: Rule cache refresh consistency across consumer instances.

**Impact**: Internal — during the 60-second refresh window, different consumer instances may
make different decisions on similar transactions. Not a data integrity issue, but a
consistency window.

**Mitigation strategy**: TBD Day 27 — involves documenting this as an accepted eventual
consistency window, adding a rule version field to evaluation records, and providing a
force-refresh endpoint for critical rule changes.

---

## Scenario 4 — FraudDecision Publish Fails

**Trigger**: The Kafka broker is unavailable when the evaluation engine attempts to publish
a `FraudDecision` event after writing the evaluation result to PostgreSQL.

**Component that fails**: Kafka broker (outbound publish).

**Impact**: Internal and user-facing — the evaluation result is persisted correctly, but the
upstream payment system does not receive the decision event. A BLOCK decision is not
communicated to the payment gateway.

**Mitigation strategy**: TBD Day 27 — involves retry with backoff on publish, outbox pattern
as fallback (write decision to a PostgreSQL outbox table, replay via a separate relay process).

---

## Scenario 5 — Behaviour Profile Cache Eviction Under Load

**Trigger**: Redis memory pressure causes behaviour profile keys to be evicted via LRU
policy. Many consecutive evaluations result in PostgreSQL profile reads on the hot path.

**Component that fails**: Redis memory / LRU eviction policy.

**Impact**: Internal — evaluation latency spikes as PostgreSQL absorbs cache miss reads.
Latency SLA (50ms p99) may be breached temporarily.

**Mitigation strategy**: TBD Day 27 — involves Redis `maxmemory-policy allkeys-lru`
configuration, memory monitoring with alerts before eviction threshold is reached, and
profile pre-warming after Redis restarts.
