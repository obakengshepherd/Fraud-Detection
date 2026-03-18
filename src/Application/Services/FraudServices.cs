using System.Text.Json;
using Confluent.Kafka;
using Dapper;
using Npgsql;
using FraudDetection.Api.Models.Requests;
using FraudDetection.Api.Models.Responses;
using FraudDetection.Application.Interfaces;
using StackExchange.Redis;

namespace FraudDetection.Infrastructure.Persistence;

// ════════════════════════════════════════════════════════════════════════════
// FRAUD REPOSITORY
// ════════════════════════════════════════════════════════════════════════════

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

namespace FraudDetection.Infrastructure.Cache;

public class FraudCacheService
{
    private readonly IDatabase _db;
    private readonly ILogger<FraudCacheService> _logger;
    private static readonly TimeSpan ProfileTtl = TimeSpan.FromMinutes(30);

    public FraudCacheService(IConnectionMultiplexer redis, ILogger<FraudCacheService> logger)
    {
        _db     = redis.GetDatabase();
        _logger = logger;
    }

    public async Task<UserProfileRecord?> GetProfileAsync(string userId)
    {
        try
        {
            var val = await _db.StringGetAsync($"profile:{userId}");
            return val.HasValue ? JsonSerializer.Deserialize<UserProfileRecord>(val.ToString()) : null;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Redis profile read failed"); return null; }
    }

    public async Task SetProfileAsync(UserProfileRecord profile)
    {
        try
        {
            await _db.StringSetAsync($"profile:{profile.UserId}",
                JsonSerializer.Serialize(profile), ProfileTtl);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Redis profile write failed"); }
    }

    /// <summary>
    /// Redis sliding window velocity counter using INCR + EXPIRE.
    /// Returns the count of events in the window.
    /// </summary>
    public async Task<long> IncrementVelocityAsync(string userId, string windowKey, int windowSeconds)
    {
        var key = $"velocity:{userId}:{windowKey}";
        try
        {
            var count = await _db.StringIncrementAsync(key);
            if (count == 1) // first event in this window — set expiry
                await _db.KeyExpireAsync(key, TimeSpan.FromSeconds(windowSeconds));
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis velocity increment failed — treating as 0");
            return 0;
        }
    }
}

namespace FraudDetection.Application.Services;

// ════════════════════════════════════════════════════════════════════════════
// RULE ENGINE SERVICE
// ════════════════════════════════════════════════════════════════════════════

public class RuleEngineService : IRuleEngineService
{
    private readonly FraudRepository _repo;
    private readonly ILogger<RuleEngineService> _logger;
    private IReadOnlyList<FraudRuleRecord> _cachedRules = [];
    private DateTimeOffset _rulesLastRefreshed = DateTimeOffset.MinValue;
    private static readonly TimeSpan RuleCacheDuration = TimeSpan.FromSeconds(60);

    public RuleEngineService(FraudRepository repo, ILogger<RuleEngineService> logger)
    {
        _repo   = repo;
        _logger = logger;
    }

    public async Task<IEnumerable<RuleResponse>> GetActiveRulesAsync(CancellationToken ct)
    {
        var rules = await GetOrRefreshRulesAsync();
        return rules.Select(MapRule);
    }

    public async Task<IEnumerable<RuleResponse>> GetRulesAsync(bool? isActive, CancellationToken ct)
    {
        using var conn = _repo.CreateConnection();
        await conn.OpenAsync(ct);
        var sql = isActive.HasValue
            ? $"SELECT * FROM fraud_rules WHERE is_active = {isActive.Value}"
            : "SELECT * FROM fraud_rules ORDER BY priority ASC";
        var rules = await conn.QueryAsync<FraudRuleRecord>(sql);
        return rules.Select(MapRule);
    }

    public async Task<RuleResponse> CreateRuleAsync(CreateRuleRequest request, CancellationToken ct)
    {
        using var conn = _repo.CreateConnection();
        await conn.OpenAsync(ct);
        var id = $"rule_{Guid.NewGuid():N}";
        await conn.ExecuteAsync("""
            INSERT INTO fraud_rules (id, name, type, rule_logic, score, priority, is_active, description, created_at, created_by)
            VALUES (@Id, @Name, @Type::rule_type, @RuleLogic::jsonb, @Score, @Priority, true, @Description, NOW(), 'system')
            """,
            new
            {
                Id = id, request.Name, Type = request.Type,
                RuleLogic    = JsonSerializer.Serialize(request.Parameters),
                request.Score, request.Priority, request.Description
            });

        // Invalidate in-memory cache so new rule is picked up within 60s
        _rulesLastRefreshed = DateTimeOffset.MinValue;

        var created = (await conn.QueryAsync<FraudRuleRecord>("SELECT * FROM fraud_rules WHERE id = @Id", new { Id = id })).First();
        return MapRule(created);
    }

    internal async Task<IReadOnlyList<FraudRuleRecord>> GetOrRefreshRulesAsync()
    {
        if (DateTimeOffset.UtcNow - _rulesLastRefreshed < RuleCacheDuration)
            return _cachedRules;

        _cachedRules        = (await _repo.GetActiveRulesAsync()).ToList().AsReadOnly();
        _rulesLastRefreshed = DateTimeOffset.UtcNow;
        _logger.LogDebug("Refreshed {Count} fraud rules from database", _cachedRules.Count);
        return _cachedRules;
    }

    private static RuleResponse MapRule(FraudRuleRecord r) => new()
    {
        Id         = r.Id,
        Name       = r.Name,
        Type       = r.Type,
        Parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(r.RuleLogic) ?? new(),
        Score      = r.Score,
        Priority   = r.Priority,
        IsActive   = r.IsActive,
        CreatedAt  = DateTimeOffset.UtcNow // not stored on record in this mapping
    };
}

// ════════════════════════════════════════════════════════════════════════════
// FRAUD EVALUATION SERVICE
// ════════════════════════════════════════════════════════════════════════════

public class FraudEvaluationService : IFraudEvaluationService
{
    private readonly FraudRepository _repo;
    private readonly FraudCacheService _cache;
    private readonly RuleEngineService _ruleEngine;
    private readonly IProducer<string, string> _kafkaProducer;
    private readonly ILogger<FraudEvaluationService> _logger;
    private const string ModelVersion = "1.0.0";

    public FraudEvaluationService(
        FraudRepository repo,
        FraudCacheService cache,
        RuleEngineService ruleEngine,
        IConfiguration configuration,
        ILogger<FraudEvaluationService> logger)
    {
        _repo        = repo;
        _cache       = cache;
        _ruleEngine  = ruleEngine;
        _logger      = logger;
        _kafkaProducer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = configuration.GetConnectionString("Kafka") ?? "localhost:9092",
            Acks             = Acks.Leader
        }).Build();
    }

