namespace FraudDetection.Api.Models.Requests;

using System.ComponentModel.DataAnnotations;

public record EvaluateTransactionRequest
{
    [Required] public string TransactionId { get; init; } = string.Empty;
    [Required] public string UserId { get; init; } = string.Empty;
    [Required][Range(typeof(decimal),"0.01","9999999.99")] public decimal Amount { get; init; }
    [Required][StringLength(3, MinimumLength = 3)] public string Currency { get; init; } = string.Empty;
    public string? MerchantId { get; init; }
    [Range(-90, 90)] public double? LocationLat { get; init; }
    [Range(-180, 180)] public double? LocationLng { get; init; }
    [Required] public DateTimeOffset Timestamp { get; init; }
}

public record GetAlertsRequest
{
    public string? Status { get; init; } = "open"; // open | resolved | all
    public string? Severity { get; init; }
    [Range(1, 100)] public int Limit { get; init; } = 20;
    public string? Cursor { get; init; }
}

public record ResolveAlertRequest
{
    [Required] public string Action { get; init; } = string.Empty; // false_positive | confirmed_fraud | under_review
    [StringLength(1024)] public string? Notes { get; init; }
}

public record CreateRuleRequest
{
    [Required][StringLength(128)] public string Name { get; init; } = string.Empty;
    [Required] public string Type { get; init; } = string.Empty; // velocity | amount_anomaly | geo_velocity | new_location
    [Required] public Dictionary<string, object> Parameters { get; init; } = new();
    [Required][Range(1, 100)] public int Score { get; init; }
    [Required][Range(1, 100)] public int Priority { get; init; }
    [StringLength(512)] public string? Description { get; init; }
}