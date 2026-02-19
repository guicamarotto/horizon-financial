using System.Text;
using System.Text.Json;
using BuildingBlocks.Messaging.Contracts;
using BuildingBlocks.Messaging.Extensions;
using BuildingBlocks.Messaging.Headers;
using BuildingBlocks.Messaging.Kafka;
using BuildingBlocks.Messaging.Options;
using BuildingBlocks.Messaging.Rabbit;
using BuildingBlocks.Observability.Extensions;
using BuildingBlocks.Observability.Telemetry;
using BuildingBlocks.Persistence.Extensions;
using BuildingBlocks.Persistence.Flow;
using Confluent.Kafka;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.AddLabDefaults("Order.Api");
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddPostgresPersistence(builder.Configuration);
builder.Services.AddKafkaMessaging(builder.Configuration);
builder.Services.AddRabbitMessaging(builder.Configuration);

var app = builder.Build();
app.MapLabDefaultEndpoints();

app.UseSwagger();
app.UseSwaggerUI();
app.UseStaticFiles();

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNameCaseInsensitive = true
};

app.MapGet("/lab", () =>
{
    var root = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
    var filePath = Path.Combine(root, "lab", "index.html");

    return File.Exists(filePath)
        ? Results.File(filePath, "text/html")
        : Results.NotFound(new { error = "Lab UI not found." });
});

app.MapPost("/orders", async (
    CreateOrderRequest request,
    HttpContext httpContext,
    NpgsqlDataSource dataSource,
    IFlowEventStore flowEventStore,
    CancellationToken cancellationToken) =>
{
    try
    {
        var correlationId = httpContext.Request.Headers["x-correlation-id"].FirstOrDefault();
        var result = await CreateOrderAsync(request, correlationId, dataSource, flowEventStore, jsonOptions, cancellationToken);

        return Results.Accepted($"/orders/{result.OrderId}", new
        {
            orderId = result.OrderId,
            status = "PENDING",
            correlationId = result.CorrelationId
        });
    }
    catch (BadHttpRequestException badRequestException)
    {
        return Results.BadRequest(new { error = badRequestException.Message });
    }
});

app.MapPost("/lab/api/smoke/run", async (
    RunSmokeRequest? request,
    NpgsqlDataSource dataSource,
    IFlowEventStore flowEventStore,
    CancellationToken cancellationToken) =>
{
    var finalRequest = new CreateOrderRequest(
        AccountId: request?.AccountId ?? Guid.NewGuid(),
        Symbol: string.IsNullOrWhiteSpace(request?.Symbol) ? "PETR4" : request.Symbol!,
        Side: string.IsNullOrWhiteSpace(request?.Side) ? "BUY" : request.Side!,
        Quantity: request?.Quantity ?? 100,
        Price: request?.Price ?? 32.15m);

    var result = await CreateOrderAsync(finalRequest, correlationId: null, dataSource, flowEventStore, jsonOptions, cancellationToken);

    return Results.Ok(new
    {
        orderId = result.OrderId,
        correlationId = result.CorrelationId,
        status = "PENDING"
    });
});

app.MapGet("/orders/{id:guid}", async (Guid id, NpgsqlDataSource dataSource, CancellationToken cancellationToken) =>
{
    const string sql = """
        SELECT id AS OrderId,
               account_id AS AccountId,
               symbol AS Symbol,
               side AS Side,
               quantity AS Quantity,
               price AS Price,
               status AS Status,
               updated_at AS UpdatedAt
        FROM orders.orders
        WHERE id = @Id;
        """;

    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
    var order = await connection.QuerySingleOrDefaultAsync<OrderResponse>(
        new CommandDefinition(sql, new { Id = id }, cancellationToken: cancellationToken));

    return order is null ? Results.NotFound() : Results.Ok(order);
});

app.MapGet("/lab/api/orders/recent", async (
    int? limit,
    NpgsqlDataSource dataSource,
    CancellationToken cancellationToken) =>
{
    const string sql = """
        SELECT id AS OrderId,
               account_id AS AccountId,
               symbol AS Symbol,
               side AS Side,
               quantity AS Quantity,
               price AS Price,
               status AS Status,
               created_at AS CreatedAt,
               updated_at AS UpdatedAt
        FROM orders.orders
        ORDER BY created_at DESC
        LIMIT @Limit;
        """;

    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
    var orders = await connection.QueryAsync<RecentOrderResponse>(new CommandDefinition(
        sql,
        new { Limit = Math.Clamp(limit ?? 20, 1, 200) },
        cancellationToken: cancellationToken));

    return Results.Ok(orders);
});

