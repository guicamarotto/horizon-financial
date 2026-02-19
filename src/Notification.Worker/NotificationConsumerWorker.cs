using System.Text.Json;
using BuildingBlocks.Messaging.Contracts;
using BuildingBlocks.Messaging.Headers;
using BuildingBlocks.Messaging.Kafka;
using BuildingBlocks.Messaging.Options;
using BuildingBlocks.Messaging.Resilience;
using BuildingBlocks.Observability.Telemetry;
using BuildingBlocks.Persistence.Flow;
using BuildingBlocks.Persistence.Stores;
using Confluent.Kafka;
using Dapper;
using Microsoft.Extensions.Options;
using Notification.Worker.Notifications;
using Npgsql;

public sealed class NotificationConsumerWorker : BackgroundService
{
    private const string ConsumerName = "notification-service";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<NotificationConsumerWorker> _logger;
    private readonly IKafkaPublisher _kafkaPublisher;
    private readonly IProcessedMessageStore _processedMessageStore;
    private readonly IFlowEventStore _flowEventStore;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ResilienceOptions _resilienceOptions;
    private readonly KafkaOptions _kafkaOptions;
    private readonly NotificationOptions _notificationOptions;

    public NotificationConsumerWorker(
        ILogger<NotificationConsumerWorker> logger,
        IKafkaPublisher kafkaPublisher,
        IProcessedMessageStore processedMessageStore,
        IFlowEventStore flowEventStore,
        NpgsqlDataSource dataSource,
        IOptions<ResilienceOptions> resilienceOptions,
        IOptions<KafkaOptions> kafkaOptions,
        IOptions<NotificationOptions> notificationOptions)
    {
        _logger = logger;
        _kafkaPublisher = kafkaPublisher;
        _processedMessageStore = processedMessageStore;
        _flowEventStore = flowEventStore;
        _dataSource = dataSource;
        _resilienceOptions = resilienceOptions.Value;
        _kafkaOptions = kafkaOptions.Value;
        _notificationOptions = notificationOptions.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = _kafkaOptions.BootstrapServers,
            GroupId = string.IsNullOrWhiteSpace(_kafkaOptions.GroupId) ? "notification-group" : _kafkaOptions.GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<Ignore, string>(consumerConfig).Build();
        consumer.Subscribe(new[] { "limits.reserved", "limits.rejected" });

        _logger.LogInformation("Notification worker subscribed to limits.reserved and limits.rejected");

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<Ignore, string>? consumeResult = null;

            try
            {
                consumeResult = consumer.Consume(stoppingToken);
                if (consumeResult is null)
                {
                    continue;
                }

                var headers = MessageHeaderConverter.FromKafkaHeaders(consumeResult.Message.Headers);

                await RetryPolicy.ExecuteAsync(
                    async (_, token) => await HandleMessageAsync(consumeResult, headers, token),
                    _resilienceOptions,
                    _logger,
                    "notification-process-event",
                    stoppingToken);

                consumer.Commit(consumeResult);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Notification processing failed");
                LabTelemetry.FailedCounter.Add(1, KeyValuePair.Create<string, object?>("service", "Notification.Worker"));

                if (consumeResult is not null)
                {
                    await PublishToDlqAsync(consumeResult, exception, stoppingToken);
                    consumer.Commit(consumeResult);
                }
            }
        }
    }

    private async Task HandleMessageAsync(ConsumeResult<Ignore, string> consumeResult, MessageHeaders headers, CancellationToken cancellationToken)
    {
        var isFirstProcess = await _processedMessageStore.TryMarkProcessedAsync(ConsumerName, headers.MessageId, cancellationToken);
        if (!isFirstProcess)
        {
            _logger.LogInformation(
                "duplicate ignored consumer={ConsumerName} message_id={MessageId}",
                ConsumerName,
                headers.MessageId);
            return;
        }

        await _flowEventStore.AppendAsync(new FlowEventAppendRequest(
            OrderId: headers.OrderId == Guid.Empty ? null : headers.OrderId,
            headers.CorrelationId,
            "Notification.Worker",
            "NOTIFICATION_CONSUMED_KAFKA",
            Broker: "KAFKA",
            Channel: consumeResult.Topic,
            MessageId: headers.MessageId,
            PayloadSnippet: consumeResult.Message.Value), cancellationToken);

        if (_notificationOptions.FailOnRejectedEvents && string.Equals(consumeResult.Topic, "limits.rejected", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Forced notification failure for rejected events.");
        }

        var orderId = headers.OrderId;
        if (orderId == Guid.Empty)
        {
            orderId = consumeResult.Topic switch
            {
                "limits.reserved" => JsonSerializer.Deserialize<LimitReservedV1>(consumeResult.Message.Value, JsonOptions)?.OrderId ?? Guid.Empty,
                "limits.rejected" => JsonSerializer.Deserialize<LimitRejectedV1>(consumeResult.Message.Value, JsonOptions)?.OrderId ?? Guid.Empty,
                _ => Guid.Empty
            };
        }

        const string insertSql = """
            INSERT INTO notifications.notifications (id, order_id, correlation_id, channel, payload, created_at)
            VALUES (@Id, @OrderId, @CorrelationId, @Channel, @Payload::jsonb, NOW());
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            insertSql,
            new
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                CorrelationId = headers.CorrelationId,
                Channel = "SYSTEM",
                Payload = consumeResult.Message.Value
            },
            cancellationToken: cancellationToken));

        await _flowEventStore.AppendAsync(new FlowEventAppendRequest(
            OrderId: orderId == Guid.Empty ? null : orderId,
            headers.CorrelationId,
            "Notification.Worker",
            "NOTIFICATION_PERSISTED",
            Broker: "NONE",
            Channel: "notifications.notifications",
            MessageId: headers.MessageId), cancellationToken);

        LabTelemetry.ProcessedCounter.Add(1, KeyValuePair.Create<string, object?>("service", "Notification.Worker"));
    }

    private async Task PublishToDlqAsync(ConsumeResult<Ignore, string> consumeResult, Exception exception, CancellationToken cancellationToken)
    {
        var incomingHeaders = MessageHeaderConverter.FromKafkaHeaders(consumeResult.Message.Headers);

        var deadLetter = new DeadLetterEventV1(
            incomingHeaders.MessageId,
            "Notification.Worker",
            consumeResult.Topic,
            exception.Message,
            consumeResult.Message.Value,
            incomingHeaders.ToDictionary(),
            DateTimeOffset.UtcNow);

        var dlqHeaders = MessageHeaders.Create(
            incomingHeaders.OrderId,
            nameof(DeadLetterEventV1),
            "v1",
            incomingHeaders.CorrelationId,
            causationId: incomingHeaders.MessageId.ToString("D"));

        await _kafkaPublisher.PublishAsync("orders.dlq", JsonSerializer.Serialize(deadLetter, JsonOptions), dlqHeaders, cancellationToken);

        await _flowEventStore.AppendAsync(new FlowEventAppendRequest(
            OrderId: incomingHeaders.OrderId == Guid.Empty ? null : incomingHeaders.OrderId,
            incomingHeaders.CorrelationId,
            "Notification.Worker",
            "DLQ_KAFKA_SENT",
            Broker: "KAFKA",
            Channel: "orders.dlq",
            MessageId: incomingHeaders.MessageId,
            PayloadSnippet: exception.Message), cancellationToken);

        LabTelemetry.DlqCounter.Add(1, KeyValuePair.Create<string, object?>("service", "Notification.Worker"));
    }
}
