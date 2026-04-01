using System.Text.Json;
using Dapper;
using Npgsql;
using FraudDetection.Application.Exceptions;
using Microsoft.Extensions.Configuration;

namespace FraudDetection.Infrastructure.Persistence;

public class FraudRepository
{
    private readonly string _connectionString;

    public FraudRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("PostgreSQL")
            ?? throw new InvalidOperationException("PostgreSQL connection string missing.");
    }

    public NpgsqlConnection CreateConnection() => new(_connectionString);

    public async Task<IEnumerable<FraudRuleRecord>> GetActiveRulesAsync()
    {
        using var conn = CreateConnection();
        const string sql = """
            SELECT id, name, type, rule_logic, score, priority, is_active, description
            FROM fraud_rules
            WHERE is_active = true
            ORDER BY priority ASC
            """;
        return await conn.QueryAsync<FraudRuleRecord>(sql);
    }

    public async Task<UserProfileRecord?> GetProfileAsync(string userId)
    {
        using var conn = CreateConnection();
        const string sql = """
            SELECT user_id, avg_transaction_amount, transaction_count,
                   common_locations, last_transaction_lat, last_transaction_lng,
                   last_transaction_at
            FROM user_behaviour_profiles WHERE user_id = @UserId
            """;
        return await conn.QuerySingleOrDefaultAsync<UserProfileRecord>(sql, new { UserId = userId });
    }

    public async Task UpsertProfileAsync(UserProfileRecord profile, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO user_behaviour_profiles
                (user_id, avg_transaction_amount, transaction_count, common_locations,
                 last_transaction_lat, last_transaction_lng, last_transaction_at, updated_at)
            VALUES
                (@UserId, @AvgTransactionAmount, @TransactionCount, @CommonLocations::jsonb,
                 @LastTransactionLat, @LastTransactionLng, @LastTransactionAt, NOW())
            ON CONFLICT (user_id) DO UPDATE SET
                avg_transaction_amount = EXCLUDED.avg_transaction_amount,
                transaction_count      = EXCLUDED.transaction_count,
                common_locations       = EXCLUDED.common_locations,
                last_transaction_lat   = EXCLUDED.last_transaction_lat,
                last_transaction_lng   = EXCLUDED.last_transaction_lng,
                last_transaction_at    = EXCLUDED.last_transaction_at,
                updated_at             = NOW()
            """;
        await conn.ExecuteAsync(sql, profile);
    }

    public async Task InsertEvaluationAsync(EvaluationRecord evaluation, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO transaction_evaluations
                (id, transaction_id, user_id, risk_score, decision, rules_fired, model_version, evaluated_at)
            VALUES
                (@Id, @TransactionId, @UserId, @RiskScore, @Decision::fraud_decision,
                 @RulesFired::jsonb, @ModelVersion, @EvaluatedAt)
            ON CONFLICT (transaction_id) DO NOTHING
            """;
        await conn.ExecuteAsync(sql, evaluation);
    }

    public async Task InsertAlertAsync(AlertRecord alert, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO fraud_alerts
                (id, transaction_id, risk_score, decision, severity, status, created_at)
            VALUES
                (@Id, @TransactionId, @RiskScore, @Decision::fraud_decision,
                 @Severity::alert_severity, 'open', @CreatedAt)
            """;
        await conn.ExecuteAsync(sql, alert);
    }

    public async Task<IEnumerable<AlertRecord>> GetAlertsAsync(string? status, string? severity, int limit, string? cursor)
    {
        using var conn = CreateConnection();
        var whereClause = new List<string>();
        if (status is not null and not "all") whereClause.Add($"status = '{status}'::alert_status");
        if (severity is not null) whereClause.Add($"severity = '{severity}'::alert_severity");
        if (cursor is not null) whereClause.Add("id < @Cursor");

        var where = whereClause.Count > 0 ? "WHERE " + string.Join(" AND ", whereClause) : string.Empty;
        var sql = $"""
            SELECT id, transaction_id, risk_score, decision, severity, status, created_at, resolved_at
            FROM fraud_alerts
            {where}
            ORDER BY created_at DESC
            LIMIT @Limit
            """;
        return await conn.QueryAsync<AlertRecord>(sql, new { Cursor = cursor, Limit = limit });
    }

    public async Task ResolveAlertAsync(string alertId, string analystId, string action, string? notes)
    {
        using var conn = CreateConnection();
        const string sql = """
            UPDATE fraud_alerts
            SET status = 'resolved'::alert_status,
                resolved_by = @AnalystId,
                resolution  = @Action,
                notes       = @Notes,
                resolved_at = NOW()
            WHERE id = @AlertId AND status = 'open'
            """;
        var rows = await conn.ExecuteAsync(sql, new { AlertId = alertId, AnalystId = analystId, Action = action, Notes = notes });
        if (rows == 0) throw new AlertAlreadyResolvedException(alertId);
    }

    public async Task<EvaluationRecord?> GetEvaluationAsync(string transactionId)
    {
        using var conn = CreateConnection();
        const string sql = """
            SELECT id, transaction_id, user_id, risk_score, decision,
                   rules_fired, model_version, evaluated_at
            FROM transaction_evaluations WHERE transaction_id = @TransactionId
            """;
        return await conn.QuerySingleOrDefaultAsync<EvaluationRecord>(sql, new { TransactionId = transactionId });
    }
}

public record FraudRuleRecord
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string RuleLogic { get; init; } = "{}";
    public int Score { get; init; }
    public int Priority { get; init; }
    public bool IsActive { get; init; }
    public string? Description { get; init; }
}

public record UserProfileRecord
{
    public string UserId { get; init; } = string.Empty;
    public decimal AvgTransactionAmount { get; init; }
    public int TransactionCount { get; init; }
    public string CommonLocations { get; init; } = "[]";
    public double? LastTransactionLat { get; init; }
    public double? LastTransactionLng { get; init; }
    public DateTimeOffset? LastTransactionAt { get; init; }
}

public record EvaluationRecord
{
    public string Id { get; init; } = string.Empty;
    public string TransactionId { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public int RiskScore { get; init; }
    public string Decision { get; init; } = string.Empty;
    public string RulesFired { get; init; } = "[]";
    public string ModelVersion { get; init; } = "1.0.0";
    public DateTimeOffset EvaluatedAt { get; init; }
}

public record AlertRecord
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