app.MapGet("/lab/api/orders/{orderId:guid}/timeline", async (
    Guid orderId,
    int? limit,
    IFlowEventStore flowEventStore,
    CancellationToken cancellationToken) =>
{
    var timeline = await flowEventStore.GetTimelineAsync(orderId, Math.Clamp(limit ?? 500, 1, 2000), cancellationToken);
    return Results.Ok(timeline);
});

app.MapGet("/lab/api/dlq/overview", async (
    IServiceProvider serviceProvider,
    IFlowEventStore flowEventStore,
    IOptions<KafkaOptions> kafkaOptions,
    CancellationToken cancellationToken) =>
{
    var rabbitPendingCount = 0L;
    var rabbitInFlightCount = 0L;

    try
    {
        var connectionProvider = serviceProvider.GetRequiredService<IRabbitConnectionProvider>();
        using var channel = connectionProvider.GetConnection().CreateModel();
        rabbitInFlightCount = channel.QueueDeclarePassive("limits.reserve").MessageCount;
        rabbitPendingCount = channel.QueueDeclarePassive("limits.reserve.dlq").MessageCount;
    }
    catch
    {
        rabbitInFlightCount = 0;
        rabbitPendingCount = 0;
    }

    var kafkaInFlightCount =
        await EstimateKafkaLagByGroupAsync(kafkaOptions.Value.BootstrapServers, "risk-engine-group", "orders.created") +
        await EstimateKafkaLagByGroupAsync(kafkaOptions.Value.BootstrapServers, "notification-group", "limits.reserved") +
        await EstimateKafkaLagByGroupAsync(kafkaOptions.Value.BootstrapServers, "notification-group", "limits.rejected");

    var kafkaDlqBacklogEstimate = await EstimateKafkaBacklogAsync(kafkaOptions.Value.BootstrapServers, "orders.dlq");

    var latestFailures = await flowEventStore.GetLatestByStagesAsync(
        ["DLQ_RABBIT_SENT", "DLQ_KAFKA_SENT"],
        limit: 25,
        cancellationToken);

    return Results.Ok(new
    {
        rabbitInFlightCount,
        kafkaInFlightCount,
        rabbitPendingCount,
        kafkaDlqBacklogEstimate,
        latestFailures
    });
});

