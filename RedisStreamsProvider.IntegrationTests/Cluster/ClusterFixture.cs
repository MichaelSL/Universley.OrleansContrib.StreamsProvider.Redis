using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using StackExchange.Redis;
using Testcontainers.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace RedisStreamsProvider.IntegrationTests.Cluster;

public sealed class ClusterFixture : IAsyncLifetime
{
    public const string ProviderName = "RedisStream";
    public const int QueueCount = 2;

    private readonly RedisContainer _redis = new RedisBuilder("redis:7.4").Build();

    // Silos and the client are created in-process from configurator types, so they read the
    // connection string from here. Every test class using this fixture is in ClusterCollection,
    // which runs them one at a time, so fixtures never overwrite each other's value mid-startup.
    internal static string RedisConnectionString { get; private set; } = "";

    public TestCluster Cluster { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        RedisConnectionString = _redis.GetConnectionString();

        var builder = new TestClusterBuilder(initialSilosCount: 2);
        builder.AddSiloBuilderConfigurator<RedisStreamSiloConfigurator>();
        builder.AddClientBuilderConfigurator<RedisStreamClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        // Cluster is still null when InitializeAsync failed before building it.
        if (Cluster is not null)
        {
            await Cluster.StopAllSilosAsync();
            await Cluster.DisposeAsync();
        }

        await _redis.DisposeAsync();
    }

    internal static void AddRedisServices(IServiceCollection services)
    {
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(RedisConnectionString));
        services.AddOptions<HashRingStreamQueueMapperOptions>(ProviderName).Configure(options => options.TotalQueueCount = QueueCount);
    }
}

public sealed class RedisStreamSiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        ClusterFixture.AddRedisServices(siloBuilder.Services);
        siloBuilder.AddMemoryGrainStorage("PubSubStore");
        siloBuilder.AddPersistentStreams(ClusterFixture.ProviderName, RedisStreamFactory.Create, _ => { });
    }
}

public sealed class RedisStreamClientConfigurator : IClientBuilderConfigurator
{
    public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
    {
        ClusterFixture.AddRedisServices(clientBuilder.Services);
        clientBuilder.AddPersistentStreams(ClusterFixture.ProviderName, RedisStreamFactory.Create, _ => { });
    }
}

[CollectionDefinition(Name)]
public sealed class ClusterCollection
{
    public const string Name = "OrleansCluster";
}
