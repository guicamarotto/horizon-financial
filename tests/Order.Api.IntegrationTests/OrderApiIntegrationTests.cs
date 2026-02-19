using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Order.Api.IntegrationTests;

public sealed class OrderApiIntegrationTests : IClassFixture<OrderApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly OrderApiFactory _factory;

    public OrderApiIntegrationTests(OrderApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostOrder_ShouldPersistOrderAndOutbox_WhenPostgresConfigured()
    {
        if (!_factory.IsEnabled)
        {
            return;
        }

        var client = _factory.CreateClient();
        var request = new
        {
            accountId = Guid.NewGuid(),
            symbol = "PETR4",
            side = "BUY",
            quantity = 100,
            price = 10.5m
        };

        var response = await client.PostAsJsonAsync("/orders", request);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Accepted);

        var payload = await response.Content.ReadFromJsonAsync<CreateOrderResult>(JsonOptions);
        payload.Should().NotBeNull();
        var orderId = payload!.OrderId;

        await AssertOrderAndOutboxAsync(orderId);
    }

    [Fact]
    public async Task LabSmokeRun_ShouldCreateOrderAndOutbox_WhenPostgresConfigured()
    {
        if (!_factory.IsEnabled)
        {
            return;
        }

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/lab/api/smoke/run", new
        {
            accountId = Guid.NewGuid(),
            symbol = "PETR4",
            side = "BUY",
            quantity = 100,
            price = 32.15m
        });

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<CreateOrderResult>(JsonOptions);
        payload.Should().NotBeNull();
        payload!.Status.Should().Be("PENDING");
        payload.CorrelationId.Should().NotBeNullOrWhiteSpace();

        await AssertOrderAndOutboxAsync(payload.OrderId);
    }

    [Fact]
    public async Task TimelineEndpoint_ShouldReturnOrderedFlowEvents_WhenOrderExists()
    {
        if (!_factory.IsEnabled)
        {
            return;
        }

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/lab/api/smoke/run", new
        {
            accountId = Guid.NewGuid(),
            symbol = "PETR4",
            side = "BUY",
            quantity = 100,
            price = 11.25m
        });

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<CreateOrderResult>(JsonOptions);
        payload.Should().NotBeNull();

        var timelineResponse = await client.GetAsync($"/lab/api/orders/{payload!.OrderId}/timeline");
        timelineResponse.EnsureSuccessStatusCode();

        var timeline = await timelineResponse.Content.ReadFromJsonAsync<List<FlowEventResponse>>(JsonOptions);
        timeline.Should().NotBeNull();
        timeline!.Should().NotBeEmpty();
        timeline.Select(x => x.Stage).Should().Contain("ORDER_RECEIVED");
        timeline.Select(x => x.Stage).Should().Contain("OUTBOX_STORED");
        timeline.Select(x => x.CreatedAt).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task DlqOverviewEndpoint_ShouldReturnExpectedShape()
    {
        if (!_factory.IsEnabled)
        {
            return;
        }

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/lab/api/dlq/overview");
        response.EnsureSuccessStatusCode();

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.TryGetProperty("rabbitPendingCount", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("kafkaDlqBacklogEstimate", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("latestFailures", out var latestFailures).Should().BeTrue();
        latestFailures.ValueKind.Should().Be(JsonValueKind.Array);
    }

    private async Task AssertOrderAndOutboxAsync(Guid orderId)
    {
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();

        await using var orderCommand = connection.CreateCommand();
        orderCommand.CommandText = "SELECT COUNT(*) FROM orders.orders WHERE id = @id";
        orderCommand.Parameters.AddWithValue("id", orderId);
        var orderCount = (long)(await orderCommand.ExecuteScalarAsync() ?? 0L);
        orderCount.Should().Be(1);

        await using var outboxCommand = connection.CreateCommand();
        outboxCommand.CommandText = "SELECT COUNT(*) FROM integration.outbox_messages WHERE aggregate_id = @id";
        outboxCommand.Parameters.AddWithValue("id", orderId);
        var outboxCount = (long)(await outboxCommand.ExecuteScalarAsync() ?? 0L);
        outboxCount.Should().Be(1);
    }

    private sealed record CreateOrderResult(Guid OrderId, string Status, string CorrelationId);

    private sealed record FlowEventResponse(string Stage, DateTimeOffset CreatedAt);
}

public sealed class OrderApiFactory : WebApplicationFactory<Program>
{
    public string? ConnectionString { get; } = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION_STRING");

    public bool IsEnabled => !string.IsNullOrWhiteSpace(ConnectionString);

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            if (!IsEnabled)
            {
                return;
            }

            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = ConnectionString,
                ["Kafka:BootstrapServers"] = "localhost:9092",
                ["RabbitMq:HostName"] = "localhost",
                ["RabbitMq:Port"] = "5672",
                ["RabbitMq:UserName"] = "guest",
                ["RabbitMq:Password"] = "guest",
                ["RabbitMq:VirtualHost"] = "/"
            };

            configBuilder.AddInMemoryCollection(settings);
        });
    }
}
