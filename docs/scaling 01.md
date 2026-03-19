# Scaling Strategy — Real-Time Fraud Detection System

---

## Horizontal Scaling Table

| Component                | Scales Horizontally? | Notes                                                        |
|--------------------------|---------------------|--------------------------------------------------------------|
| Kafka Consumer Workers   | ✅ Yes               | Scale up to Kafka partition count (8 for `transactions` topic)|
| Rule Engine              | ✅ Yes               | In-memory cache per instance; independent refresh            |
| FraudEvaluationService   | ✅ Yes               | Stateless; scales with consumer instances                    |
| REST API (analyst tools) | ✅ Yes               | Low traffic; 2–3 instances sufficient                        |
| Redis (velocity/profiles)| ✅ Yes (Cluster)     | Shard by user_id for velocity counter locality               |
| PostgreSQL (evaluations) | ❌ No (writes)       | Single primary; very high write rate → NVMe storage critical |
| PostgreSQL (analyst reads)| ✅ Yes               | Replicas for `GET /alerts`, rule reads                       |
| Kafka                    | ✅ Yes               | Add brokers + partitions; always add consumers in sync       |

---

## Load Balancing — Fraud Detection Has No Public Hot Path

The fraud engine is event-driven. There is no public client-facing endpoint with
high QPS. The primary "traffic" is Kafka message consumption — which scales by
adding consumer instances to the `fraud-engine` consumer group, not by adding
load-balanced API instances.

**REST API (analyst tools):**
```
Algorithm:  Round-Robin
Affinity:   None required
Health:     GET /health every 10s
```

**Kafka Consumer Group:**
```
Max instances:  8 (partition count of 'transactions' topic)
Scale trigger:  Consumer lag > 5,000 messages on any partition
Scale action:   Add consumer instance (up to 8 total)
Scale down:     Consumer lag = 0 for 5 consecutive minutes
```

---

## Kafka Partition Strategy

The `transactions` topic is partitioned by `user_id`. This ensures:
1. All transactions for a given user arrive at the same consumer instance.
2. Velocity counters (per-user window counts) do not require cross-instance
   coordination — all data for one user lives in one consumer's Redis commands.
3. Per-user event ordering is preserved (geo-velocity calculations depend on
   sequential event processing).

**Partition count:** 8 (current). Can be increased but cannot be decreased.
Consumer count must be ≤ partition count for load distribution.

---

## Stateless Design Guarantees

1. **No per-instance evaluation state.** Each evaluation is self-contained:
   load profile from Redis/DB, load rules from in-memory cache, evaluate,
   write result. The next evaluation for the same user can run on any instance.

2. **Idempotent evaluation.** The `UNIQUE(transaction_id)` constraint means
   a re-processed Kafka message (after crash recovery) produces a silently-skipped
   INSERT — the second evaluation result is not stored.

3. **Rule cache is eventually consistent.** All instances load rules from the
   same PostgreSQL table and refresh every 60s (+jitter). A rule change takes
   effect on all instances within one refresh cycle without any coordination.

---

## Scaling Triggers

| Metric                                    | Threshold       | Action                                   |
|-------------------------------------------|-----------------|------------------------------------------|
| Kafka consumer lag (`transactions`)       | > 5,000 msgs    | Add consumer instance (max 8)            |
| Evaluation p99 latency                    | > 40ms          | Investigate PostgreSQL INSERT performance|
| Redis memory utilisation                  | > 75%           | Increase memory; review profile TTLs     |
| PostgreSQL evaluations table row count    | > 100M          | Partition by month; archive old data     |
| Dead letter topic (`transactions.dlq`)    | Any new message | Alert + manual investigation             |