    public async Task<EvaluationResponse> EvaluateAsync(
        string idempotencyKey,
        EvaluateTransactionRequest request,
        CancellationToken ct)
    {
        // Check for existing evaluation (idempotent re-processing)
        var existing = await _repo.GetEvaluationAsync(request.TransactionId);
        if (existing is not null)
            return MapEvaluation(existing);

        // Load behaviour profile — Redis first, DB fallback
        var profile = await _cache.GetProfileAsync(request.UserId)
                   ?? await _repo.GetProfileAsync(request.UserId)
                   ?? new UserProfileRecord { UserId = request.UserId };

        var rules    = await _ruleEngine.GetOrRefreshRulesAsync();
        var firedRules = new List<FiredRule>();
        var totalScore = 0;

        // ── Execute each rule ─────────────────────────────────────────────────
        foreach (var rule in rules)
        {
            var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(rule.RuleLogic) ?? new();
            int? contribution = rule.Type switch
            {
                "velocity"       => await EvaluateVelocityAsync(request.UserId, rule.Id, parameters, rule.Score),
                "amount_anomaly" => EvaluateAmountAnomaly(request.Amount, profile, parameters, rule.Score),
                "geo_velocity"   => EvaluateGeoVelocity(request, profile, parameters, rule.Score),
                "new_location"   => EvaluateNewLocation(request, profile, parameters, rule.Score),
                _                => null
            };

            if (contribution.HasValue && contribution.Value > 0)
            {
                firedRules.Add(new FiredRule
                {
                    RuleId            = rule.Id,
                    Name              = rule.Name,
                    ScoreContribution = contribution.Value
                });
                totalScore += contribution.Value;
            }
        }

        totalScore = Math.Min(totalScore, 100);
        var decision = totalScore switch
        {
            <= 30 => "allow",
            <= 70 => "review",
            _     => "block"
        };

        var evaluationId = $"eval_{Guid.NewGuid():N}";
        var evaluation   = new EvaluationRecord
        {
            Id            = evaluationId,
            TransactionId = request.TransactionId,
            UserId        = request.UserId,
            RiskScore     = totalScore,
            Decision      = decision,
            RulesFired    = JsonSerializer.Serialize(firedRules),
            ModelVersion  = ModelVersion,
            EvaluatedAt   = DateTimeOffset.UtcNow
        };

        await using var conn = _repo.CreateConnection();
        await conn.OpenAsync(ct);
        await _repo.InsertEvaluationAsync(evaluation, conn);

        // Create alert for review/block decisions
        if (decision is "review" or "block")
        {
            var severity = totalScore switch { > 85 => "critical", > 70 => "high", > 50 => "medium", _ => "low" };
            await _repo.InsertAlertAsync(new AlertRecord
            {
                Id            = $"alrt_{Guid.NewGuid():N}",
                TransactionId = request.TransactionId,
                RiskScore     = totalScore,
                Decision      = decision,
                Severity      = severity,
                Status        = "open",
                CreatedAt     = DateTimeOffset.UtcNow
            }, conn);
        }

        // Update behaviour profile
        var updatedProfile = UpdateProfile(profile, request);
        await _repo.UpsertProfileAsync(updatedProfile, conn);
        await _cache.SetProfileAsync(updatedProfile);

        // Publish FraudDecision event
        _ = _kafkaProducer.ProduceAsync("fraud.decisions", new Message<string, string>
        {
            Key   = request.UserId,
            Value = JsonSerializer.Serialize(new
            {
                evaluation.TransactionId,
                Decision    = decision.ToUpper(),
                evaluation.RiskScore,
                evaluation.EvaluatedAt
            })
        });

        _logger.LogInformation(
            "Evaluated transaction {TxnId}: score={Score}, decision={Decision}",
            request.TransactionId, totalScore, decision);

        return MapEvaluation(evaluation);
    }

