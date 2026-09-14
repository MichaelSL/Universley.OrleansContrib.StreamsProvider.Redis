using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime;
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace RedisStreamsProvider.IntegrationTests;

[Collection(Redis60Collection.Name)]
public sealed class Redis60TrimmingTests(Redis60Fixture redis)
{
    private const int Published = 250;
    private static readonly TimeSpan PastTrimInterval = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task Auto_trimming_detects_Redis_6_0_at_startup_and_caps_the_stream()
    {
        var logs = new FakeLogCollector();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new FakeLoggerProvider(logs)));
        var factory = ProviderHarness.CreateFactory(redis.Connection, loggerFactory, new RedisStreamReceiverOptions { MaxStreamLength = 10, TrimTimeMinutes = 1 });

        var adapter = await factory.CreateAdapter();
        Assert.Contains(logs.GetSnapshot(), IsFallbackWarning);

        var queueId = factory.GetStreamQueueMapper().GetAllQueues().Single();
        var receiver = (RedisStreamReceiver)adapter.CreateReceiver(queueId);
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        await adapter.QueueMessageBatchAsync(StreamId.Create("it-namespace", "it-key"),
            Enumerable.Range(0, Published).Select(i => new TestEvent(i, "e")), null!, []);

        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        receiver.SetTimeProvider(time);
        time.Advance(PastTrimInterval);
        await receiver.TrimStreamIfNeeded();

        Assert.True(await redis.Connection.GetDatabase().StreamLengthAsync(queueId.ToString()) < Published);
        Assert.DoesNotContain(logs.GetSnapshot(), r => r.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Auto_trimming_falls_back_to_MaxLength_when_the_first_trim_finds_MINID_unsupported()
    {
        // Without the startup probe, only the trim itself can find out that the server is too old.
        var harness = await PublishedHarnessAsync(RedisStreamTrimStrategy.Auto);
        var logger = new FakeLogger<RedisStreamReceiver>();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var receiver = await TrimOnceAsync(harness, logger, time);

        Assert.True(await harness.Database.StreamLengthAsync(harness.Key) < Published);
        Assert.Single(logger.Collector.GetSnapshot(), IsFallbackWarning);
        Assert.DoesNotContain(logger.Collector.GetSnapshot(), r => r.Level == LogLevel.Error);

        // Later trims go straight to MaxLength without warning again.
        await harness.PublishAsync(Enumerable.Range(0, Published).Select(i => new TestEvent(i, "e")).ToArray());
        time.Advance(PastTrimInterval);
        await receiver.TrimStreamIfNeeded();
        Assert.Single(logger.Collector.GetSnapshot(), IsFallbackWarning);
    }

    [Fact]
    public async Task AcknowledgedOnly_trimming_logs_an_error_and_never_falls_back()
    {
        var harness = await PublishedHarnessAsync(RedisStreamTrimStrategy.AcknowledgedOnly);
        var logger = new FakeLogger<RedisStreamReceiver>();
        await TrimOnceAsync(harness, logger, new FakeTimeProvider(DateTimeOffset.UtcNow));

        Assert.Equal(Published, await harness.Database.StreamLengthAsync(harness.Key));
        Assert.Contains(logger.Collector.GetSnapshot(),
            r => r.Level == LogLevel.Error && r.Message.Contains("Error trimming stream") && r.Exception is NotSupportedException);
        Assert.DoesNotContain(logger.Collector.GetSnapshot(), IsFallbackWarning);
    }

    private static bool IsFallbackWarning(FakeLogRecord record) =>
        record.Level == LogLevel.Warning && record.Message.Contains("falls back to MaxLength");

    private async Task<ProviderHarness> PublishedHarnessAsync(RedisStreamTrimStrategy strategy)
    {
        var harness = new ProviderHarness(redis.Connection, new RedisStreamReceiverOptions { MaxStreamLength = 10, TrimTimeMinutes = 1, TrimStrategy = strategy });
        await harness.PublishAsync(Enumerable.Range(0, Published).Select(i => new TestEvent(i, "e")).ToArray());
        return harness;
    }

    private static async Task<RedisStreamReceiver> TrimOnceAsync(ProviderHarness harness, ILogger<RedisStreamReceiver> logger, FakeTimeProvider time)
    {
        var receiver = harness.CreateReceiver(time, logger);
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        time.Advance(PastTrimInterval);
        await receiver.TrimStreamIfNeeded();
        return receiver;
    }
}
