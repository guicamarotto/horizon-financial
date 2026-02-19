using Dapper;
using Npgsql;

namespace BuildingBlocks.Persistence.Stores;

public sealed class ProcessedMessageStore : IProcessedMessageStore
{
    private readonly NpgsqlDataSource _dataSource;

    public ProcessedMessageStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<bool> TryMarkProcessedAsync(string consumerName, Guid messageId, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO integration.processed_messages (consumer_name, message_id, processed_at)
            VALUES (@ConsumerName, @MessageId, NOW())
            ON CONFLICT (consumer_name, message_id) DO NOTHING;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var affectedRows = await connection.ExecuteAsync(new CommandDefinition(sql, new { ConsumerName = consumerName, MessageId = messageId }, cancellationToken: cancellationToken));
        return affectedRows == 1;
    }
}
