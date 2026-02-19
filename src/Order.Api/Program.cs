using System.Text;
using System.Text.Json;
using BuildingBlocks.Messaging.Contracts;
using BuildingBlocks.Messaging.Extensions;
using BuildingBlocks.Messaging.Headers;
using BuildingBlocks.Messaging.Kafka;
using BuildingBlocks.Messaging.Options;
using BuildingBlocks.Observability.Extensions;
using BuildingBlocks.Observability.Telemetry;
using BuildingBlocks.Persistence.Extensions;
using Confluent.Kafka;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

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

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNameCaseInsensitive = true
};

app.MapPost("/orders", async (
    CreateOrderRequest request,
    HttpContext httpContext,
    NpgsqlDataSource dataSource,
    CancellationToken cancellationToken) =>
{
    if (request.Quantity <= 0 || request.Price <= 0)
    {
        return Results.BadRequest(new { error = "quantity and price must be greater than zero." });
    }

    var normalizedSide = request.Side.Trim().ToUpperInvariant();
    if (normalizedSide is not ("BUY" or "SELL"))
    {
        return Results.BadRequest(new { error = "side must be BUY or SELL." });
    }

    var orderId = Guid.NewGuid();
    var correlationId = httpContext.Request.Headers["x-correlation-id"].FirstOrDefault() ?? Guid.NewGuid().ToString("D");

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
        correlationId: correlationId);

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

    LabTelemetry.ProcessedCounter.Add(1, KeyValuePair.Create<string, object?>("service", "Order.Api"));

    return Results.Accepted($"/orders/{orderId}", new
    {
        orderId,
        status = "PENDING",
        correlationId
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

app.MapPost("/ops/replay/rabbit-dlq", (
    int? count,
    IServiceProvider serviceProvider,
    CancellationToken cancellationToken) =>
{
    const string commandExchange = "limits.commands";
    const string commandRoutingKey = "limit.reserve";
    const string dlqQueue = "limits.reserve.dlq";

    var maxMessages = Math.Clamp(count ?? 50, 1, 500);
    var replayed = 0;

    var connectionProvider = serviceProvider.GetRequiredService<BuildingBlocks.Messaging.Rabbit.IRabbitConnectionProvider>();
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

    return Results.Ok(new { replayed, source = dlqQueue, target = $"{commandExchange}:{commandRoutingKey}" });
});

app.MapPost("/ops/replay/kafka-dlq", async (
    int? count,
    IKafkaPublisher kafkaPublisher,
    IOptions<KafkaOptions> kafkaOptions,
    CancellationToken cancellationToken) =>
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

        DeadLetterEventV1? deadLetter;

        try
        {
            deadLetter = JsonSerializer.Deserialize<DeadLetterEventV1>(consumeResult.Message.Value, jsonOptions);
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

    return Results.Ok(new { replayed, source = "orders.dlq" });
});

app.Run();

public sealed record CreateOrderRequest(Guid AccountId, string Symbol, string Side, decimal Quantity, decimal Price);

public sealed record OrderResponse(
    Guid OrderId,
    Guid AccountId,
    string Symbol,
    string Side,
    decimal Quantity,
    decimal Price,
    string Status,
    DateTime UpdatedAt);

public partial class Program;
