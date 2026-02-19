using BuildingBlocks.Messaging.Headers;

namespace BuildingBlocks.Messaging.Kafka;

public interface IKafkaPublisher
{
    Task PublishAsync(string topic, string payload, MessageHeaders headers, CancellationToken cancellationToken);
}
