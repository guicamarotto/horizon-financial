using System.Text;
using Confluent.Kafka;
using RabbitMQ.Client;

namespace BuildingBlocks.Messaging.Headers;

public static class MessageHeaderConverter
{
    public static Confluent.Kafka.Headers ToKafkaHeaders(this MessageHeaders messageHeaders)
    {
        var headers = new Confluent.Kafka.Headers();

        foreach (var (key, value) in messageHeaders.ToDictionary())
        {
            headers.Add(key, Encoding.UTF8.GetBytes(value));
        }

        return headers;
    }

    public static MessageHeaders FromKafkaHeaders(Confluent.Kafka.Headers? kafkaHeaders)
    {
        if (kafkaHeaders is null)
        {
            return MessageHeaders.Create(Guid.Empty, "unknown", "v1", Guid.NewGuid().ToString("D"));
        }

        var headers = kafkaHeaders
            .ToDictionary(static header => header.Key, static header => header.GetValueBytes() is { Length: > 0 } value ? Encoding.UTF8.GetString(value) : string.Empty, StringComparer.OrdinalIgnoreCase);

        return MessageHeaders.FromDictionary(headers);
    }

    public static IDictionary<string, object> ToRabbitHeaders(this MessageHeaders messageHeaders)
    {
        return messageHeaders
            .ToDictionary()
            .ToDictionary(static x => x.Key, static x => (object)Encoding.UTF8.GetBytes(x.Value), StringComparer.OrdinalIgnoreCase);
    }

    public static MessageHeaders FromRabbitHeaders(IBasicProperties properties)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (properties.Headers is not null)
        {
            foreach (var entry in properties.Headers)
            {
                headers[entry.Key] = entry.Value switch
                {
                    byte[] bytes => Encoding.UTF8.GetString(bytes),
                    string text => text,
                    _ => entry.Value?.ToString() ?? string.Empty
                };
            }
        }

        if (!string.IsNullOrWhiteSpace(properties.MessageId))
        {
            headers[MessageHeaderNames.MessageId] = properties.MessageId;
        }

        if (!string.IsNullOrWhiteSpace(properties.CorrelationId))
        {
            headers[MessageHeaderNames.CorrelationId] = properties.CorrelationId;
        }

        return MessageHeaders.FromDictionary(headers);
    }
}
