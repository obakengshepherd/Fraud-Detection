namespace FraudDetection.Api.Models.Responses;

public record EvaluationResponse
{
    public string TransactionId { get; init; } = string.Empty;
    public int RiskScore { get; init; }
    public string Decision { get; init; } = string.Empty; // ALLOW | REVIEW | BLOCK
    public IEnumerable<FiredRule> RulesFired { get; init; } = [];
    public string ModelVersion { get; init; } = string.Empty;
    public DateTimeOffset EvaluatedAt { get; init; }
}

public record FiredRule
{
    public string RuleId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int ScoreContribution { get; init; }
}

public record AlertResponse
{
    public string Id { get; init; } = string.Empty;
    public string TransactionId { get; init; } = string.Empty;
    public int RiskScore { get; init; }
    public string Decision { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
}

public record AlertResolvedResponse
{
    public string Id { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string ResolvedBy { get; init; } = string.Empty;
    public DateTimeOffset ResolvedAt { get; init; }
}

public record RuleResponse
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public Dictionary<string, object> Parameters { get; init; } = new();
    public int Score { get; init; }
    public int Priority { get; init; }
    public bool IsActive { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public record ApiResponse<T> { public T Data { get; init; } = default!; public ApiMeta Meta { get; init; } = new(); }
public record PagedApiResponse<T> { public IEnumerable<T> Data { get; init; } = []; public PaginationMeta Pagination { get; init; } = new(); public ApiMeta Meta { get; init; } = new(); }
public record ApiMeta { public string RequestId { get; init; } = Guid.NewGuid().ToString(); public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow; }
public record PaginationMeta { public string? Cursor { get; init; } public bool HasMore { get; init; } public int Limit { get; init; } }
public static class ApiResponse { public static ApiResponse<T> Success<T>(T data) => new() { Data = data }; }