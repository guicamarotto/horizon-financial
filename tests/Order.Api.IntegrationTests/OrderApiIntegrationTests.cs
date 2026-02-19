using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Order.Api.IntegrationTests;

public sealed class OrderApiIntegrationTests : IClassFixture<OrderApiFactory>
{
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
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        payload.Should().NotBeNull();
        var orderId = Guid.Parse(payload!["orderId"].ToString()!);

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM orders.orders WHERE id = @id";
            cmd.Parameters.AddWithValue("id", orderId);
            var orderCount = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
            orderCount.Should().Be(1);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM integration.outbox_messages WHERE aggregate_id = @id";
            cmd.Parameters.AddWithValue("id", orderId);
            var outboxCount = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
            outboxCount.Should().Be(1);
        }
    }
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
