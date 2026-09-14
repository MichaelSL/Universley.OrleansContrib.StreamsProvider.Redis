using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Streams;
using StackExchange.Redis;

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    public class RedisStreamAdapter : IQueueAdapter
    {
        private readonly IDatabase _database;
        private readonly string _providerName;
        private readonly HashRingBasedStreamQueueMapper _hashRingBasedStreamQueueMapper;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<RedisStreamAdapter> _logger;
        private readonly IOptions<RedisStreamReceiverOptions> _receiverOptions;
        private readonly MinIdTrimSupport _minIdTrimSupport;

        public RedisStreamAdapter(IDatabase database,
                                string providerName,
                                HashRingBasedStreamQueueMapper hashRingBasedStreamQueueMapper,
                                ILoggerFactory loggerFactory,
                                IOptions<RedisStreamReceiverOptions> receiverOptions)
            : this(database, providerName, hashRingBasedStreamQueueMapper, loggerFactory, receiverOptions, new MinIdTrimSupport())
        {
        }

        /// <param name="minIdTrimSupport">Shared by every receiver this adapter creates.</param>
        internal RedisStreamAdapter(IDatabase database,
                                string providerName,
                                HashRingBasedStreamQueueMapper hashRingBasedStreamQueueMapper,
                                ILoggerFactory loggerFactory,
                                IOptions<RedisStreamReceiverOptions> receiverOptions,
                                MinIdTrimSupport minIdTrimSupport)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _providerName = providerName ?? throw new ArgumentNullException(nameof(providerName));
            _hashRingBasedStreamQueueMapper = hashRingBasedStreamQueueMapper ?? throw new ArgumentNullException(nameof(hashRingBasedStreamQueueMapper));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _logger = loggerFactory.CreateLogger<RedisStreamAdapter>();
            _receiverOptions = receiverOptions ?? throw new ArgumentNullException(nameof(receiverOptions));
            _minIdTrimSupport = minIdTrimSupport;
        }

        public string Name => _providerName;

        public bool IsRewindable => false;

        public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            var logger = _loggerFactory.CreateLogger<RedisStreamReceiver>();
            var trimmer = new RedisStreamTrimmer(queueId, _database, logger, TimeProvider.System, _receiverOptions.Value, _minIdTrimSupport);
            return new RedisStreamReceiver(queueId, _database, logger, trimmer);
        }

        public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            try
            {
                var streamKey = RedisStreamWireFormat.StreamKey(_hashRingBasedStreamQueueMapper.GetQueueForStream(streamId));
                // One transaction, so a failed call leaves none of its events in the stream for a retry to duplicate.
                var transaction = _database.CreateTransaction();
                var added = events.Select(@event => transaction.StreamAddAsync(streamKey, RedisStreamWireFormat.Encode(streamId, @event))).ToList();
                await transaction.ExecuteAsync();
                await Task.WhenAll(added);
            }
            catch (Exception ex)
            {
                // Rethrow so the producer's OnNextAsync fails and it can retry; swallowing loses the event.
                _logger.LogError(ex, "Error adding event to stream {StreamId}", streamId);
                throw;
            }
        }
    }
}
