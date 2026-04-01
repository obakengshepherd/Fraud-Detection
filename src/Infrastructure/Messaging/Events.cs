namespace FraudDetection.Infrastructure.Messaging;

using FraudDetection.Api.Models.Responses;

/// <summary>
/// TransactionEvent schema for Kafka 'transactions' topic.
/// Produced by upstream payment systems (wallet, payment processor).
/// Consumed by fraud-engine consumer group.
/// </summary>
public record TransactionEvent
{
    public string TransactionId { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public string? MerchantId { get; init; }
    public double? LocationLat { get; init; }
    public double? LocationLng { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// FraudDecision schema for Kafka 'fraud.decisions' topic.
/// Produced by fraud-engine consumer group.
/// Consumed by downstream systems (alert dashboard, payment service).
/// </summary>
public record FraudDecisionEvent
{
    public string TransactionId { get; init; } = string.Empty;
    public string Decision { get; init; } = string.Empty; // ALLOW | REVIEW | BLOCK
    public int RiskScore { get; init; }
    public IEnumerable<object> RulesFired { get; init; } = [];
    public DateTimeOffset EvaluatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Dead Letter Queue (DLQ) event schema for 'transactions.dlq' topic.
/// Stores failed messages with full context for manual replay.
/// </summary>
public record DlqEvent
{
    public string OriginalTopic { get; init; } = string.Empty;
    public int OriginalPartition { get; init; }
    public long OriginalOffset { get; init; }
    public string OriginalKey { get; init; } = string.Empty;
    public string OriginalValue { get; init; } = string.Empty;
    public string ErrorReason { get; init; } = string.Empty;
    public DateTimeOffset FailedAt { get; init; } = DateTimeOffset.UtcNow;
    public string ConsumerGroup { get; init; } = "fraud-engine";
}
