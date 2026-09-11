using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace RedisStreamsProvider.IntegrationTests;

[Collection(RedisCollection.Name)]
public sealed class TrimmingTests(RedisFixture redis)
{
    private static readonly TimeSpan PastTrimInterval = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task Default_trimming_never_deletes_unacknowledged_entries()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var harness = new ProviderHarness(redis.Connection, new RedisStreamReceiverOptions { MaxStreamLength = 10, TrimTimeMinutes = 1 });
        var receiver = harness.CreateReceiver(time);
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        await harness.PublishAsync(Enumerable.Range(0, 250).Select(i => new TestEvent(i, "e")).ToArray());
        var read = await ProviderHarness.ReadAsync(receiver, expected: 250, maxCount: 250);

        // The oldest 50 stay unacknowledged, e.g. a slow stream sharing the queue with fast ones.
        await receiver.MessagesDeliveredAsync(read.Skip(50).ToList());
        time.Advance(PastTrimInterval);
        await receiver.TrimStreamIfNeeded();

        var remaining = (await harness.Database.StreamRangeAsync(harness.Key)).Select(e => e.Id.ToString()).ToHashSet();
        Assert.All(read.Take(50).Select(ProviderHarness.EntryId), id => Assert.Contains(id, remaining));
    }

    [Fact]
    public async Task Default_trimming_deletes_acknowledged_entries()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var harness = new ProviderHarness(redis.Connection, new RedisStreamReceiverOptions { TrimTimeMinutes = 1 });
        var receiver = harness.CreateReceiver(time);
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        await harness.PublishAsync(Enumerable.Range(0, 250).Select(i => new TestEvent(i, "e")).ToArray());
        var read = await ProviderHarness.ReadAsync(receiver, expected: 250, maxCount: 250);

        await receiver.MessagesDeliveredAsync(read.Take(200).ToList());
        time.Advance(PastTrimInterval);
        await receiver.TrimStreamIfNeeded();

        var remaining = (await harness.Database.StreamRangeAsync(harness.Key)).Select(e => e.Id.ToString()).ToHashSet();
        Assert.True(remaining.Count < 250, $"Expected acknowledged entries to be trimmed, but {remaining.Count} remain.");
        Assert.All(read.Skip(200).Select(ProviderHarness.EntryId), id => Assert.Contains(id, remaining));
    }

    [Fact]
    public async Task Default_trimming_warns_when_the_backlog_exceeds_MaxStreamLength()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logger = new FakeLogger<RedisStreamReceiver>();
        var harness = new ProviderHarness(redis.Connection, new RedisStreamReceiverOptions { MaxStreamLength = 10, TrimTimeMinutes = 1 });
        var receiver = harness.CreateReceiver(time, logger);
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        await harness.PublishAsync(Enumerable.Range(0, 20).Select(i => new TestEvent(i, "e")).ToArray());
        await ProviderHarness.ReadAsync(receiver, expected: 20);

        time.Advance(PastTrimInterval);
        await receiver.TrimStreamIfNeeded();

        Assert.Contains(logger.Collector.GetSnapshot(),
            r => r.Level == LogLevel.Warning && r.Message.Contains("consumers may be falling behind"));
    }
}
