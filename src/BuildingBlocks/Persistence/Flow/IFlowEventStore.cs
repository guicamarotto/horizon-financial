namespace BuildingBlocks.Persistence.Flow;

public interface IFlowEventStore
{
    Task AppendAsync(FlowEventAppendRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<FlowEventReadModel>> GetTimelineAsync(Guid orderId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<FlowEventReadModel>> GetLatestByStagesAsync(string[] stages, int limit, CancellationToken cancellationToken);
}
