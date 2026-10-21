using Orleans;
using Orleans.Runtime;

namespace RedisStreamsProvider.IntegrationTests.Cluster;

[Collection(ClusterCollection.Name)]
public sealed class StreamDeliveryTests(ClusterFixture fixture) : IClassFixture<ClusterFixture>
{
    [Fact]
    public async Task Events_from_a_client_reach_the_implicitly_subscribed_grain_in_order()
    {
        var key = $"order-{Guid.NewGuid():N}";
        var stream = fixture.Cluster.Client.GetStreamProvider(ClusterFixture.ProviderName)
            .GetStream<int>(StreamId.Create(EventCollectorGrain.StreamNamespace, key));

        for (var i = 0; i < 20; i++)
        {
            await stream.OnNextAsync(i);
        }

        // Delivery is at-least-once: a queue rebalance right after the cluster starts can redeliver events.
        await Eventually.WaitUntilAsync(() => ReceivedEvents.For(key).Distinct().Count() >= 20, TimeSpan.FromSeconds(30));
        Assert.Equal(Enumerable.Range(0, 20), ReceivedEvents.For(key).Distinct());
    }

    [Fact]
    public async Task Events_on_different_streams_reach_only_their_own_grain()
    {
        var keys = Enumerable.Range(0, 3).Select(i => $"multi-{i}-{Guid.NewGuid():N}").ToArray();
        var provider = fixture.Cluster.Client.GetStreamProvider(ClusterFixture.ProviderName);

        foreach (var key in keys)
        {
            var stream = provider.GetStream<int>(StreamId.Create(EventCollectorGrain.StreamNamespace, key));
            for (var i = 0; i < 10; i++)
            {
                await stream.OnNextAsync(i);
            }
        }

        foreach (var key in keys)
        {
            await Eventually.WaitUntilAsync(() => ReceivedEvents.For(key).Distinct().Count() >= 10, TimeSpan.FromSeconds(30));
            Assert.Equal(Enumerable.Range(0, 10), ReceivedEvents.For(key).Distinct());
        }
    }

    [Fact]
    public async Task Events_reach_a_grain_that_subscribed_with_an_interface_they_implement()
    {
        var key = $"interface-{Guid.NewGuid():N}";
        var stream = fixture.Cluster.Client.GetStreamProvider(ClusterFixture.ProviderName)
            .GetStream<CollectedEvent>(StreamId.Create(InterfaceCollectorGrain.StreamNamespace, key));

        for (var i = 0; i < 5; i++)
        {
            await stream.OnNextAsync(new CollectedEvent(i));
        }

        await Eventually.WaitUntilAsync(() => ReceivedEvents.For(key).Distinct().Count() >= 5, TimeSpan.FromSeconds(30));
        Assert.Equal(Enumerable.Range(0, 5), ReceivedEvents.For(key).Distinct());
    }
}
