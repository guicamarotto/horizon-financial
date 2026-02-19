namespace BuildingBlocks.Messaging.Contracts;

public sealed record OrderCreatedV1(
    Guid OrderId,
    Guid AccountId,
    string Symbol,
    string Side,
    decimal Quantity,
    decimal Price,
    DateTimeOffset CreatedAt,
    string Status);

public sealed record ReserveLimitV1(
    Guid OrderId,
    Guid AccountId,
    string Symbol,
    string Side,
    decimal Quantity,
    decimal Price,
    decimal Notional,
    DateTimeOffset RequestedAt);

public sealed record LimitReservedV1(
    Guid OrderId,
    Guid AccountId,
    decimal ReservedAmount,
    DateTimeOffset ReservedAt);

public sealed record LimitRejectedV1(
    Guid OrderId,
    Guid AccountId,
    string Reason,
    DateTimeOffset RejectedAt);

public sealed record DeadLetterEventV1(
    Guid MessageId,
    string Source,
    string TargetTopic,
    string Reason,
    string OriginalPayload,
    IReadOnlyDictionary<string, string> OriginalHeaders,
    DateTimeOffset FailedAt);
