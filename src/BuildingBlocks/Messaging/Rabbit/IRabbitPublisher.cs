using BuildingBlocks.Messaging.Headers;

namespace BuildingBlocks.Messaging.Rabbit;

public interface IRabbitPublisher
{
    Task PublishAsync(string exchange, string routingKey, string payload, MessageHeaders headers, CancellationToken cancellationToken);
}
