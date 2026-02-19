using System.Globalization;

namespace BuildingBlocks.Messaging.Headers;

public sealed record MessageHeaders(
    Guid MessageId,
    string CorrelationId,
    Guid OrderId,
    string? CausationId,
    string EventType,
    string EventVersion,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string>? Additional = null)
{
    public static MessageHeaders Create(
        Guid orderId,
        string eventType,
        string eventVersion,
        string correlationId,
        string? causationId = null,
        Guid? messageId = null,
        DateTimeOffset? occurredAt = null,
        IReadOnlyDictionary<string, string>? additional = null)
    {
        return new MessageHeaders(
            messageId ?? Guid.NewGuid(),
            correlationId,
            orderId,
            causationId,
            eventType,
            eventVersion,
            occurredAt ?? DateTimeOffset.UtcNow,
            additional);
    }

    public Dictionary<string, string> ToDictionary()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MessageHeaderNames.MessageId] = MessageId.ToString("D"),
            [MessageHeaderNames.CorrelationId] = CorrelationId,
            [MessageHeaderNames.OrderId] = OrderId.ToString("D"),
            [MessageHeaderNames.EventType] = EventType,
            [MessageHeaderNames.EventVersion] = EventVersion,
            [MessageHeaderNames.OccurredAt] = OccurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
        };

        if (!string.IsNullOrWhiteSpace(CausationId))
        {
            headers[MessageHeaderNames.CausationId] = CausationId;
        }

        if (Additional is not null)
        {
            foreach (var entry in Additional)
            {
                headers[entry.Key] = entry.Value;
            }
        }

        return headers;
    }

    public static MessageHeaders FromDictionary(IReadOnlyDictionary<string, string> headers)
    {
        var messageId = Guid.TryParse(headers.GetValueOrDefault(MessageHeaderNames.MessageId), out var parsedMessageId)
            ? parsedMessageId
            : Guid.NewGuid();

        var orderId = Guid.TryParse(headers.GetValueOrDefault(MessageHeaderNames.OrderId), out var parsedOrderId)
            ? parsedOrderId
            : Guid.Empty;

        var occurredAt = DateTimeOffset.TryParse(headers.GetValueOrDefault(MessageHeaderNames.OccurredAt), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedOccurredAt)
            ? parsedOccurredAt
            : DateTimeOffset.UtcNow;

        var additional = headers
            .Where(static x => x.Key is not MessageHeaderNames.MessageId
                and not MessageHeaderNames.CorrelationId
                and not MessageHeaderNames.OrderId
                and not MessageHeaderNames.CausationId
                and not MessageHeaderNames.EventType
                and not MessageHeaderNames.EventVersion
                and not MessageHeaderNames.OccurredAt)
            .ToDictionary(static x => x.Key, static x => x.Value, StringComparer.OrdinalIgnoreCase);

        return new MessageHeaders(
            messageId,
            headers.GetValueOrDefault(MessageHeaderNames.CorrelationId) ?? Guid.NewGuid().ToString("D"),
            orderId,
            headers.GetValueOrDefault(MessageHeaderNames.CausationId),
            headers.GetValueOrDefault(MessageHeaderNames.EventType) ?? "unknown",
            headers.GetValueOrDefault(MessageHeaderNames.EventVersion) ?? "v1",
            occurredAt,
            additional);
    }
}
