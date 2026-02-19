namespace BuildingBlocks.Messaging.Headers;

public static class MessageHeaderNames
{
    public const string MessageId = "message_id";
    public const string CorrelationId = "correlation_id";
    public const string OrderId = "order_id";
    public const string CausationId = "causation_id";
    public const string EventType = "event_type";
    public const string EventVersion = "event_version";
    public const string OccurredAt = "occurred_at";
    public const string RetryCount = "x-retry-count";
}
