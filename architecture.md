# Architecture — Real-Time Fraud Detection System

---

## Overview

The Fraud Detection System is an event-driven, stream-processing architecture. It does not
sit inline with the payment pipeline — it is a downstream consumer that evaluates transactions
asynchronously. This design means the payment flow is never blocked waiting for a fraud
decision, and the fraud engine can be evolved, scaled, and deployed independently. Transactions
arrive via a Kafka topic, are evaluated against a configurable rule set, and produce a
`FraudDecision` event that the upstream payment or wallet system can consume to take follow-up
action.

---

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────┐
│         Upstream Systems (Wallet, Payment Processing)       │
│              PUBLISH to: kafka/transactions                 │
└──────────────────────────────┬──────────────────────────────┘
                               │ Kafka: transactions topic
┌──────────────────────────────▼──────────────────────────────┐
│                   Fraud Consumer (Worker)                    │
│              (Kafka consumer group: fraud-engine)           │
└──────────────────────────────┬──────────────────────────────┘
                               │
┌──────────────────────────────▼──────────────────────────────┐
│                   Fraud Evaluation Engine                    │
│        (RuleEngine · VelocityChecker · ProfileLoader)       │
└────────────┬─────────────────────────────────────┬──────────┘
             │                                     │
┌────────────▼──────────┐            ┌─────────────▼──────────┐
│         Redis          │            │       PostgreSQL        │
│  (velocity counters    │            │  (evaluation results   │
│   behaviour profiles)  │            │   rules · alerts)      │
└────────────────────────┘            └────────────┬───────────┘
                                                   │
                               ┌───────────────────▼───────────┐
                               │   Kafka: fraud.decisions topic │
                               │  (consumed by payment systems) │
                               └───────────────────────────────┘
                                           ↑
                               ┌───────────┴──────────┐
                               │      Fraud API        │
                               │ (alerts · rules CRUD) │
                               └──────────────────────┘
```

---

## Layer-by-Layer Description

### Transaction Ingestion — Kafka Consumer

The system consumes from the `transactions` topic in Kafka using a dedicated consumer group
(`fraud-engine`). Messages on this topic are produced by the Digital Wallet System and the
Payment Processing System whenever a financial transaction occurs. The topic is partitioned
by `user_id`, which means all transactions for a given user arrive at the same consumer
instance in order — critical for velocity and sequence-based fraud detection.

The consumer is responsible for exactly-one processing: it commits the Kafka offset only
after the evaluation result has been written to PostgreSQL. If the process crashes after
evaluation but before commit, the message will be re-consumed and re-evaluated. The rule
engine and all downstream writes are therefore designed to be idempotent: a duplicate
evaluation for the same `transaction_id` overwrites the previous result rather than
creating a duplicate record.

### Fraud Evaluation Engine

The evaluation engine is the core of the system. For each consumed transaction, it executes
the following steps:

1. **Load behaviour profile**: Retrieve the user's profile from Redis cache. On cache miss,
   load from PostgreSQL and warm the cache. The profile contains average transaction amount,
   known geographic locations, transaction frequency baseline, and last-seen timestamp.

2. **Load active rules**: Rules are loaded from PostgreSQL at startup and cached in memory.
   They are refreshed every 60 seconds to pick up new or deactivated rules without a restart.

3. **Execute rules in priority order**: Each rule is evaluated against the transaction and
   the behaviour profile. Rules that fire contribute a score value. Currently implemented
   rule types:
   - **Velocity rule**: Use Redis `INCR` and `EXPIRE` to count transactions per user in a
     rolling time window. If the count exceeds the threshold, the rule fires.
   - **Amount anomaly rule**: Compare the transaction amount to the user's running average.
     Flag if the amount exceeds a configured multiplier of the average.
   - **Geo-velocity rule**: Compare the location and timestamp of this transaction to the
     previous one. Flag if travel between the two points would require impossible speed.
   - **New location rule**: Flag if the transaction location has not appeared in the user's
     known locations set.

4. **Compute risk score**: Aggregate the scores of all fired rules into a final risk score
   (0–100).

5. **Make a decision**: Map the score to a decision:
   - 0–30: ALLOW
   - 31–70: REVIEW (flag for analyst attention, allow through)
   - 71–100: BLOCK

6. **Persist evaluation result**: Write the `transaction_evaluations` record including the
   complete rule-firing trace to PostgreSQL.

7. **Update behaviour profile**: Increment velocity counters in Redis; update the profile
   with the new transaction's data.

8. **Publish decision event**: Publish a `FraudDecision` event to the `fraud.decisions`
   Kafka topic.

### Rule Engine — Configuration Model

Rules are stored in the `fraud_rules` PostgreSQL table as structured JSON (`rule_logic`
column). Each rule defines its type, parameters (threshold, window, multiplier), score
contribution, and active flag. The evaluation engine loads and caches rules in memory,
refreshing every 60 seconds. Activating a new rule in the database takes effect within one
refresh cycle — no deployment is required.

### Cache — Redis

Redis serves two purposes in the fraud engine:

**Velocity counters**: For each user and rule window (e.g. "transactions in last 5 minutes"),
a Redis key `velocity:{user_id}:{window_key}` is incremented with `INCR` and given an `EXPIRE`
equal to the window duration. This provides an O(1) sliding window approximation without any
database query on the hot evaluation path.

**Behaviour profiles**: User profiles are cached under `profile:{user_id}` with a 30-minute
TTL. The profile is a JSON blob containing all signals needed for evaluation. Cache misses
fall back to PostgreSQL. Cache writes happen after every evaluation to keep the profile
current.

### Database — PostgreSQL

PostgreSQL stores the authoritative records for rules, evaluation results, and alerts. The
`transaction_evaluations` table is append-only and serves as the audit trail. The `fraud_rules`
table is the source of truth for the active rule set. The `fraud_alerts` table stores alerts
generated by BLOCK or REVIEW decisions, with analyst resolution workflow fields.

### Fraud API

The Fraud API provides a management interface: create and deactivate rules, list and resolve
alerts, and query evaluation history for a transaction. It does not sit on the real-time
evaluation path — it is an operational and analyst interface only.

---

## Component Responsibilities Summary

| Component            | Responsibility                                           | Communicates Via       |
|----------------------|----------------------------------------------------------|------------------------|
| Kafka Consumer       | Consume transactions, manage offset commit after write   | Kafka protocol         |
| RuleEngine           | Load rules, execute in priority order, aggregate score   | In-memory + PostgreSQL |
| VelocityChecker      | Sliding window transaction counts per user               | Redis                  |
| ProfileLoader        | Load and cache user behaviour profiles                   | Redis + PostgreSQL     |
| Redis                | Velocity counters + behaviour profile cache              | In-memory              |
| PostgreSQL           | Rules, evaluation results, alerts (source of truth)      | TCP                    |
| Kafka (decisions)    | Publish FraudDecision events to downstream consumers     | Kafka protocol         |
| Fraud API            | Rule management, alert resolution, evaluation history    | HTTP                   |
