using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace RedisStreamsProvider.IntegrationTests;

internal sealed class ProviderHarness
{
    public ProviderHarness(IConnectionMultiplexer connection, RedisStreamReceiverOptions? receiverOptions = null)
    {
        Database = connection.GetDatabase();
        ReceiverOptions = receiverOptions ?? new RedisStreamReceiverOptions();

        // A unique queue prefix gives every test its own Redis stream key.
        var mapper = new HashRingBasedStreamQueueMapper(
            new HashRingStreamQueueMapperOptions { TotalQueueCount = 1 },
            $"it-{Guid.NewGuid():N}");
        QueueId = mapper.GetAllQueues().Single();
        Adapter = new RedisStreamAdapter(Database, "RedisStream", mapper, NullLoggerFactory.Instance, MsOptions.Create(ReceiverOptions), MsOptions.Create(new RedisStreamPublisherOptions()), Serializer);
    }

    /// <summary>A serializer that knows the event types in this assembly, like the one Orleans gives a silo.</summary>
    public static Serializer Serializer { get; } = new ServiceCollection()
        .AddSerializer(serializer => serializer.AddAssembly(typeof(TestEvent).Assembly))
        .BuildServiceProvider()
        .GetRequiredService<Serializer>();

    public IDatabase Database { get; }

    public RedisStreamReceiverOptions ReceiverOptions { get; }

    public QueueId QueueId { get; }

    public RedisKey Key => QueueId.ToString();

    public RedisStreamAdapter Adapter { get; }

    public StreamId StreamId { get; } = StreamId.Create("it-namespace", "it-key");

    public RedisStreamReceiver CreateReceiver(TimeProvider? timeProvider = null, ILogger<RedisStreamReceiver>? logger = null)
    {
        logger ??= NullLogger<RedisStreamReceiver>.Instance;
        var trimmer = new RedisStreamTrimmer(QueueId, Database, logger, timeProvider ?? TimeProvider.System, ReceiverOptions, new MinIdTrimSupport());
        return new(QueueId, Database, logger, new RedisStreamCodec(Serializer), trimmer);
    }

    /// <summary>A factory for a provider with one queue, whose stream key no other test uses.</summary>
    public static RedisStreamFactory CreateFactory(IConnectionMultiplexer connection, ILoggerFactory loggerFactory, RedisStreamReceiverOptions receiverOptions) =>
        new(connection, loggerFactory, $"it-{Guid.NewGuid():N}", new RedisStreamFailureHandler(loggerFactory.CreateLogger<RedisStreamFailureHandler>()),
            new SimpleQueueCacheOptions(), new HashRingStreamQueueMapperOptions { TotalQueueCount = 1 }, MsOptions.Create(receiverOptions), MsOptions.Create(new RedisStreamPublisherOptions()), Serializer);

    public Task PublishAsync<T>(params T[] events) =>
        Adapter.QueueMessageBatchAsync(StreamId, events, null!, new Dictionary<string, object>());

    /// <summary>Polls the receiver (at most 10 reads) until it has returned <paramref name="expected"/> batches.</summary>
    public static async Task<List<IBatchContainer>> ReadAsync(RedisStreamReceiver receiver, int expected, int maxCount = 100)
    {
        var result = new List<IBatchContainer>();
        for (var attempt = 0; attempt < 10 && result.Count < expected; attempt++)
        {
            var batch = await receiver.GetQueueMessagesAsync(maxCount);
            if (batch is not null)
            {
                result.AddRange(batch);
            }
        }

        return result;
    }

    public static string EntryId(IBatchContainer batch) => ((RedisStreamBatchContainer)batch).StreamEntryId;
}
