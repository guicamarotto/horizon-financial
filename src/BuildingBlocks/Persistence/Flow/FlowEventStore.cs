using System.Text.Json;
using Dapper;
using Npgsql;

namespace BuildingBlocks.Persistence.Flow;

public sealed class FlowEventStore : IFlowEventStore
{
    private const int MaxSnippetLength = 240;
    private const string EnsureSchemaSql = """
        CREATE SCHEMA IF NOT EXISTS integration;

        CREATE TABLE IF NOT EXISTS integration.flow_events (
            id UUID PRIMARY KEY,
            order_id UUID NULL,
            correlation_id TEXT NOT NULL,
            service TEXT NOT NULL,
            stage TEXT NOT NULL,
            broker TEXT NULL,
            channel TEXT NULL,
            message_id UUID NULL,
            payload_snippet TEXT NULL,
            metadata JSONB NULL,
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );

        CREATE INDEX IF NOT EXISTS idx_flow_events_order_created ON integration.flow_events (order_id, created_at);
        CREATE INDEX IF NOT EXISTS idx_flow_events_correlation_created ON integration.flow_events (correlation_id, created_at);
        CREATE INDEX IF NOT EXISTS idx_flow_events_stage_created_desc ON integration.flow_events (stage, created_at DESC);
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _schemaReady;

    public FlowEventStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task AppendAsync(FlowEventAppendRequest request, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        const string sql = """
            INSERT INTO integration.flow_events
                (id, order_id, correlation_id, service, stage, broker, channel, message_id, payload_snippet, metadata, created_at)
            VALUES
                (@Id, @OrderId, @CorrelationId, @Service, @Stage, @Broker, @Channel, @MessageId, @PayloadSnippet, @Metadata::jsonb, NOW());
            """;

        var metadata = request.Metadata is null ? null : JsonSerializer.Serialize(request.Metadata, JsonOptions);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = Guid.NewGuid(),
                request.OrderId,
                CorrelationId = request.CorrelationId,
                Service = request.Service,
                Stage = request.Stage,
                Broker = request.Broker,
                request.Channel,
                request.MessageId,
                PayloadSnippet = Truncate(request.PayloadSnippet),
                Metadata = metadata
            },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<FlowEventReadModel>> GetTimelineAsync(Guid orderId, int limit, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        const string sql = """
            SELECT id AS Id,
                   order_id AS OrderId,
                   correlation_id AS CorrelationId,
                   service AS Service,
                   stage AS Stage,
                   COALESCE(broker, 'NONE') AS Broker,
                   channel AS Channel,
                   message_id AS MessageId,
                   payload_snippet AS PayloadSnippet,
                   metadata::text AS Metadata,
                   created_at AS CreatedAt
            FROM integration.flow_events
            WHERE order_id = @OrderId
            ORDER BY created_at ASC
            LIMIT @Limit;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<FlowEventReadModel>(new CommandDefinition(
            sql,
            new { OrderId = orderId, Limit = Math.Clamp(limit, 1, 1000) },
            cancellationToken: cancellationToken));

        return rows.ToList();
    }

    public async Task<IReadOnlyList<FlowEventReadModel>> GetLatestByStagesAsync(string[] stages, int limit, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        if (stages.Length == 0)
        {
            return [];
        }

        const string sql = """
            SELECT id AS Id,
                   order_id AS OrderId,
                   correlation_id AS CorrelationId,
                   service AS Service,
                   stage AS Stage,
                   COALESCE(broker, 'NONE') AS Broker,
                   channel AS Channel,
                   message_id AS MessageId,
                   payload_snippet AS PayloadSnippet,
                   metadata::text AS Metadata,
                   created_at AS CreatedAt
            FROM integration.flow_events
            WHERE stage = ANY(@Stages)
            ORDER BY created_at DESC
            LIMIT @Limit;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<FlowEventReadModel>(new CommandDefinition(
            sql,
            new { Stages = stages, Limit = Math.Clamp(limit, 1, 1000) },
            cancellationToken: cancellationToken));

        return rows.ToList();
    }

    private static string? Truncate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        return input.Length <= MaxSnippetLength
            ? input
            : input[..MaxSnippetLength];
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaReady)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady)
            {
                return;
            }

            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(EnsureSchemaSql, cancellationToken: cancellationToken));
            _schemaReady = true;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
