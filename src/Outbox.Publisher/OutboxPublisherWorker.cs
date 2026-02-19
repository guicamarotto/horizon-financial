using System.Text.Json;
using BuildingBlocks.Messaging.Contracts;
using BuildingBlocks.Messaging.Headers;
using BuildingBlocks.Messaging.Kafka;
using BuildingBlocks.Messaging.Resilience;
using BuildingBlocks.Observability.Telemetry;
using BuildingBlocks.Persistence.Flow;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

public sealed class OutboxPublisherWorker : BackgroundService
{
    private const string WorkerName = "outbox-publisher";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<OutboxPublisherWorker> _logger;
    private readonly IKafkaPublisher _kafkaPublisher;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ResilienceOptions _resilienceOptions;
    private readonly IFlowEventStore _flowEventStore;

    public OutboxPublisherWorker(
        ILogger<OutboxPublisherWorker> logger,
        IKafkaPublisher kafkaPublisher,
        NpgsqlDataSource dataSource,
        IFlowEventStore flowEventStore,
        IOptions<ResilienceOptions> resilienceOptions)
    {
        _logger = logger;
        _kafkaPublisher = kafkaPublisher;
        _dataSource = dataSource;
        _flowEventStore = flowEventStore;
        _resilienceOptions = resilienceOptions.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{WorkerName} started", WorkerName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pendingMessages = await FetchPendingOutboxMessagesAsync(stoppingToken);

                if (pendingMessages.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                foreach (var message in pendingMessages)
                {
                    await ProcessMessageAsync(message, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Unexpected error in {WorkerName}", WorkerName);
                LabTelemetry.FailedCounter.Add(1, KeyValuePair.Create<string, object?>("service", "Outbox.Publisher"));
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task<IReadOnlyList<OutboxMessageRow>> FetchPendingOutboxMessagesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id AS Id,
                   aggregate_id AS AggregateId,
                   type AS Type,
                   payload::text AS Payload,
                   headers::text AS Headers,
                   retries AS Retries
            FROM integration.outbox_messages
            WHERE status = 'Pending'
            ORDER BY created_at
            LIMIT 100;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var messages = await connection.QueryAsync<OutboxMessageRow>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return messages.ToList();
    }

    private async Task ProcessMessageAsync(OutboxMessageRow message, CancellationToken cancellationToken)
    {
        try
        {
            var topic = ResolveTopicName(message.Type);
            var headersDictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(message.Headers, JsonOptions) ?? [];
            var headers = MessageHeaders.FromDictionary(headersDictionary);

            await RetryPolicy.ExecuteAsync(
                async (_, token) =>
                {
                    await _kafkaPublisher.PublishAsync(topic, message.Payload, headers, token);
                },
                _resilienceOptions,
                _logger,
                $"publish-outbox-{message.Id}",
                cancellationToken);

            await MarkAsPublishedAsync(message.Id, cancellationToken);

            await _flowEventStore.AppendAsync(new FlowEventAppendRequest(
                headers.OrderId == Guid.Empty ? message.AggregateId : headers.OrderId,
                headers.CorrelationId,
                "Outbox.Publisher",
                "OUTBOX_PUBLISHED_KAFKA",
                Broker: "KAFKA",
                Channel: topic,
                MessageId: headers.MessageId,
                PayloadSnippet: message.Payload), cancellationToken);

            LabTelemetry.ProcessedCounter.Add(1, KeyValuePair.Create<string, object?>("service", "Outbox.Publisher"));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error publishing outbox message {MessageId}", message.Id);
            LabTelemetry.FailedCounter.Add(1, KeyValuePair.Create<string, object?>("service", "Outbox.Publisher"));
            await MarkAsFailedAttemptAsync(message.Id, cancellationToken);
        }
    }

    private async Task MarkAsPublishedAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE integration.outbox_messages
            SET status = 'Published',
                published_at = NOW(),
                retries = retries + 1
            WHERE id = @Id;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, new { Id = id }, cancellationToken: cancellationToken));
    }

    private async Task MarkAsFailedAttemptAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE integration.outbox_messages
            SET retries = retries + 1,
                status = CASE WHEN retries + 1 >= 5 THEN 'Failed' ELSE 'Pending' END
            WHERE id = @Id;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, new { Id = id }, cancellationToken: cancellationToken));
    }

    private static string ResolveTopicName(string messageType)
    {
        return messageType switch
        {
            nameof(OrderCreatedV1) => "orders.created",
            nameof(LimitReservedV1) => "limits.reserved",
            nameof(LimitRejectedV1) => "limits.rejected",
            _ => throw new InvalidOperationException($"Unsupported outbox type '{messageType}'.")
        };
    }

    private sealed record OutboxMessageRow(Guid Id, Guid AggregateId, string Type, string Payload, string Headers, int Retries);
}
