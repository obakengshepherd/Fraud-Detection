# Problem Statement — Real-Time Fraud Detection System

---

## Section 1 — The Problem.

Every financial transaction carries the risk of fraud. A stolen card, a compromised account,
or an automated bot can initiate hundreds of fraudulent transactions before a human analyst
notices. The only viable defence is automated, real-time evaluation: every transaction must
be assessed for fraud risk before or immediately after it is processed, fast enough that
legitimate customers experience no perceptible delay. The business consequence of getting this
wrong runs in both directions — failing to catch fraud results in direct financial losses and
chargebacks; incorrectly blocking legitimate transactions alienates customers and destroys
conversion rates.

---

## Section 2 — Why It Is Hard

- **Latency constraint**: The fraud evaluation pipeline must complete within 50 milliseconds
  for 99% of transactions. This rules out synchronous database scans of historical data on
  the hot evaluation path. All signals used during evaluation must be pre-computed and
  available in fast in-memory storage.

- **Volume without blocking**: The system must evaluate 100% of transactions in real time
  without becoming a bottleneck in the payment pipeline. The integration model must be
  event-driven: transactions are published to a stream and consumed asynchronously, so the
  payment flow is never waiting for a fraud decision to unblock.

- **Behavioural context**: A single transaction viewed in isolation tells you very little.
  Fraud signals emerge from patterns: many transactions in a short window, a sudden change
  in geography, an amount far outside the user's normal range. This context must be
  maintained per user and kept current as new transactions arrive.

- **Rule management**: Fraud patterns evolve constantly. The system must support a
  configurable rule set that can be updated without a deployment. New rules must activate
  immediately; retired rules must stop influencing decisions without leaving orphaned state.

- **Decision explainability**: When a transaction is blocked or flagged for review, the
  system must record exactly which rules fired, what scores they contributed, and what the
  final decision was. This is required for analyst review, dispute resolution, and
  regulatory audit.

---

## Section 3 — Scope of This Implementation.

**In scope:**

- Event-driven transaction ingestion via Kafka (`transactions` topic)
- Per-user behaviour profile: rolling transaction velocity, average amount, known locations
- Rule-based fraud evaluation engine with configurable, prioritised rules
- Fraud risk scoring (0–100) and decision output: ALLOW, REVIEW, BLOCK
- Evaluation result persistence with full rule-firing trace
- Fraud alert creation and analyst resolution workflow
- Redis-backed velocity counters (sliding window using INCR + EXPIRE)
- User behaviour profile caching in Redis with DB fallback
- FraudDecision event publishing for downstream consumers (payment gateway)

**Out of scope:**

- Machine learning or statistical anomaly detection models
- Graph-based fraud network analysis
- Device fingerprinting
- Real-time analyst dashboard (alerts are stored, retrieval via API only)
- Integration with external fraud data providers or card network signals

---

## Section 4 — Success Criteria.

The system is working correctly when:

1. Every transaction event published to the Kafka `transactions` topic is evaluated exactly
   once, with a decision written and a `FraudDecision` event published downstream.

2. The p99 evaluation latency — from event consumption to decision written — does not
   exceed 50 milliseconds under steady-state load.

3. Velocity rules correctly count transactions within their defined time window, reset
   cleanly after the window expires, and are not affected by Redis restarts (graceful
   degradation: missing counters are treated as zero, not as errors).

4. A new fraud rule activated in the rules table takes effect on the next evaluation cycle
   without requiring any service restart or deployment.

5. Every evaluation result record contains the complete rule-firing trace so that any
   decision can be fully explained by an analyst without additional system queries.

6. A transaction that triggers a BLOCK decision results in a `FraudDecision` event
   published within 100ms of the original transaction event being received.
