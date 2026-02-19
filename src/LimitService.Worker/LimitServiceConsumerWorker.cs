using System.Text;
using System.Text.Json;
using BuildingBlocks.Messaging.Contracts;
using BuildingBlocks.Messaging.Headers;
using BuildingBlocks.Messaging.Kafka;
using BuildingBlocks.Messaging.Rabbit;
using BuildingBlocks.Messaging.Resilience;
using BuildingBlocks.Observability.Telemetry;
using BuildingBlocks.Persistence.Stores;
using Dapper;
using LimitService.Worker.Limits;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;

public sealed class LimitServiceConsumerWorker : BackgroundService
{
    private const string ConsumerName = "limit-service";
    private const string Exchange = "limits.commands";
    private const string Queue = "limits.reserve";
    private const string RoutingKey = "limit.reserve";
    private const string DlqExchange = "limits.commands.dlx";
    private const string DlqQueue = "limits.reserve.dlq";
    private const string DlqRoutingKey = "limit.reserve.dlq";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<LimitServiceConsumerWorker> _logger;
    private readonly IRabbitConnectionProvider _rabbitConnectionProvider;
    private readonly IKafkaPublisher _kafkaPublisher;
    private readonly IProcessedMessageStore _processedMessageStore;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ResilienceOptions _resilienceOptions;
    private readonly LimitRulesOptions _limitRules;

