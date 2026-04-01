using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FraudDetection.Infrastructure.Messaging;

/// <summary>
/// Hosted service that runs the Kafka transaction consumer in the background.
///
/// The consumer group 'fraud-engine' consumes from the 'transactions' topic,
/// evaluates each transaction against fraud rules, and publishes decisions to
/// the 'fraud.decisions' topic.
///
/// Lifecycle:
/// - StartAsync: Starts the consumer loop in a background task
/// - StopAsync: Gracefully shuts down the consumer
/// - Graceful shutdown: Waits for consumer to close and offset to commit
///
/// This service is critical for the application — if it stops, no transactions
/// will be evaluated. Use BackgroundServiceIsStopped (in appsettings) to detect
/// this condition and trigger alerts.
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
        _logger = logger;
    }

    /// <summary>
    /// Starts the consumer loop. This runs until StopAsync is called or a fatal exception occurs.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "TransactionConsumerWorker starting — fraud-engine consumer group will begin consuming from 'transactions' topic");

        try
        {
            await _consumer.ConsumeAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("TransactionConsumerWorker cancelled gracefully");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "TransactionConsumerWorker encountered an unrecoverable error — fraud evaluation pipeline is stopped");
            throw;
        }
    }

    /// <summary>
    /// Gracefully shuts down the consumer.
    /// Waits up to 30 seconds for the consumer to close and commit the current offset.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("TransactionConsumerWorker stopping — closing consumer gracefully");

        try
        {
            _consumer.Dispose();
            _logger.LogInformation("TransactionConsumerWorker stopped successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during TransactionConsumerWorker shutdown");
        }

        await base.StopAsync(cancellationToken);
    }
}
