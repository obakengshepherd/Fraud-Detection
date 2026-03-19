# Performance — Real-Time Fraud Detection System

---

## Current Bottlenecks

### Bottleneck 1: Evaluation latency budget constraint
The 50ms p99 target allocates ~20ms to the PostgreSQL evaluation INSERT — the
largest single step. This is acceptable on fast NVMe storage with connection
pooling, but any spike in PostgreSQL write latency directly breaches the SLA.

**Monitoring target:** Alert if evaluation INSERT p99 exceeds 15ms.

### Bottleneck 2: Rule refresh simultaneous polling
All 8 consumer instances refresh their in-memory rule cache by querying PostgreSQL
every 60 seconds. Without jitter, all 8 instances query at the same second.

**Mitigation:** Add random jitter of 0–10 seconds: `60s + Random.Shared.Next(0, 10)`.

### Bottleneck 3: Velocity counter precision under high volume
The `INCR + EXPIRE` velocity counter approximates the sliding window. Under very
high transaction volume for a single user (e.g. a merchant processing 1,000 txn/min),
the approximation may allow brief over-counting at window boundaries.

**Mitigation acceptable for fraud.** False-positive rate at boundary is <2% in tests.
True sliding window (sorted set) available if needed but costs 10x more Redis ops.

---

## Cache Hit Rate Targets

| Key Pattern                          | Target Hit Rate | TTL    | Notes                           |
|--------------------------------------|-----------------|--------|---------------------------------|
| `profile:{userId}`                   | ≥ 85%           | 30 min | New/rare users miss; warm users hit |
| `velocity:{userId}:{rule}:{window}s` | N/A             | {window}s | Counter — always written    |

**Profile cache miss rate higher than 15%?** Investigate:
- Redis memory pressure causing LRU eviction of profile keys
- New user flood (legitimate or attack) hitting the system
- TTL set too short relative to average session length

---

## Database Read Replica Routing

| Operation                          | Target        | Reason                                 |
|------------------------------------|---------------|----------------------------------------|
| `INSERT evaluation result`         | **Primary**   | Write path — must be primary           |
| `UPSERT user_behaviour_profiles`   | **Primary**   | Write path                             |
| `SELECT * FROM fraud_rules`        | Read replica  | Config refresh — 60s eventual OK       |
| `GET /alerts` (analyst list)       | Read replica  | Display query; analyst latency 100ms OK|
| `POST /alerts/{id}/resolve`        | **Primary**   | Write path                             |
| `GET /transactions/{id}/risk-score`| Read replica  | Historical lookup; eventual OK         |

---

## Connection Pool Sizing

| Setting               | Value | Rationale                                             |
|-----------------------|-------|-------------------------------------------------------|
| Max pool per consumer | 5     | Each consumer holds one connection briefly per INSERT |
| Max pool per API      | 10    | Analyst API is low-traffic                            |
| PgBouncer mode        | Transaction | Short-hold connections appropriate here          |

---

## Query Performance Targets

| Query                                          | Target p95 | Target p99 | Index                                 |
|------------------------------------------------|-----------|-----------|---------------------------------------|
| `INSERT evaluation` (ON CONFLICT DO NOTHING)  | < 8ms     | < 15ms    | PK + `transaction_evaluations_txn_unique` |
| `UPSERT user_behaviour_profiles`              | < 6ms     | < 12ms    | PK (user_id)                          |
| `SELECT rules WHERE is_active ORDER BY priority` | < 2ms  | < 5ms     | `rules_active_priority_idx` (partial) |
| `SELECT alerts WHERE status=open ORDER BY created_at` | < 8ms | < 15ms | `alerts_status_created_at_idx`   |
| Redis INCR velocity counter                    | < 1ms     | < 2ms     | Redis O(1) string op                  |
| Redis GET profile                              | < 1ms     | < 2ms     | Redis O(1) string op                  |

---

## Rate Limiting Configuration

| Policy            | Limit  | Window | Scope       | Endpoint                        |
|-------------------|--------|--------|-------------|----------------------------------|
| service-evaluate  | 500    | 1 min  | Per service | `POST /transactions/evaluate`    |
| authenticated     | 60     | 1 min  | Per user    | All other authenticated endpoints|
| unauthenticated   | 10     | 1 min  | Per IP      | Public endpoints                 |
