using StackExchange.Redis;
using Testcontainers.Redis;

namespace RedisStreamsProvider.IntegrationTests;

/// <summary>A Redis container shared by one test collection.</summary>
public abstract class RedisContainerFixture(string image) : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder(image).Build();

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

public sealed class RedisFixture() : RedisContainerFixture("redis:7.4");

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "Redis";
}

/// <summary>Redis 6.0, which lacks <c>XTRIM MINID</c>, as Azure Cache for Redis Basic, Standard and Premium still run.</summary>
public sealed class Redis60Fixture() : RedisContainerFixture("redis:6.0");

[CollectionDefinition(Name)]
public sealed class Redis60Collection : ICollectionFixture<Redis60Fixture>
{
    public const string Name = "Redis 6.0";
}