    public LimitServiceConsumerWorker(
        ILogger<LimitServiceConsumerWorker> logger,
        IRabbitConnectionProvider rabbitConnectionProvider,
        IKafkaPublisher kafkaPublisher,
        IProcessedMessageStore processedMessageStore,
        NpgsqlDataSource dataSource,
        IOptions<ResilienceOptions> resilienceOptions,
        IOptions<LimitRulesOptions> limitRules)
    {
        _logger = logger;
        _rabbitConnectionProvider = rabbitConnectionProvider;
        _kafkaPublisher = kafkaPublisher;
        _processedMessageStore = processedMessageStore;
        _dataSource = dataSource;
        _resilienceOptions = resilienceOptions.Value;
        _limitRules = limitRules.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = _rabbitConnectionProvider.GetConnection();
                using var channel = connection.CreateModel();
                ConfigureTopology(channel);

                _logger.LogInformation("Limit service listening on queue {Queue}", Queue);

                while (!stoppingToken.IsCancellationRequested && connection.IsOpen)
                {
                    var result = channel.BasicGet(Queue, autoAck: false);
                    if (result is null)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
                        continue;
                    }

                    try
                    {
                        var body = Encoding.UTF8.GetString(result.Body.ToArray());
                        var headers = MessageHeaderConverter.FromRabbitHeaders(result.BasicProperties);

                        await RetryPolicy.ExecuteAsync(
                            async (_, token) => await HandleMessageAsync(body, headers, token),
                            _resilienceOptions,
                            _logger,
                            "limit-service-process-command",
                            stoppingToken);

                        channel.BasicAck(result.DeliveryTag, multiple: false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(exception, "Error processing limit command. Sending to DLQ.");

                        var properties = channel.CreateBasicProperties();
                        properties.Persistent = true;
                        properties.MessageId = result.BasicProperties.MessageId;
                        properties.CorrelationId = result.BasicProperties.CorrelationId;
                        properties.Headers = result.BasicProperties.Headers ?? new Dictionary<string, object>();
                        properties.Headers["dlq_reason"] = Encoding.UTF8.GetBytes(exception.Message);

                        channel.BasicPublish(DlqExchange, DlqRoutingKey, mandatory: false, basicProperties: properties, body: result.Body);
                        channel.BasicAck(result.DeliveryTag, multiple: false);

                        LabTelemetry.DlqCounter.Add(1, KeyValuePair.Create<string, object?>("service", "LimitService.Worker"));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "RabbitMQ unavailable for limit service. Retrying in 5 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private static void ConfigureTopology(IModel channel)
    {
        channel.ExchangeDeclare(Exchange, ExchangeType.Direct, durable: true, autoDelete: false);
        channel.ExchangeDeclare(DlqExchange, ExchangeType.Direct, durable: true, autoDelete: false);

        channel.QueueDeclare(Queue, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object>
        {
            ["x-dead-letter-exchange"] = DlqExchange,
            ["x-dead-letter-routing-key"] = DlqRoutingKey
        });

        channel.QueueDeclare(DlqQueue, durable: true, exclusive: false, autoDelete: false);

        channel.QueueBind(Queue, Exchange, RoutingKey);
        channel.QueueBind(DlqQueue, DlqExchange, DlqRoutingKey);
    }

    private async Task HandleMessageAsync(string payload, MessageHeaders headers, CancellationToken cancellationToken)
    {
        var command = JsonSerializer.Deserialize<ReserveLimitV1>(payload, JsonOptions)
            ?? throw new InvalidOperationException("Invalid ReserveLimitV1 payload.");

        var normalizedHeaders = headers with { OrderId = headers.OrderId == Guid.Empty ? command.OrderId : headers.OrderId };

        var isFirstProcess = await _processedMessageStore.TryMarkProcessedAsync(ConsumerName, normalizedHeaders.MessageId, cancellationToken);
        if (!isFirstProcess)
        {
            _logger.LogInformation(
                "duplicate ignored consumer={ConsumerName} message_id={MessageId} order_id={OrderId}",
                ConsumerName,
                normalizedHeaders.MessageId,
                command.OrderId);

            return;
        }

        if (_limitRules.FailSymbols.Any(symbol => string.Equals(symbol, command.Symbol, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Forced failure for symbol {command.Symbol}.");
        }

        var reserveResult = await TryReserveLimitAsync(command.AccountId, command.Notional, cancellationToken);

        if (reserveResult.Approved)
        {
            var reservedEvent = new LimitReservedV1(
                command.OrderId,
                command.AccountId,
                reserveResult.ReservedAmount,
                DateTimeOffset.UtcNow);

            var reservedHeaders = MessageHeaders.Create(
                command.OrderId,
                nameof(LimitReservedV1),
                "v1",
                normalizedHeaders.CorrelationId,
                causationId: normalizedHeaders.MessageId.ToString("D"));

            await _kafkaPublisher.PublishAsync("limits.reserved", JsonSerializer.Serialize(reservedEvent, JsonOptions), reservedHeaders, cancellationToken);
            await UpdateOrderStatusAsync(command.OrderId, "LIMIT_RESERVED", cancellationToken);
            LabTelemetry.ProcessedCounter.Add(1, KeyValuePair.Create<string, object?>("service", "LimitService.Worker"));
            return;
        }

        var rejectedEvent = new LimitRejectedV1(
            command.OrderId,
            command.AccountId,
            reserveResult.Reason,
            DateTimeOffset.UtcNow);

        var rejectedHeaders = MessageHeaders.Create(
            command.OrderId,
            nameof(LimitRejectedV1),
            "v1",
            normalizedHeaders.CorrelationId,
            causationId: normalizedHeaders.MessageId.ToString("D"));

        await _kafkaPublisher.PublishAsync("limits.rejected", JsonSerializer.Serialize(rejectedEvent, JsonOptions), rejectedHeaders, cancellationToken);
        await UpdateOrderStatusAsync(command.OrderId, "REJECTED", cancellationToken);
        LabTelemetry.ProcessedCounter.Add(1, KeyValuePair.Create<string, object?>("service", "LimitService.Worker"));
    }

    private async Task<(bool Approved, decimal ReservedAmount, string Reason)> TryReserveLimitAsync(Guid accountId, decimal requestedNotional, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string currentSql = """
            SELECT reserved_amount
            FROM limits.account_limits
            WHERE account_id = @AccountId
            FOR UPDATE;
            """;

        var currentReserved = await connection.QuerySingleOrDefaultAsync<decimal?>(
            new CommandDefinition(currentSql, new { AccountId = accountId }, transaction, cancellationToken: cancellationToken)) ?? 0m;

        var newReserved = currentReserved + requestedNotional;

        if (newReserved > _limitRules.AccountMaxLimit)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, currentReserved, $"Account limit exceeded ({newReserved} > {_limitRules.AccountMaxLimit}).");
        }

        const string upsertSql = """
            INSERT INTO limits.account_limits (account_id, reserved_amount, updated_at)
            VALUES (@AccountId, @ReservedAmount, NOW())
            ON CONFLICT (account_id)
            DO UPDATE SET reserved_amount = @ReservedAmount, updated_at = NOW();
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            upsertSql,
            new { AccountId = accountId, ReservedAmount = newReserved },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return (true, newReserved, string.Empty);
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
}
