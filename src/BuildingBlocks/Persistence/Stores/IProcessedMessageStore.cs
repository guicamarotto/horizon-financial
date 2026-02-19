namespace BuildingBlocks.Persistence.Stores;

public interface IProcessedMessageStore
{
    Task<bool> TryMarkProcessedAsync(string consumerName, Guid messageId, CancellationToken cancellationToken);

    Task UnmarkProcessedAsync(string consumerName, Guid messageId, CancellationToken cancellationToken);
}
