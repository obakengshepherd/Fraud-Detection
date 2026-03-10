# API Specification — Real-Time Fraud Detection System

---

## Overview

The Fraud Detection API provides operational and management access to the fraud evaluation
pipeline. The primary evaluation path is event-driven (Kafka) and is not exposed via REST —
transactions are consumed automatically from the `transactions` topic. This REST API is the
management interface: it allows analysts to retrieve evaluation results, manage fraud rules,
and resolve alerts. Consumed by analyst dashboards, internal tooling, and upstream payment
systems querying decision history.

---

## Base URL and Versioning

```
https://api.fraud.internal/api/v1
```

---

## Authentication

```
Authorization: Bearer <jwt_token>
```

Role-based access:
- `service` — may call `POST /transactions/evaluate` (internal service-to-service)
- `analyst` — may read evaluations, manage alerts, read rules
- `admin` — full access including rule creation and deactivation

---

## Common Response Envelope

### Success
```json
{
  "data": { ... },
  "meta": { "request_id": "uuid", "timestamp": "2024-01-15T10:30:00Z" }
}
```

### Error
```json
{
  "error": {
    "code": "RULE_NOT_FOUND",
    "message": "No fraud rule found with the given ID.",
    "details": []
  },
  "meta": { "request_id": "uuid", "timestamp": "2024-01-15T10:30:00Z" }
}
```

---

## Rate Limiting

| Endpoint                       | Limit              | Scope        |
|-------------------------------|--------------------|--------------|
| `POST /transactions/evaluate`  | 500 / minute       | Per service  |
| `GET /alerts`                  | 60 / minute        | Per user     |
| All other endpoints            | 60 / minute        | Per user     |

---

## Endpoints

---

### POST /transactions/evaluate

**Description:** Synchronous fraud evaluation endpoint for internal service use. Returns
a fraud decision for the provided transaction data. This is a supplementary path to the
Kafka consumer — use when a synchronous decision is required (e.g. blocking a payment
before authorisation).

**Request Headers:** `X-Idempotency-Key: <uuid-v4>` *(required)*

**Request Body:**

| Field              | Type    | Required | Validation             | Example                          |
|--------------------|---------|----------|------------------------|----------------------------------|
| `transaction_id`   | string  | Yes      | UUID                   | `"txn_01j9z3k4m5n6p7q8"`        |
| `user_id`          | string  | Yes      | UUID                   | `"usr_abc123"`                   |
| `amount`           | decimal | Yes      | > 0                    | `"1500.00"`                      |
| `currency`         | string  | Yes      | ISO 4217               | `"ZAR"`                          |
| `merchant_id`      | string  | No       | UUID                   | `"mrc_xyz789"`                   |
| `location_lat`     | float   | No       | -90 to 90              | `-26.2041`                       |
| `location_lng`     | float   | No       | -180 to 180            | `28.0473`                        |
| `timestamp`        | string  | Yes      | ISO8601                | `"2024-01-15T10:30:00Z"`         |

