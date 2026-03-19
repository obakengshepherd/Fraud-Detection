using FraudDetection.Infrastructure.Cache;
using FraudDetection.Infrastructure.Messaging;
using FraudDetection.Infrastructure.Persistence;
using StackExchange.Redis;
using Shared.Infrastructure.RateLimit;
using Shared.Api.Controllers;
using Microsoft.Extensions.Diagnostics.HealthChecks;

builder.Services.AddSingleton<IEnumerable<RateLimitRule>>(
    _ => RateLimitPolicies.FraudPolicies());

builder.Services.AddHealthChecks()
    .AddCheck<RedisHealthCheck>("redis",       failureStatus: HealthStatus.Degraded,  tags: ["cache"])
    .AddCheck<KafkaHealthCheck>("kafka",       failureStatus: HealthStatus.Unhealthy, tags: ["messaging"])  // Kafka is critical for fraud
    .AddCheck<PostgreSqlHealthCheck>("postgresql", failureStatus: HealthStatus.Unhealthy, tags: ["database"]);

builder.Services.AddTransient<RedisHealthCheck>();
builder.Services.AddTransient(_ => new PostgreSqlHealthCheck(builder.Configuration.GetConnectionString("PostgreSQL")!));
builder.Services.AddTransient(_ => new KafkaHealthCheck(builder.Configuration.GetConnectionString("Kafka") ?? "localhost:9092"));

// ── Middleware pipeline ──
// app.UseAuthentication();
// app.UseAuthorization();
// app.UseMiddleware<RedisRateLimitMiddleware>();
// app.MapControllers();
// app.MapHealthEndpoints();

// Redis
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379"));
builder.Services.AddSingleton<FraudCacheService>();

// Repositories
builder.Services.AddScoped<FraudRepository>();

// Application services
builder.Services.AddSingleton<RuleEngineService>();           // singleton — in-memory rule cache
builder.Services.AddScoped<IFraudEvaluationService, FraudEvaluationService>();
builder.Services.AddScoped<IAlertService, AlertService>();
builder.Services.AddScoped<IRuleEngineService>(sp => sp.GetRequiredService<RuleEngineService>());

// Kafka consumer (primary evaluation pipeline — the core of this system)
builder.Services.AddSingleton<TransactionKafkaConsumer>();
builder.Services.AddHostedService<TransactionConsumerWorker>();

// Health checks
builder.Services.AddHealthChecks()
    .AddNpgsql(builder.Configuration.GetConnectionString("PostgreSQL")!)
    .AddRedis(builder.Configuration.GetConnectionString("Redis")!);