    public async Task<EvaluationResponse> GetEvaluationAsync(string transactionId, CancellationToken ct)
    {
        var evaluation = await _repo.GetEvaluationAsync(transactionId)
            ?? throw new EvaluationNotFoundException(transactionId);
        return MapEvaluation(evaluation);
    }

    // ── Rule evaluators ───────────────────────────────────────────────────────

    private async Task<int?> EvaluateVelocityAsync(
        string userId, string ruleId, Dictionary<string, JsonElement> parameters, int ruleScore)
    {
        var threshold     = parameters.GetValueOrDefault("threshold").GetInt32();
        var windowSeconds = parameters.GetValueOrDefault("window_seconds").GetInt32();
        var windowKey     = $"{ruleId}:{windowSeconds}";
        var count = await _cache.IncrementVelocityAsync(userId, windowKey, windowSeconds);
        return count > threshold ? ruleScore : null;
    }

    private static int? EvaluateAmountAnomaly(
        decimal amount, UserProfileRecord profile,
        Dictionary<string, JsonElement> parameters, int ruleScore)
    {
        if (profile.TransactionCount < 5) return null; // not enough history
        var multiplier = parameters.GetValueOrDefault("multiplier").GetDouble();
        return amount > (double)profile.AvgTransactionAmount * multiplier ? ruleScore : null;
    }

    private static int? EvaluateGeoVelocity(
        EvaluateTransactionRequest request, UserProfileRecord profile,
        Dictionary<string, JsonElement> parameters, int ruleScore)
    {
        if (!profile.LastTransactionLat.HasValue || !request.LocationLat.HasValue)
            return null;

        var maxSpeedKmh = parameters.GetValueOrDefault("max_speed_kmh").GetDouble();
        var distance    = HaversineKm(
            profile.LastTransactionLat.Value, profile.LastTransactionLng!.Value,
            (double)request.LocationLat.Value, (double)request.LocationLng!.Value);

        if (!profile.LastTransactionAt.HasValue) return null;
        var elapsed  = (request.Timestamp - profile.LastTransactionAt.Value).TotalHours;
        if (elapsed <= 0) return ruleScore; // simultaneous = impossible

        var impliedSpeed = distance / elapsed;
        return impliedSpeed > maxSpeedKmh ? ruleScore : null;
    }

