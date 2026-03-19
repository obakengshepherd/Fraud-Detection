using FraudDetection.Infrastructure.Cache;
using FraudDetection.Infrastructure.Messaging;
using FraudDetection.Infrastructure.Persistence;
using StackExchange.Redis;

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