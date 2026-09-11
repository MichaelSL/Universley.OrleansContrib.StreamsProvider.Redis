using Orleans;
using Orleans.Runtime;

namespace RedisStreamsProvider.IntegrationTests.Cluster;

// Gets its own ClusterFixture because it stops a silo; sharing a cluster with other tests would break them.
[Collection(ClusterCollection.Name)]
public sealed class SiloFailoverTests(ClusterFixture fixture) : IClassFixture<ClusterFixture>
{
    [Fact]
    public async Task No_events_are_lost_when_a_silo_leaves_mid_stream()
    {
        var key = $"failover-{Guid.NewGuid():N}";
        var stream = fixture.Cluster.Client.GetStreamProvider(ClusterFixture.ProviderName)
            .GetStream<int>(StreamId.Create(EventCollectorGrain.StreamNamespace, key));

        for (var i = 0; i < 25; i++)
        {
            await stream.OnNextAsync(i);
        }

        await fixture.Cluster.StopSiloAsync(fixture.Cluster.SecondarySilos[0]);

        for (var i = 25; i < 50; i++)
        {
            await stream.OnNextAsync(i);
        }

        // At-least-once: duplicates are allowed, gaps are not.
        await Eventually.WaitUntilAsync(() => ReceivedEvents.For(key).Distinct().Count() >= 50, TimeSpan.FromSeconds(60));
        Assert.Equal(Enumerable.Range(0, 50), ReceivedEvents.For(key).Distinct().Order());
    }
}