**Response — 200 OK:**
```json
{
  "data": {
    "transaction_id": "txn_01j9z3k4m5n6p7q8",
    "risk_score": 72,
    "decision": "BLOCK",
    "rules_fired": [
      { "rule_id": "rule_vel_01", "name": "High velocity", "score_contribution": 40 },
      { "rule_id": "rule_geo_01", "name": "Impossible travel", "score_contribution": 32 }
    ],
    "evaluated_at": "2024-01-15T10:30:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                           |
|------|-------------------------------------|
| 200  | Evaluation completed                |
| 400  | Missing or invalid fields           |
| 401  | Unauthorized                        |
| 403  | Caller does not have `service` role |
| 409  | Duplicate idempotency key           |
| 500  | Rule engine error                   |

---

### GET /transactions/{id}/risk-score

**Description:** Returns the stored fraud evaluation result for a specific transaction ID.

**Path Parameters:** `id` — Transaction ID

**Response — 200 OK:**
```json
{
  "data": {
    "transaction_id": "txn_01j9z3k4m5n6p7q8",
    "risk_score": 72,
    "decision": "BLOCK",
    "rules_fired": [
      { "rule_id": "rule_vel_01", "name": "High velocity", "score_contribution": 40 }
    ],
    "model_version": "1.2.0",
    "evaluated_at": "2024-01-15T10:30:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                              |
|------|----------------------------------------|
| 200  | Success                                |
| 401  | Unauthorized                           |
| 404  | No evaluation found for this transaction |

---

### GET /alerts

**Description:** Returns a paginated list of fraud alerts. Defaults to open alerts
ordered by created_at descending.

**Query Parameters:**

| Parameter  | Type    | Default  | Description                          |
|------------|---------|----------|--------------------------------------|
| `status`   | string  | `open`   | `open`, `resolved`, or `all`         |
| `severity` | string  | —        | `low`, `medium`, `high`, `critical`  |
| `limit`    | integer | `20`     | Page size, max 100                   |
| `cursor`   | string  | —        | Pagination cursor                    |

**Response — 200 OK:**
```json
{
  "data": [
    {
      "id": "alrt_01j9z3k4m5n6p7q8",
      "transaction_id": "txn_01j9z3k4m5n6p7q8",
      "risk_score": 72,
      "decision": "BLOCK",
      "severity": "high",
      "status": "open",
      "created_at": "2024-01-15T10:30:00Z",
      "resolved_at": null
    }
  ],
  "pagination": { ... },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition              |
|------|------------------------|
| 200  | Success                |
| 400  | Invalid query params   |
| 401  | Unauthorized           |

---

### POST /alerts/{id}/resolve

**Description:** Analyst resolves a fraud alert, recording the resolution action and notes.

**Path Parameters:** `id` — Alert ID

**Request Body:**

| Field    | Type   | Required | Validation           | Example               |
|----------|--------|----------|----------------------|-----------------------|
| `action` | string | Yes      | `false_positive`, `confirmed_fraud`, `under_review` | `"false_positive"` |
| `notes`  | string | No       | max 1024 chars       | `"Verified with user"` |

**Response — 200 OK:**
```json
{
  "data": {
    "id": "alrt_01j9z3k4m5n6p7q8",
    "status": "resolved",
    "action": "false_positive",
    "resolved_by": "usr_analyst_01",
    "resolved_at": "2024-01-15T11:00:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                           |
|------|-------------------------------------|
| 200  | Alert resolved                      |
| 401  | Unauthorized                        |
| 403  | Requires `analyst` or `admin` role  |
| 404  | Alert not found                     |
| 422  | Alert already resolved              |

---

### POST /rules

**Description:** Creates a new fraud detection rule. Takes effect within 60 seconds.

**Request Body:**

| Field         | Type    | Required | Validation                              | Example              |
|---------------|---------|----------|-----------------------------------------|----------------------|
| `name`        | string  | Yes      | max 128 chars                           | `"High velocity"`    |
| `type`        | string  | Yes      | `velocity`, `amount_anomaly`, `geo_velocity`, `new_location` | `"velocity"` |
| `parameters`  | object  | Yes      | Depends on type (see below)             | `{"threshold":5,"window_seconds":300}` |
| `score`       | integer | Yes      | 1–100                                   | `40`                 |
| `priority`    | integer | Yes      | 1–100 (lower number = higher priority)  | `1`                  |
| `description` | string  | No       | max 512 chars                           | `"Flags 5+ txns/5min"` |

**Parameters by rule type:**

```json
// velocity
{ "threshold": 5, "window_seconds": 300 }

// amount_anomaly
{ "multiplier": 3.0 }

// geo_velocity
{ "max_speed_kmh": 900 }

// new_location
{ "min_known_transactions": 3 }
```

**Response — 201 Created:**
```json
{
  "data": {
    "id": "rule_vel_02",
    "name": "High velocity",
    "type": "velocity",
    "is_active": true,
    "created_at": "2024-01-15T10:30:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                        |
|------|----------------------------------|
| 201  | Rule created                     |
| 400  | Invalid parameters for rule type |
| 401  | Unauthorized                     |
| 403  | Requires `admin` role            |

---

### GET /rules

**Description:** Returns all fraud rules with their current active status.

**Query Parameters:**

| Parameter   | Type    | Default | Description          |
|-------------|---------|---------|----------------------|
| `is_active` | boolean | —       | Filter by active flag |

**Response — 200 OK:**
```json
{
  "data": [
    {
      "id": "rule_vel_01",
      "name": "High velocity",
      "type": "velocity",
      "parameters": { "threshold": 5, "window_seconds": 300 },
      "score": 40,
      "priority": 1,
      "is_active": true,
      "created_at": "2024-01-15T10:30:00Z"
    }
  ],
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition     |
|------|---------------|
| 200  | Success       |
| 401  | Unauthorized  |
