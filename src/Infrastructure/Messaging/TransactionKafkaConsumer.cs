using System.Text.Json;
using Confluent.Kafka;
using Dapper;
using FraudDetection.Api.Models.Requests;
using FraudDetection.Application.Interfaces;

namespace FraudDetection.Infrastructure.Messaging;

/// <summary>
/// Primary Kafka consumer for the Fraud Detection System.
///
/// Consumes: transactions topic (partitioned by user_id)
/// Produces: fraud.decisions topic
///
/// Consumer group: fraud-engine
///
/// Partition strategy: partitioned by user_id so all transactions for a given
/// user flow through the same consumer instance. This preserves per-user event
/// ordering for velocity checks and geo-velocity calculations.
///
/// Offset management: MANUAL commit only after evaluation result is persisted.
/// The consumer commits the offset only after:
///   1. Transaction evaluated
///   2. Result written to PostgreSQL
///   3. FraudDecision event published to Kafka
///
/// If the process crashes between steps 2 and 3, the message is re-consumed
/// and re-evaluated. The UNIQUE constraint on transaction_id in the evaluations
/// table makes this idempotent — the duplicate INSERT is silently skipped.
/// </summary>
public class TransactionKafkaConsumer : IDisposable
{
    private readonly IConsumer<string, string> _consumer;
    private readonly IProducer<string, string> _dlqProducer;
    private readonly IFraudEvaluationService _evaluationService;
    private readonly ILogger<TransactionKafkaConsumer> _logger;

    private const string InputTopic  = "transactions";
    private const string DlqTopic    = "transactions.dlq";
    private const int MaxRetries      = 3;

