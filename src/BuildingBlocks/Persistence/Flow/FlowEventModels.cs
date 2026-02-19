namespace BuildingBlocks.Persistence.Flow;

public sealed record FlowEventAppendRequest(
    Guid? OrderId,
    string CorrelationId,
    string Service,
    string Stage,
    string Broker = "NONE",
    string? Channel = null,
    Guid? MessageId = null,
    string? PayloadSnippet = null,
    object? Metadata = null);

public sealed class FlowEventReadModel
{
    public Guid Id { get; init; }

    public Guid? OrderId { get; init; }

    public string CorrelationId { get; init; } = string.Empty;

    public string Service { get; init; } = string.Empty;

    public string Stage { get; init; } = string.Empty;

    public string Broker { get; init; } = "NONE";

    public string? Channel { get; init; }

    public Guid? MessageId { get; init; }

    public string? PayloadSnippet { get; init; }

    public string? Metadata { get; init; }

    public DateTime CreatedAt { get; init; }
}
