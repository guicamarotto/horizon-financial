using System.Text.Json;
using BuildingBlocks.Messaging.Contracts;
using BuildingBlocks.Messaging.Headers;
using BuildingBlocks.Messaging.Kafka;
using BuildingBlocks.Messaging.Options;
using BuildingBlocks.Messaging.Rabbit;
using BuildingBlocks.Messaging.Resilience;
using BuildingBlocks.Observability.Telemetry;
using BuildingBlocks.Persistence.Stores;
using Confluent.Kafka;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using RiskEngine.Worker.Risk;

public sealed class RiskEngineConsumerWorker : BackgroundService
{
    private const string ConsumerName = "risk-engine";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<RiskEngineConsumerWorker> _logger;
    private readonly IKafkaPublisher _kafkaPublisher;
    private readonly IRabbitPublisher _rabbitPublisher;
    private readonly IProcessedMessageStore _processedMessageStore;
    private readonly NpgsqlDataSource _dataSource;
    private readonly RiskEvaluator _riskEvaluator;
    private readonly ResilienceOptions _resilienceOptions;
    private readonly KafkaOptions _kafkaOptions;

    public RiskEngineConsumerWorker(
        ILogger<RiskEngineConsumerWorker> logger,
        IKafkaPublisher kafkaPublisher,
        IRabbitPublisher rabbitPublisher,
        IProcessedMessageStore processedMessageStore,
        NpgsqlDataSource dataSource,
        RiskEvaluator riskEvaluator,
        IOptions<ResilienceOptions> resilienceOptions,
        IOptions<KafkaOptions> kafkaOptions)
    {
        _logger = logger;
        _kafkaPublisher = kafkaPublisher;
        _rabbitPublisher = rabbitPublisher;
        _processedMessageStore = processedMessageStore;
        _dataSource = dataSource;
        _riskEvaluator = riskEvaluator;
        _resilienceOptions = resilienceOptions.Value;
        _kafkaOptions = kafkaOptions.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = _kafkaOptions.BootstrapServers,
            GroupId = string.IsNullOrWhiteSpace(_kafkaOptions.GroupId) ? "risk-engine-group" : _kafkaOptions.GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<Ignore, string>(consumerConfig).Build();
        consumer.Subscribe("orders.created");

        _logger.LogInformation("Risk engine subscribed to orders.created");

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
                    async (_, token) =>
                    {
                        await HandleMessageAsync(consumeResult, headers, token);
                    },
                    _resilienceOptions,
                    _logger,
                    "risk-process-order-created",
                    stoppingToken);

                consumer.Commit(consumeResult);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error handling orders.created message");
                LabTelemetry.FailedCounter.Add(1, KeyValuePair.Create<string, object?>("service", "RiskEngine.Worker"));

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
        var createdEvent = JsonSerializer.Deserialize<OrderCreatedV1>(consumeResult.Message.Value, JsonOptions)
            ?? throw new InvalidOperationException("Invalid OrderCreatedV1 payload.");

        var normalizedHeaders = headers with { OrderId = headers.OrderId == Guid.Empty ? createdEvent.OrderId : headers.OrderId };

        var isFirstProcess = await _processedMessageStore.TryMarkProcessedAsync(ConsumerName, normalizedHeaders.MessageId, cancellationToken);
        if (!isFirstProcess)
        {
            _logger.LogInformation(
                "duplicate ignored consumer={ConsumerName} message_id={MessageId} order_id={OrderId}",
                ConsumerName,
                normalizedHeaders.MessageId,
                createdEvent.OrderId);

            return;
        }

        var riskResult = _riskEvaluator.Evaluate(createdEvent, DateTimeOffset.UtcNow);

        if (!riskResult.Approved)
        {
            var rejectedEvent = new LimitRejectedV1(
                createdEvent.OrderId,
                createdEvent.AccountId,
                riskResult.Reason ?? "Rejected by risk engine.",
                DateTimeOffset.UtcNow);

            var rejectedHeaders = MessageHeaders.Create(
                createdEvent.OrderId,
                nameof(LimitRejectedV1),
                "v1",
                normalizedHeaders.CorrelationId,
                causationId: normalizedHeaders.MessageId.ToString("D"));

            await _kafkaPublisher.PublishAsync("limits.rejected", JsonSerializer.Serialize(rejectedEvent, JsonOptions), rejectedHeaders, cancellationToken);
            await UpdateOrderStatusAsync(createdEvent.OrderId, "REJECTED", cancellationToken);
            LabTelemetry.ProcessedCounter.Add(1, KeyValuePair.Create<string, object?>("service", "RiskEngine.Worker"));
            return;
        }

        var reserveCommand = new ReserveLimitV1(
            createdEvent.OrderId,
            createdEvent.AccountId,
            createdEvent.Symbol,
            createdEvent.Side,
            createdEvent.Quantity,
            createdEvent.Price,
            createdEvent.Quantity * createdEvent.Price,
            DateTimeOffset.UtcNow);

        var commandHeaders = MessageHeaders.Create(
            createdEvent.OrderId,
            nameof(ReserveLimitV1),
            "v1",
            normalizedHeaders.CorrelationId,
            causationId: normalizedHeaders.MessageId.ToString("D"));

        await _rabbitPublisher.PublishAsync(
            exchange: "limits.commands",
            routingKey: "limit.reserve",
            payload: JsonSerializer.Serialize(reserveCommand, JsonOptions),
            headers: commandHeaders,
            cancellationToken: cancellationToken);

        await UpdateOrderStatusAsync(createdEvent.OrderId, "RISK_APPROVED", cancellationToken);

        LabTelemetry.ProcessedCounter.Add(1, KeyValuePair.Create<string, object?>("service", "RiskEngine.Worker"));
    }

    private async Task UpdateOrderStatusAsync(Guid orderId, string status, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE orders.orders
            SET status = @Status,
                updated_at = NOW()
            WHERE id = @OrderId;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, new { OrderId = orderId, Status = status }, cancellationToken: cancellationToken));
    }

    private async Task PublishToDlqAsync(ConsumeResult<Ignore, string> consumeResult, Exception exception, CancellationToken cancellationToken)
    {
        var incomingHeaders = MessageHeaderConverter.FromKafkaHeaders(consumeResult.Message.Headers);

        var dlqEvent = new DeadLetterEventV1(
            incomingHeaders.MessageId,
            "RiskEngine.Worker",
            "orders.created",
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

        await _kafkaPublisher.PublishAsync("orders.dlq", JsonSerializer.Serialize(dlqEvent, JsonOptions), dlqHeaders, cancellationToken);
        LabTelemetry.DlqCounter.Add(1, KeyValuePair.Create<string, object?>("service", "RiskEngine.Worker"));
    }
}
