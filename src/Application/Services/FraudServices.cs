namespace FraudDetection.Application.Services;

using FraudDetection.Application.Interfaces;
using FraudDetection.Api.Models.Requests;
using FraudDetection.Api.Models.Responses;

public class FraudEvaluationService : IFraudEvaluationService
{
    public Task<EvaluationResponse> EvaluateAsync(string idempotencyKey, EvaluateTransactionRequest request, CancellationToken ct) => throw new NotImplementedException("Implemented Day 14");
    public Task<EvaluationResponse> GetEvaluationAsync(string transactionId, CancellationToken ct) => throw new NotImplementedException("Implemented Day 14");
}

public class AlertService : IAlertService
{
    public Task<PagedApiResponse<AlertResponse>> GetAlertsAsync(GetAlertsRequest query, CancellationToken ct) => throw new NotImplementedException("Implemented Day 14");
    public Task<AlertResolvedResponse> ResolveAlertAsync(string alertId, string analystId, ResolveAlertRequest request, CancellationToken ct) => throw new NotImplementedException("Implemented Day 14");
}

public class RuleEngineService : IRuleEngineService
{
    public Task<RuleResponse> CreateRuleAsync(CreateRuleRequest request, CancellationToken ct) => throw new NotImplementedException("Implemented Day 14");
    public Task<IEnumerable<RuleResponse>> GetRulesAsync(bool? isActive, CancellationToken ct) => throw new NotImplementedException("Implemented Day 14");
    public Task<IEnumerable<RuleResponse>> GetActiveRulesAsync(CancellationToken ct) => throw new NotImplementedException("Implemented Day 14");
}