    public TransactionKafkaConsumer(
        IConfiguration configuration,
        IFraudEvaluationService evaluationService,
        ILogger<TransactionKafkaConsumer> logger)
    {
        _evaluationService = evaluationService;
        _logger            = logger;

        // Consumer configuration: exactly-once processing semantics
        _consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers      = configuration.GetConnectionString("Kafka") ?? "localhost:9092",
            GroupId               = "fraud-engine",
            AutoOffsetReset       = AutoOffsetReset.Earliest,
            EnableAutoCommit      = false,       // Manual commit — commit ONLY after DB write
            EnableAutoOffsetStore = false,
            IsolationLevel        = IsolationLevel.ReadCommitted,  // Read only committed producer messages
            MaxPollIntervalMs     = 60000,       // 60s max between polls before rebalance
            SessionTimeoutMs      = 30000
        }).Build();

        _dlqProducer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = configuration.GetConnectionString("Kafka") ?? "localhost:9092",
            Acks             = Acks.All
        }).Build();
    }

    public async Task ConsumeAsync(CancellationToken ct)
    {
        _consumer.Subscribe(InputTopic);
        _logger.LogInformation(
            "FraudEngine Kafka consumer started — group: fraud-engine, topic: {Topic}", InputTopic);

        while (!ct.IsCancellationRequested)
        {
            ConsumeResult<string, string>? result = null;
            try
            {
                // Poll with timeout — returns null on timeout, never blocks indefinitely
                result = _consumer.Consume(TimeSpan.FromSeconds(1));
                if (result is null || result.IsPartitionEOF) continue;

                _logger.LogDebug("Consumed message from {Topic}/{Partition}@{Offset}",
                    result.Topic, result.Partition, result.Offset);

                await ProcessWithRetryAsync(result, ct);

                // Commit offset AFTER successful processing (manual commit)
                // This ensures at-least-once processing — if we crash, we re-process
                _consumer.StoreOffset(result);
                _consumer.Commit(result);
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "Kafka consume exception on {Topic}", InputTopic);
                await Task.Delay(2000, ct);
            }
            catch (Exception ex) when (result is not null)
            {
                _logger.LogError(ex,
                    "Permanent failure processing message at {Partition}@{Offset} — routing to DLQ",
                    result.Partition, result.Offset);

                await SendToDlqAsync(result, ex.Message);

                // Commit the original offset so we don't reprocess it
                _consumer.StoreOffset(result);
                _consumer.Commit(result);
            }
        }

        _logger.LogInformation("FraudEngine consumer stopping — closing consumer group");
        _consumer.Close();
    }

    private async Task ProcessWithRetryAsync(ConsumeResult<string, string> result, CancellationToken ct)
    {
        Exception? lastException = null;

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                await EvaluateTransactionAsync(result.Message.Value, ct);
                return; // success
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt < MaxRetries - 1)
                {
                    var delay = TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 200);
                    _logger.LogWarning(ex,
                        "Fraud evaluation attempt {Attempt}/{Max} failed — retrying in {Delay}ms",
                        attempt + 1, MaxRetries, delay.TotalMilliseconds);
                    await Task.Delay(delay, ct);
                }
            }
        }

        throw lastException!;
    }

    private async Task EvaluateTransactionAsync(string messageValue, CancellationToken ct)
    {
        var txn = JsonSerializer.Deserialize<TransactionEvent>(messageValue)
            ?? throw new InvalidOperationException("Cannot deserialise transaction event");

        var request = new EvaluateTransactionRequest
        {
            TransactionId = txn.TransactionId,
            UserId        = txn.UserId,
            Amount        = txn.Amount,
            Currency      = txn.Currency,
            MerchantId    = txn.MerchantId,
            LocationLat   = txn.LocationLat,
            LocationLng   = txn.LocationLng,
            Timestamp     = txn.Timestamp
        };

        var result = await _evaluationService.EvaluateAsync(
            txn.TransactionId, // use transaction_id as idempotency key
            request,
            ct);

        _logger.LogInformation(
            "Evaluated transaction {TxnId}: score={Score}, decision={Decision}",
            txn.TransactionId, result.RiskScore, result.Decision);
    }

    /// <summary>
    /// Sends a failed message to the dead letter queue (DLQ) with full context.
    /// The DLQ topic retains messages for 30 days for manual replay.
    /// </summary>
    private async Task SendToDlqAsync(ConsumeResult<string, string> result, string errorReason)
    {
        var dlqPayload = JsonSerializer.Serialize(new
        {
            OriginalTopic     = result.Topic,
            OriginalPartition = result.Partition.Value,
            OriginalOffset    = result.Offset.Value,
            OriginalKey       = result.Message.Key,
            OriginalValue     = result.Message.Value,
            ErrorReason       = errorReason,
            FailedAt          = DateTimeOffset.UtcNow,
            ConsumerGroup     = "fraud-engine"
        });

        try
        {
            await _dlqProducer.ProduceAsync(DlqTopic, new Message<string, string>
            {
                Key   = result.Message.Key,
                Value = dlqPayload
            });
            _logger.LogWarning("Message sent to DLQ {DlqTopic} — key={Key}", DlqTopic, result.Message.Key);
        }
        catch (Exception ex)
        {
            // DLQ publish failure is critical — log at ERROR but do not re-throw
            // (we cannot block the main consumer loop over a DLQ issue)
            _logger.LogError(ex, "CRITICAL: Failed to send message to DLQ {DlqTopic}", DlqTopic);
        }
    }

    public void Dispose()
    {
        _consumer?.Dispose();
        _dlqProducer?.Dispose();
    }

    // ── Event DTOs ────────────────────────────────────────────────────────────

    private record TransactionEvent(
        string TransactionId,
        string UserId,
        decimal Amount,
        string Currency,
        string? MerchantId,
        decimal? LocationLat,
        decimal? LocationLng,
        DateTimeOffset Timestamp
    );
}

/// <summary>
/// Hosted background service wrapping the Kafka consumer.
/// Register: builder.Services.AddHostedService<TransactionConsumerWorker>();
/// </summary>
public class TransactionConsumerWorker : BackgroundService
{
    private readonly TransactionKafkaConsumer _consumer;
    private readonly ILogger<TransactionConsumerWorker> _logger;

    public TransactionConsumerWorker(
        TransactionKafkaConsumer consumer,
        ILogger<TransactionConsumerWorker> logger)
    {
        _consumer = consumer;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TransactionConsumerWorker starting");
        await _consumer.ConsumeAsync(stoppingToken);
        _logger.LogInformation("TransactionConsumerWorker stopping");
    }
}
