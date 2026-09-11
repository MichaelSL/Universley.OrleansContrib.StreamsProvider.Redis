using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Streams;

namespace RedisStreamsProvider.IntegrationTests.Cluster;

// Gets its own ClusterFixture because it stops a silo; sharing a cluster with other tests would break them.
[Collection(ClusterCollection.Name)]
public sealed class SiloFailoverTests(ClusterFixture fixture) : IClassFixture<ClusterFixture>
{
    [Fact]
    public async Task No_events_are_lost_when_a_silo_leaves_mid_stream()
    {
        var keys = KeysCoveringEveryQueue();
        var provider = fixture.Cluster.Client.GetStreamProvider(ClusterFixture.ProviderName);
        var streams = keys.Select(key => provider.GetStream<int>(StreamId.Create(EventCollectorGrain.StreamNamespace, key))).ToArray();

        foreach (var stream in streams)
        {
            for (var i = 0; i < 25; i++)
            {
                await stream.OnNextAsync(i);
            }
        }

        await fixture.Cluster.StopSiloAsync(fixture.Cluster.SecondarySilos[0]);

        foreach (var stream in streams)
        {
            for (var i = 25; i < 50; i++)
            {
                await stream.OnNextAsync(i);
            }
        }

        foreach (var key in keys)
        {
            // At-least-once: duplicates are allowed, gaps are not.
            await Eventually.WaitUntilAsync(() => ReceivedEvents.For(key).Distinct().Count() >= 50, TimeSpan.FromSeconds(60));
            Assert.Equal(Enumerable.Range(0, 50), ReceivedEvents.For(key).Distinct().Order());
        }
    }

    // Picks one stream key per queue, using the same mapper the provider builds internally, so that whichever
    // silo is stopped mid-test is guaranteed to own a queue carrying in-flight events for at least one key.
    private static string[] KeysCoveringEveryQueue()
    {
        var mapper = new HashRingBasedStreamQueueMapper(
            new HashRingStreamQueueMapperOptions { TotalQueueCount = ClusterFixture.QueueCount },
            ClusterFixture.ProviderName);

        var keysByQueue = new Dictionary<QueueId, string>();
        while (keysByQueue.Count < ClusterFixture.QueueCount)
        {
            var key = $"failover-{Guid.NewGuid():N}";
            var queueId = mapper.GetQueueForStream(StreamId.Create(EventCollectorGrain.StreamNamespace, key));
            keysByQueue.TryAdd(queueId, key);
        }

        return [.. keysByQueue.Values];
    }
}
