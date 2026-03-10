using FraudDetection.Application.Interfaces;
using FraudDetection.Api.Models.Requests;
using FraudDetection.Api.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FraudDetection.Api.Controllers;

[ApiController]
[Route("api/v1")]
[Authorize]
public class FraudController : ControllerBase
{
    private readonly IFraudEvaluationService _evaluationService;
    private readonly IAlertService _alertService;
    private readonly IRuleEngineService _ruleService;

    public FraudController(
        IFraudEvaluationService evaluationService,
        IAlertService alertService,
        IRuleEngineService ruleService)
    {
        _evaluationService = evaluationService;
        _alertService = alertService;
        _ruleService = ruleService;
    }

    // POST /api/v1/transactions/evaluate
    [HttpPost("transactions/evaluate")]
    public async Task<IActionResult> Evaluate(
        [FromBody] EvaluateTransactionRequest request,
        [FromHeader(Name = "X-Idempotency-Key")] string idempotencyKey,
        CancellationToken ct)
    {
        var result = await _evaluationService.EvaluateAsync(idempotencyKey, request, ct);
        return Ok(ApiResponse.Success(result));
    }

    // GET /api/v1/transactions/{id}/risk-score
    [HttpGet("transactions/{id}/risk-score")]
    public async Task<IActionResult> GetRiskScore([FromRoute] string id, CancellationToken ct)
    {
        var result = await _evaluationService.GetEvaluationAsync(id, ct);
        return Ok(ApiResponse.Success(result));
    }

    // GET /api/v1/alerts
    [HttpGet("alerts")]
    public async Task<IActionResult> GetAlerts([FromQuery] GetAlertsRequest query, CancellationToken ct)
    {
        var result = await _alertService.GetAlertsAsync(query, ct);
        return Ok(result);
    }

    // POST /api/v1/alerts/{id}/resolve
    [HttpPost("alerts/{id}/resolve")]
    public async Task<IActionResult> ResolveAlert(
        [FromRoute] string id, [FromBody] ResolveAlertRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var result = await _alertService.ResolveAlertAsync(id, userId, request, ct);
        return Ok(ApiResponse.Success(result));
    }

    // POST /api/v1/rules
    [HttpPost("rules")]
    public async Task<IActionResult> CreateRule([FromBody] CreateRuleRequest request, CancellationToken ct)
    {
        var result = await _ruleService.CreateRuleAsync(request, ct);
        return StatusCode(StatusCodes.Status201Created, ApiResponse.Success(result));
    }

    // GET /api/v1/rules
    [HttpGet("rules")]
    public async Task<IActionResult> GetRules([FromQuery] bool? isActive, CancellationToken ct)
    {
        var result = await _ruleService.GetRulesAsync(isActive, ct);
        return Ok(ApiResponse.Success(result));
    }
}