app.MapPost("/ops/replay/rabbit-dlq", (
    int? count,
    IServiceProvider serviceProvider,
    CancellationToken cancellationToken) =>
{
    var result = ReplayRabbitDlqAsync(count, serviceProvider, cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/ops/replay/kafka-dlq", async (
    int? count,
    IKafkaPublisher kafkaPublisher,
    IOptions<KafkaOptions> kafkaOptions,
    CancellationToken cancellationToken) =>
{
    var result = await ReplayKafkaDlqAsync(count, kafkaPublisher, kafkaOptions, jsonOptions, cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/lab/api/dlq/replay/rabbit", (
    int? count,
    IServiceProvider serviceProvider,
    CancellationToken cancellationToken) =>
{
    var result = ReplayRabbitDlqAsync(count, serviceProvider, cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/lab/api/dlq/replay/kafka", async (
    int? count,
    IKafkaPublisher kafkaPublisher,
    IOptions<KafkaOptions> kafkaOptions,
    CancellationToken cancellationToken) =>
{
    var result = await ReplayKafkaDlqAsync(count, kafkaPublisher, kafkaOptions, jsonOptions, cancellationToken);
    return Results.Ok(result);
});

app.Run();

static async Task<(Guid OrderId, string CorrelationId)> CreateOrderAsync(
    CreateOrderRequest request,
    string? correlationId,
    NpgsqlDataSource dataSource,
    IFlowEventStore flowEventStore,
    JsonSerializerOptions jsonOptions,
    CancellationToken cancellationToken)
{
    if (request.Quantity <= 0 || request.Price <= 0)
    {
        throw new BadHttpRequestException("quantity and price must be greater than zero.", StatusCodes.Status400BadRequest);
    }

    var normalizedSide = request.Side.Trim().ToUpperInvariant();
    if (normalizedSide is not ("BUY" or "SELL"))
    {
        throw new BadHttpRequestException("side must be BUY or SELL.", StatusCodes.Status400BadRequest);
    }

    var orderId = Guid.NewGuid();
    var resolvedCorrelationId = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("D") : correlationId;

    var createdEvent = new OrderCreatedV1(
        orderId,
        request.AccountId,
        request.Symbol.Trim().ToUpperInvariant(),
        normalizedSide,
        request.Quantity,
        request.Price,
        DateTimeOffset.UtcNow,
        "PENDING");

    var headers = MessageHeaders.Create(
        orderId,
        eventType: nameof(OrderCreatedV1),
        eventVersion: "v1",
        correlationId: resolvedCorrelationId);

    const string insertOrderSql = """
        INSERT INTO orders.orders (id, account_id, symbol, side, quantity, price, status, created_at, updated_at)
        VALUES (@Id, @AccountId, @Symbol, @Side, @Quantity, @Price, @Status, NOW(), NOW());
        """;

    const string insertOutboxSql = """
        INSERT INTO integration.outbox_messages (id, aggregate_id, type, payload, headers, status, created_at, retries)
        VALUES (@Id, @AggregateId, @Type, @Payload::jsonb, @Headers::jsonb, @Status, NOW(), 0);
        """;

    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

    await connection.ExecuteAsync(new CommandDefinition(
        insertOrderSql,
        new
        {
            Id = createdEvent.OrderId,
            createdEvent.AccountId,
            createdEvent.Symbol,
            createdEvent.Side,
            createdEvent.Quantity,
            createdEvent.Price,
            Status = "PENDING"
        },
        transaction,
        cancellationToken: cancellationToken));

    await connection.ExecuteAsync(new CommandDefinition(
        insertOutboxSql,
        new
        {
            Id = headers.MessageId,
            AggregateId = createdEvent.OrderId,
            Type = nameof(OrderCreatedV1),
            Payload = JsonSerializer.Serialize(createdEvent, jsonOptions),
            Headers = JsonSerializer.Serialize(headers.ToDictionary(), jsonOptions),
            Status = "Pending"
        },
        transaction,
        cancellationToken: cancellationToken));

    await transaction.CommitAsync(cancellationToken);

    await flowEventStore.AppendAsync(new FlowEventAppendRequest(
        orderId,
        resolvedCorrelationId,
        "Order.Api",
        "ORDER_RECEIVED",
        Broker: "NONE",
        Channel: "POST /orders",
        MessageId: headers.MessageId,
        PayloadSnippet: JsonSerializer.Serialize(request, jsonOptions)), cancellationToken);

    await flowEventStore.AppendAsync(new FlowEventAppendRequest(
        orderId,
        resolvedCorrelationId,
        "Order.Api",
        "OUTBOX_STORED",
        Broker: "NONE",
        Channel: "integration.outbox_messages",
        MessageId: headers.MessageId,
        PayloadSnippet: JsonSerializer.Serialize(createdEvent, jsonOptions)), cancellationToken);

    LabTelemetry.ProcessedCounter.Add(1, KeyValuePair.Create<string, object?>("service", "Order.Api"));

    return (orderId, resolvedCorrelationId);
}

static object ReplayRabbitDlqAsync(int? count, IServiceProvider serviceProvider, CancellationToken cancellationToken)
{
    const string commandExchange = "limits.commands";
    const string commandRoutingKey = "limit.reserve";
    const string dlqQueue = "limits.reserve.dlq";

    var maxMessages = Math.Clamp(count ?? 50, 1, 500);
    var replayed = 0;

    var connectionProvider = serviceProvider.GetRequiredService<IRabbitConnectionProvider>();
    using var channel = connectionProvider.GetConnection().CreateModel();

    while (replayed < maxMessages && !cancellationToken.IsCancellationRequested)
    {
        var result = channel.BasicGet(dlqQueue, autoAck: false);
        if (result is null)
        {
            break;
        }

        var properties = channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.Headers = result.BasicProperties.Headers ?? new Dictionary<string, object>();
        properties.MessageId = result.BasicProperties.MessageId;
        properties.CorrelationId = result.BasicProperties.CorrelationId;
        properties.Headers[MessageHeaderNames.RetryCount] = Encoding.UTF8.GetBytes("0");

        channel.BasicPublish(commandExchange, commandRoutingKey, mandatory: false, basicProperties: properties, body: result.Body);
        channel.BasicAck(result.DeliveryTag, multiple: false);
        replayed++;
    }

    return new { replayed, source = dlqQueue, target = $"{commandExchange}:{commandRoutingKey}" };
}

static async Task<object> ReplayKafkaDlqAsync(
    int? count,
    IKafkaPublisher kafkaPublisher,
    IOptions<KafkaOptions> kafkaOptions,
    JsonSerializerOptions jsonOptions,
    CancellationToken cancellationToken)
{
    var maxMessages = Math.Clamp(count ?? 50, 1, 500);
    var replayed = 0;

    var consumerConfig = new ConsumerConfig
    {
        BootstrapServers = kafkaOptions.Value.BootstrapServers,
        GroupId = $"order-api-dlq-replay-{Guid.NewGuid():N}",
        AutoOffsetReset = AutoOffsetReset.Earliest,
        EnableAutoCommit = false
    };

    using var consumer = new ConsumerBuilder<Ignore, string>(consumerConfig).Build();
    consumer.Subscribe("orders.dlq");

    while (replayed < maxMessages && !cancellationToken.IsCancellationRequested)
    {
        var consumeResult = consumer.Consume(TimeSpan.FromSeconds(1));
        if (consumeResult is null)
        {
            break;
        }

        try
        {
            var deadLetter = JsonSerializer.Deserialize<DeadLetterEventV1>(consumeResult.Message.Value, jsonOptions);
            if (deadLetter is null)
            {
                consumer.Commit(consumeResult);
                continue;
            }

            var headers = MessageHeaders.FromDictionary(deadLetter.OriginalHeaders);
            await kafkaPublisher.PublishAsync(deadLetter.TargetTopic, deadLetter.OriginalPayload, headers, cancellationToken);
            consumer.Commit(consumeResult);
            replayed++;
        }
        catch
        {
            consumer.Commit(consumeResult);
        }
    }

    return new { replayed, source = "orders.dlq" };
}

static Task<long> EstimateKafkaBacklogAsync(string bootstrapServers, string topic)
{
    try
    {
        var adminConfig = new AdminClientConfig
        {
            BootstrapServers = bootstrapServers
        };

        using var adminClient = new AdminClientBuilder(adminConfig).Build();
        var metadata = adminClient.GetMetadata(topic, TimeSpan.FromSeconds(2));
        var topicMetadata = metadata.Topics.FirstOrDefault(x => string.Equals(x.Topic, topic, StringComparison.Ordinal));

        if (topicMetadata is null || topicMetadata.Error.IsError || topicMetadata.Partitions.Count == 0)
        {
            return Task.FromResult(0L);
        }

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = $"order-api-lab-inspector-{Guid.NewGuid():N}",
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest
        };

        using var consumer = new ConsumerBuilder<Ignore, string>(consumerConfig).Build();

        long total = 0;

        foreach (var partition in topicMetadata.Partitions)
        {
            var offsets = consumer.QueryWatermarkOffsets(new TopicPartition(topic, new Partition(partition.PartitionId)), TimeSpan.FromSeconds(2));
            total += Math.Max(0L, offsets.High.Value - offsets.Low.Value);
        }

        return Task.FromResult(total);
    }
    catch
    {
        return Task.FromResult(0L);
    }
}

static Task<long> EstimateKafkaLagByGroupAsync(string bootstrapServers, string groupId, string topic)
{
    try
    {
        var adminConfig = new AdminClientConfig
        {
            BootstrapServers = bootstrapServers
        };

        using var adminClient = new AdminClientBuilder(adminConfig).Build();
        var metadata = adminClient.GetMetadata(topic, TimeSpan.FromSeconds(2));
        var topicMetadata = metadata.Topics.FirstOrDefault(x => string.Equals(x.Topic, topic, StringComparison.Ordinal));

        if (topicMetadata is null || topicMetadata.Error.IsError || topicMetadata.Partitions.Count == 0)
        {
            return Task.FromResult(0L);
        }

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = groupId,
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest
        };

        using var consumer = new ConsumerBuilder<Ignore, string>(consumerConfig).Build();
        var partitions = topicMetadata.Partitions
            .Select(partition => new TopicPartition(topic, new Partition(partition.PartitionId)))
            .ToArray();

        var committed = consumer.Committed(partitions, TimeSpan.FromSeconds(2))
            .ToDictionary(static x => x.TopicPartition, static x => x.Offset.Value);

        long totalLag = 0;

        foreach (var topicPartition in partitions)
        {
            var watermark = consumer.QueryWatermarkOffsets(topicPartition, TimeSpan.FromSeconds(2));
            var committedOffset = committed.TryGetValue(topicPartition, out var value) ? value : -1;
            var normalizedCommitted = Math.Max(0L, committedOffset);
            totalLag += Math.Max(0L, watermark.High.Value - normalizedCommitted);
        }

        return Task.FromResult(totalLag);
    }
    catch
    {
        return Task.FromResult(0L);
    }
}

public sealed record CreateOrderRequest(Guid AccountId, string Symbol, string Side, decimal Quantity, decimal Price);

public sealed record RunSmokeRequest(Guid? AccountId, string? Symbol, string? Side, decimal? Quantity, decimal? Price);

public sealed record OrderResponse(
    Guid OrderId,
    Guid AccountId,
    string Symbol,
    string Side,
    decimal Quantity,
    decimal Price,
    string Status,
    DateTime UpdatedAt);

public sealed class RecentOrderResponse
{
    public Guid OrderId { get; init; }

    public Guid AccountId { get; init; }

    public string Symbol { get; init; } = string.Empty;

    public string Side { get; init; } = string.Empty;

    public decimal Quantity { get; init; }

    public decimal Price { get; init; }

    public string Status { get; init; } = string.Empty;

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }
}

public partial class Program;
