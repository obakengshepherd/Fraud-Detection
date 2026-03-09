# Scaling Strategy — Real-Time Fraud Detection System

---

## Current Single-Node Bottlenecks

- **Consumer throughput ceiling**: A single Kafka consumer instance processes transactions
  sequentially within each partition. At the defined scale (100% of transactions from the
  Payment System at 200 TPS), a single consumer instance is sufficient. If upstream
  transaction volume grows to thousands per second, more consumer instances are needed —
  but the partition count is the hard limit on parallelism.

- **Redis velocity counter write rate**: Every evaluated transaction writes at least two
  `INCR` commands to Redis (one per active velocity window). At 200 TPS with 5 active
  velocity rules, that is 1,000 Redis writes per second. A single Redis instance handles
  this trivially, but it is the component to monitor as transaction volume scales.

- **In-memory rule cache refresh**: Rules are cached in memory and refreshed every 60
  seconds by polling PostgreSQL. Under normal operation this is a single, cheap query.
  Under very high instance counts (50+ consumer instances), the aggregate polling load on
  PostgreSQL is worth monitoring.

- **Behaviour profile cache misses**: A cold cache (after a Redis restart or for new users)
  means every evaluation hits PostgreSQL for the profile. Under normal operation, the 30-
  minute TTL means warm users never hit the database. The concern is a cache eviction event
  causing a thundering herd of profile reads.

---

## Horizontal Scaling Plan

### Consumer Instances

The Kafka consumer group `fraud-engine` can have at most one active consumer per partition.
The `transactions` topic is partitioned by `user_id`. Start with 8 partitions and 8 consumer
instances. Add partitions and instances proportionally as upstream transaction volume grows.

Because the topic is partitioned by `user_id`, all transactions for a given user always flow
through the same consumer instance. This means per-user velocity state is consistent within
a consumer — no cross-instance coordination is needed for velocity counting.

Target scaling ratio: 1 consumer instance per 50 TPS sustained throughput.

### Rule Engine

The rule engine runs in-process within each consumer instance. It scales automatically with
the consumer — no independent scaling is needed. The only coordination required is that all
instances load rules from the same PostgreSQL table, which they do independently every 60
seconds. Rule updates propagate to all instances within one refresh cycle.

### Redis

A single Redis instance is sufficient at the defined scale. The velocity counter workload
is write-heavy but the individual operations are O(1). Memory footprint is predictable:
one key per active user per velocity window. With 50,000 daily active users and 5 velocity
windows per user, that is 250,000 keys — negligible memory consumption.

If transaction volume scales to the point where Redis becomes a bottleneck, introduce Redis
Cluster with hash-slot partitioning by `user_id`. Velocity counters and behaviour profiles
for the same user land on the same shard, preserving the single-instance semantics.

### PostgreSQL

**Phase 1 — Read replicas for API**: The Fraud API reads (alert listing, evaluation history)
are routed to a read replica. The consumer writes (evaluation results) go to the primary.

**Phase 2 — Partition `transaction_evaluations`**: Partition by month once the table exceeds
100M rows. Older partitions can be moved to cold storage.

**Phase 3 — Profile pre-warming**: On Redis restart or known cold-cache event, run a
background job that pre-loads profiles for all users active in the last 7 days before
consumer instances begin processing. This prevents the thundering herd problem.

---

## Queue Throughput Targets

| Topic                  | Direction  | Expected Rate   | Partition Count | Consumer Instances |
|------------------------|------------|-----------------|-----------------|--------------------|
| `transactions`         | Consume    | 200 TPS         | 8               | Up to 8            |
| `fraud.decisions`      | Produce    | 200 TPS         | 8               | N/A (producer)     |

Monitor consumer group lag on the `transactions` topic continuously. The fraud engine must
keep pace with upstream producers. An acceptable lag is ≤ 1,000 messages. Alert at 5,000
messages lag. At 10,000 messages lag, consider adding consumer instances (after verifying
partition count allows it).

---

## Latency Budget

The p99 evaluation latency target of 50ms breaks down as follows:

| Step                              | Budget   |
|-----------------------------------|----------|
| Kafka message deserialisation     | ~1ms     |
| Redis velocity counter read/write | ~2ms     |
| Behaviour profile Redis read      | ~2ms     |
| Rule execution (in-memory)        | ~3ms     |
| PostgreSQL evaluation write       | ~20ms    |
| Kafka decision publish            | ~10ms    |
| **Total budget**                  | **38ms** |

The PostgreSQL write is the largest variable. Ensure the primary has NVMe storage and
connections are pooled. If write latency spikes, this target is the first to breach.
