using BuildingBlocks.Messaging.Options;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace BuildingBlocks.Messaging.Rabbit;

public interface IRabbitConnectionProvider : IDisposable
{
    IConnection GetConnection();
}

public sealed class RabbitConnectionProvider : IRabbitConnectionProvider
{
    private readonly object _sync = new();
    private readonly ConnectionFactory _factory;
    private IConnection? _connection;

    public RabbitConnectionProvider(IOptions<RabbitOptions> options)
    {
        var rabbitOptions = options.Value;

        _factory = new ConnectionFactory
        {
            HostName = rabbitOptions.HostName,
            Port = rabbitOptions.Port,
            UserName = rabbitOptions.UserName,
            Password = rabbitOptions.Password,
            VirtualHost = rabbitOptions.VirtualHost,
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };
    }

    public IConnection GetConnection()
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        lock (_sync)
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            _connection?.Dispose();
            _connection = _factory.CreateConnection();
            return _connection;
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
