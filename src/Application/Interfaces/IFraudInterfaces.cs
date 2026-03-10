namespace FraudDetection.Application.Interfaces;

using FraudDetection.Api.Models.Requests;
using FraudDetection.Api.Models.Responses;

/// <summary>
/// Executes fraud evaluation on a transaction.
/// Primary path: Kafka consumer (event-driven).
/// Secondary path: synchronous REST via POST /transactions/evaluate.
/// </summary>
public interface IFraudEvaluationService
{
    Task<EvaluationResponse> EvaluateAsync(string idempotencyKey, EvaluateTransactionRequest request, CancellationToken ct);
    Task<EvaluationResponse> GetEvaluationAsync(string transactionId, CancellationToken ct);
}

/// <summary>
/// Manages fraud alert lifecycle: creation (from REVIEW/BLOCK decisions), retrieval, resolution.
/// </summary>
public interface IAlertService
{
    Task<PagedApiResponse<AlertResponse>> GetAlertsAsync(GetAlertsRequest query, CancellationToken ct);
    Task<AlertResolvedResponse> ResolveAlertAsync(string alertId, string analystId, ResolveAlertRequest request, CancellationToken ct);
}

/// <summary>
/// Manages fraud rule lifecycle.
/// Rules are cached in memory and refreshed every 60 seconds.
/// </summary>
public interface IRuleEngineService
{
    Task<RuleResponse> CreateRuleAsync(CreateRuleRequest request, CancellationToken ct);
    Task<IEnumerable<RuleResponse>> GetRulesAsync(bool? isActive, CancellationToken ct);
    Task<IEnumerable<RuleResponse>> GetActiveRulesAsync(CancellationToken ct); // used by evaluation engine
}