    private static int? EvaluateNewLocation(
        EvaluateTransactionRequest request, UserProfileRecord profile,
        Dictionary<string, JsonElement> parameters, int ruleScore)
    {
        if (!request.LocationLat.HasValue) return null;
        var minTxns = parameters.GetValueOrDefault("min_known_transactions").GetInt32();
        if (profile.TransactionCount < minTxns) return null;

        var locations = JsonSerializer.Deserialize<List<KnownLocation>>(profile.CommonLocations) ?? [];
        const double knownRadiusKm = 0.5;
        var isKnown = locations.Any(l =>
            HaversineKm(l.Lat, l.Lng, (double)request.LocationLat.Value, (double)request.LocationLng!.Value) < knownRadiusKm);
        return isKnown ? null : ruleScore;
    }

    private static UserProfileRecord UpdateProfile(UserProfileRecord profile, EvaluateTransactionRequest request)
    {
        var newCount = profile.TransactionCount + 1;
        var newAvg   = ((profile.AvgTransactionAmount * profile.TransactionCount) + request.Amount) / newCount;
        return profile with
        {
            AvgTransactionAmount  = newAvg,
            TransactionCount      = newCount,
            LastTransactionLat    = (double?)request.LocationLat,
            LastTransactionLng    = (double?)request.LocationLng,
            LastTransactionAt     = request.Timestamp
        };
    }

    private static double HaversineKm(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLng = (lng2 - lng1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
              * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static EvaluationResponse MapEvaluation(EvaluationRecord e) => new()
    {
        TransactionId = e.TransactionId,
        RiskScore     = e.RiskScore,
        Decision      = e.Decision.ToUpper(),
        RulesFired    = JsonSerializer.Deserialize<List<FiredRule>>(e.RulesFired) ?? [],
        ModelVersion  = e.ModelVersion,
        EvaluatedAt   = e.EvaluatedAt
    };

    private record KnownLocation(double Lat, double Lng, int Count);
}

// ════════════════════════════════════════════════════════════════════════════
// ALERT SERVICE
// ════════════════════════════════════════════════════════════════════════════

public class AlertService : IAlertService
{
    private readonly FraudRepository _repo;

    public AlertService(FraudRepository repo) => _repo = repo;

    public async Task<PagedApiResponse<AlertResponse>> GetAlertsAsync(GetAlertsRequest query, CancellationToken ct)
    {
        var alerts = await _repo.GetAlertsAsync(query.Status, query.Severity, query.Limit, query.Cursor);
        return new PagedApiResponse<AlertResponse>
        {
            Data = alerts.Select(a => new AlertResponse
            {
                Id            = a.Id,
                TransactionId = a.TransactionId,
                RiskScore     = a.RiskScore,
                Decision      = a.Decision.ToUpper(),
                Severity      = a.Severity,
                Status        = a.Status,
                CreatedAt     = a.CreatedAt,
                ResolvedAt    = a.ResolvedAt
            }),
            Pagination = new PaginationMeta { Limit = query.Limit }
        };
    }

    public async Task<AlertResolvedResponse> ResolveAlertAsync(
        string alertId, string analystId, ResolveAlertRequest request, CancellationToken ct)
    {
        await _repo.ResolveAlertAsync(alertId, analystId, request.Action, request.Notes);
        return new AlertResolvedResponse
        {
            Id         = alertId,
            Status     = "resolved",
            Action     = request.Action,
            ResolvedBy = analystId,
            ResolvedAt = DateTimeOffset.UtcNow
        };
    }
}

// ── Fraud exceptions ──────────────────────────────────────────────────────────

public class EvaluationNotFoundException(string txnId)
    : Exception($"No evaluation found for transaction '{txnId}'.");

public class AlertAlreadyResolvedException(string alertId)
    : Exception($"Alert '{alertId}' is already resolved.");
