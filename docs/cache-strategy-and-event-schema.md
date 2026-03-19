# Cache Strategy & Event Schema — Fraud Detection System

---

## Cache Strategy

### Pattern 1: Cache-Aside (User Behaviour Profiles)

```
Evaluate transaction for user X:
  1. GET profile:{userId}               ← Redis profile cache
  2. HIT  → use cached profile (p99 ~1ms)
  3. MISS → SELECT FROM user_behaviour_profiles WHERE user_id = X
         → SET profile:{userId} {json} EX 1800
         → use profile
  4. After evaluation completes:
         → UPSERT profile in PostgreSQL
         → SET profile:{userId} {updated_json} EX 1800  ← update cache
```

**TTL rationale:** 30 minutes. A user profile that is 30 minutes stale introduces
only minor inaccuracy in anomaly detection — the rolling averages change slowly.
A shorter TTL would increase DB load on the hot evaluation path.

### Pattern 2: Sliding Window Velocity Counter (INCR + EXPIRE)

```
For rule "max 5 transactions in 5 minutes":
  key = velocity:{userId}:{ruleId}:300

  1. INCR velocity:{userId}:{ruleId}:300
     → count = 1: SET EXPIRE key 300   (first event, set TTL to window duration)
     → count > 1: TTL already set, no action needed
  2. Return count
  3. If count > threshold (5) → rule fires
```

Why `INCR + EXPIRE` instead of a sorted set?

A sorted set (`ZADD` + `ZRANGEBYSCORE`) gives exact sliding windows but costs
O(log N) per operation and requires pruning old entries. The `INCR + EXPIRE`
pattern is O(1) and approximates the window — it slightly over-counts at window
boundaries but is accurate enough for fraud detection and dramatically faster.

**No persistence needed:** velocity counters expire naturally with the window.
A Redis restart resets all counters to zero, which means the window restarts
clean — slightly weaker detection for one window period, not a correctness failure.

---

## Key Inventory

| Key Pattern                              | Type   | TTL          | Written When                    |
|------------------------------------------|--------|--------------|---------------------------------|
| `profile:{userId}`                       | String | 30 min       | After each evaluation           |
| `velocity:{userId}:{ruleId}:{window}s`  | String | {window}s    | On first event in window (INCR) |

---

## Event Schema

### Consumes: `transactions` topic
- **Partitioned by:** `user_id`
- **Consumer group:** `fraud-engine`
- **Processing:** Evaluate each transaction, write result, publish decision

**Input message schema:**
```json
{
  "transaction_id": "txn_01j9...",
  "user_id": "usr_abc123",
  "amount": 1500.00,
  "currency": "ZAR",
  "merchant_id": "mrc_xyz789",
  "location_lat": -26.2041,
  "location_lng": 28.0473,
  "timestamp": "2024-01-15T10:30:00Z"
}
```

### Produces: `fraud.decisions` topic
- **Partitioned by:** `user_id` (same as input — consumers can correlate)
- **Consumers:** PaymentService, WalletService, AlertDashboard
- **Retention:** 7 days

**Output message schema:**
```json
{
  "transaction_id": "txn_01j9...",
  "decision": "BLOCK",
  "risk_score": 72,
  "evaluated_at": "2024-01-15T10:30:00Z"
}
```

### Dead Letter: `transactions.dlq`
- Receives messages that failed after 3 retry attempts
- Contains full original message + error context for debugging
- Retention: 30 days
- Replayed manually by operators using `kafka-console-producer` or a replay tool

---

## Consumer Group Lag Policy

The fraud engine must keep pace with upstream payment volume. Acceptable lag: ≤1,000
messages. At lag > 5,000 messages: page on-call and add consumer instances (up to
partition count of 8).

A slow fraud consumer means delayed fraud signals but no blocked payments — producers
never wait for fraud evaluation. This is by design: the payment pipeline must not be
gated on fraud engine availability.
