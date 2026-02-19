using BuildingBlocks.Messaging.Headers;
using BuildingBlocks.Messaging.Options;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Messaging.Kafka;

public sealed class KafkaPublisher : IKafkaPublisher, IDisposable
{
    private readonly ILogger<KafkaPublisher> _logger;
    private readonly IProducer<Null, string> _producer;

    public KafkaPublisher(IOptions<KafkaOptions> options, ILogger<KafkaPublisher> logger)
    {
        _logger = logger;

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = 30000,
            RetryBackoffMs = 500
        };

        _producer = new ProducerBuilder<Null, string>(producerConfig).Build();
    }

    public async Task PublishAsync(string topic, string payload, MessageHeaders headers, CancellationToken cancellationToken)
    {
        var message = new Message<Null, string>
        {
            Value = payload,
            Headers = headers.ToKafkaHeaders()
        };

        var deliveryResult = await _producer.ProduceAsync(topic, message, cancellationToken);

        _logger.LogInformation(
            "Kafka message published to {Topic} partition {Partition} offset {Offset} message_id={MessageId} correlation_id={CorrelationId}",
            topic,
            deliveryResult.Partition.Value,
            deliveryResult.Offset.Value,
            headers.MessageId,
            headers.CorrelationId);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}
