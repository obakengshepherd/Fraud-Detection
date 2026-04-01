using FraudDetection.Infrastructure.Cache;
using FraudDetection.Infrastructure.Messaging;
using FraudDetection.Infrastructure.Persistence;
using FraudDetection.Application.Interfaces;
using FraudDetection.Application.Services;
using Shared.Infrastructure.RateLimit;
using Shared.Api.Controllers;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ════════════════════════════════════════════════════════════════════════════
// DEPENDENCY INJECTION — Layer-by-layer registration
// ════════════════════════════════════════════════════════════════════════════

// ── Rate limit policies (shared infrastructure) ────────────────────────────
builder.Services.AddSingleton<IEnumerable<RateLimitRule>>(_ => RateLimitPolicies.FraudPolicies());

// ── Redis ──────────────────────────────────────────────────────────────────
var redisConnection = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        redisConnection,
        options => options.ConnectTimeout = 5000));
builder.Services.AddSingleton<FraudCacheService>();

// ── PostgreSQL Persistence ────────────────────────────────────────────────
builder.Services.AddScoped<FraudRepository>();

// ── Application Services ──────────────────────────────────────────────────
// RuleEngineService: Singleton with in-memory rule cache (60s refresh + jitter)
builder.Services.AddSingleton<RuleEngineService>();
builder.Services.AddScoped<IFraudEvaluationService, FraudEvaluationService>();
builder.Services.AddScoped<IAlertService, AlertService>();
builder.Services.AddScoped<IRuleEngineService>(sp => sp.GetRequiredService<RuleEngineService>());

// ── Kafka Consumer (critical path) ────────────────────────────────────────
// The transaction consumer is a singleton — it maintains state across requests
builder.Services.AddSingleton<TransactionKafkaConsumer>();
builder.Services.AddHostedService<TransactionConsumerWorker>();

// ── Health Checks ────────────────────────────────────────────────────────
var pgConnectionString = builder.Configuration.GetConnectionString("PostgreSQL") 
    ?? "Host=localhost;Port=5432;Database=fraud_detection;Username=devuser;Password=devpass;";
var kafkaConnectionString = builder.Configuration.GetConnectionString("Kafka") ?? "localhost:9092";

builder.Services.AddHealthChecks()
    .AddCheck("postgresql", new PostgreSqlHealthCheck(pgConnectionString), 
        failureStatus: HealthStatus.Unhealthy, tags: ["database"])
    .AddAsyncCheck("redis", async (ct) =>
    {
        try
        {
            var redis = builder.Services.BuildServiceProvider().GetRequiredService<IConnectionMultiplexer>();
            var server = redis.GetServer(redis.GetEndPoints().FirstOrDefault());
            await server.PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded(description: $"Redis unavailable: {ex.Message}");
        }
    },
        failureStatus: HealthStatus.Degraded, tags: ["cache"])
    .AddCheck("kafka", new KafkaHealthCheck(kafkaConnectionString),
        failureStatus: HealthStatus.Unhealthy, tags: ["messaging"]);

// ── API Services ─────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddSwaggerGen();

// ════════════════════════════════════════════════════════════════════════════
// MIDDLEWARE PIPELINE
// ════════════════════════════════════════════════════════════════════════════

var app = builder.Build();

// Development-only features
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Fraud Detection API v1");
        options.RoutePrefix = "swagger";
    });
}

// HTTPS redirect in production
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Structured logging middleware
app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    var startTime = DateTimeOffset.UtcNow;

    await next.Invoke();

    var duration = DateTimeOffset.UtcNow - startTime;
    logger.LogInformation(
        "HTTP {Method} {Path} => {StatusCode} in {DurationMs}ms",
        context.Request.Method, context.Request.Path, context.Response.StatusCode, duration.TotalMilliseconds);
});

// Rate limiting middleware (Redis-backed, distributed)
app.UseMiddleware<RedisRateLimitMiddleware>();

// Standard ASP.NET Core middleware
app.UseRouting();

// Route handlers
app.MapControllers();
app.MapHealthEndpoints();

// ════════════════════════════════════════════════════════════════════════════
// STARTUP LOGGING
// ════════════════════════════════════════════════════════════════════════════

app.Logger.LogInformation("═══════════════════════════════════════════════════════════════");
app.Logger.LogInformation("Fraud Detection System starting");
app.Logger.LogInformation("Environment: {Environment}", app.Environment.EnvironmentName);
app.Logger.LogInformation("Primary traffic path: Kafka consumer group 'fraud-engine'");
app.Logger.LogInformation("Secondary path: HTTP REST API (rate limited)");
app.Logger.LogInformation("API: http://localhost:8083");
app.Logger.LogInformation("Health: http://localhost:8083/health");
app.Logger.LogInformation("═══════════════════════════════════════════════════════════════");

await app.RunAsync();