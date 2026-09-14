using StackExchange.Redis;
using Testcontainers.Redis;

namespace RedisStreamsProvider.IntegrationTests;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7.4").Build();

    private IConnectionMultiplexer? _connection;

    public IConnectionMultiplexer Connection =>
        _connection ?? throw new InvalidOperationException("The Redis fixture has not been initialized.");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _connection = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        // The connection is still null when InitializeAsync failed before connecting.
        if (_connection is not null)
        {
            await _connection.CloseAsync();
            _connection.Dispose();
        }

        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "Redis";
}
