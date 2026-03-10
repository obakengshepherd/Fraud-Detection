namespace FraudDetection.Domain.Entities;

public class TransactionEvaluation
{
    public string Id { get; private set; } = string.Empty;
    public string TransactionId { get; private set; } = string.Empty;
    public int RiskScore { get; private set; }
    public FraudDecision Decision { get; private set; }
    public string RulesFiredJson { get; private set; } = string.Empty; // serialised rule trace
    public string ModelVersion { get; private set; } = string.Empty;
    public DateTimeOffset EvaluatedAt { get; private set; }
}

public class FraudRule
{
    public string Id { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public FraudRuleType Type { get; private set; }
    public string ParametersJson { get; private set; } = string.Empty;
    public int Score { get; private set; }
    public int Priority { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

public class FraudAlert
{
    public string Id { get; private set; } = string.Empty;
    public string TransactionId { get; private set; } = string.Empty;
    public int RiskScore { get; private set; }
    public string Severity { get; private set; } = string.Empty;
    public AlertStatus Status { get; private set; }
    public string? ResolvedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
}

public enum FraudDecision { Allow, Review, Block }
public enum FraudRuleType { Velocity, AmountAnomaly, GeoVelocity, NewLocation }
public enum AlertStatus { Open, Resolved }

namespace FraudDetection.Api;
using System.Security.Claims;
public static class ClaimsPrincipalExtensions
{
    public static string GetUserId(this ClaimsPrincipal p)
    {
        var id = p.FindFirstValue(ClaimTypes.NameIdentifier) ?? p.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(id)) throw new UnauthorizedAccessException("User ID claim missing.");
        return id;
    }
}
