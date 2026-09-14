using System.Collections.Concurrent;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;

namespace RedisStreamsProvider.IntegrationTests.Cluster;

/// <summary>
/// Silos run in the test process, so a static store survives grain deactivation and silo shutdown.
/// </summary>
public static class ReceivedEvents
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<int>> ByStreamKey = new();

    public static void Record(string streamKey, int item) => ByStreamKey.GetOrAdd(streamKey, _ => new()).Enqueue(item);

    public static int[] For(string streamKey) => ByStreamKey.TryGetValue(streamKey, out var items) ? items.ToArray() : [];
}

public interface IEventCollectorGrain : IGrainWithStringKey
{
}

[ImplicitStreamSubscription(StreamNamespace)]
public sealed class EventCollectorGrain : Grain, IEventCollectorGrain, IAsyncObserver<int>
{
    public const string StreamNamespace = "it-collector";

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamId = StreamId.Create(StreamNamespace, this.GetPrimaryKeyString());
        await this.GetStreamProvider(ClusterFixture.ProviderName).GetStream<int>(streamId).SubscribeAsync(this);
        await base.OnActivateAsync(cancellationToken);
    }

    public Task OnNextAsync(int item, StreamSequenceToken? token = null)
    {
        ReceivedEvents.Record(this.GetPrimaryKeyString(), item);
        return Task.CompletedTask;
    }

    public Task OnCompletedAsync() => Task.CompletedTask;

    public Task OnErrorAsync(Exception ex) => Task.CompletedTask;
}
