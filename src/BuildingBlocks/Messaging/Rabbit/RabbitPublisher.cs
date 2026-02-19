using System.Text;
using BuildingBlocks.Messaging.Headers;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Messaging.Rabbit;

public sealed class RabbitPublisher : IRabbitPublisher
{
    private readonly ILogger<RabbitPublisher> _logger;
    private readonly IRabbitConnectionProvider _connectionProvider;

    public RabbitPublisher(IRabbitConnectionProvider connectionProvider, ILogger<RabbitPublisher> logger)
    {
        _connectionProvider = connectionProvider;
        _logger = logger;
    }

    public Task PublishAsync(string exchange, string routingKey, string payload, MessageHeaders headers, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var channel = _connectionProvider.GetConnection().CreateModel();
        var body = Encoding.UTF8.GetBytes(payload);

        var properties = channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.MessageId = headers.MessageId.ToString("D");
        properties.CorrelationId = headers.CorrelationId;
        properties.Headers = headers.ToRabbitHeaders();

        channel.BasicPublish(exchange, routingKey, mandatory: false, basicProperties: properties, body: body);

        _logger.LogInformation(
            "RabbitMQ message published exchange={Exchange} routing_key={RoutingKey} message_id={MessageId} correlation_id={CorrelationId}",
            exchange,
            routingKey,
            headers.MessageId,
            headers.CorrelationId);

        return Task.CompletedTask;
    }
}
