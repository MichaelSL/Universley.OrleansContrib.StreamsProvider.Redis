using Microsoft.Extensions.Logging;
using Orleans.Streams;
using StackExchange.Redis;
using System.Text.Json;
using Microsoft.Extensions.Options; // Added for IOptions

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    public class RedisStreamAdapter : IQueueAdapter
    {
        private readonly IDatabase _database;
        private readonly string _providerName;
        private readonly HashRingBasedStreamQueueMapper _hashRingBasedStreamQueueMapper;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<RedisStreamAdapter> _logger;
        private readonly IOptions<RedisStreamReceiverOptions> _receiverOptions; // Added receiver options
        private readonly MinIdTrimSupport _minIdTrimSupport;

        // Changed: Constructor to accept IOptions<RedisStreamReceiverOptions>
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
            _receiverOptions = receiverOptions ?? throw new ArgumentNullException(nameof(receiverOptions)); // Store receiver options
            _minIdTrimSupport = minIdTrimSupport;
        }

        public string Name => _providerName;

        public bool IsRewindable => false;

        public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            // Pass receiver options to RedisStreamReceiver
            return new RedisStreamReceiver(queueId, _database, _loggerFactory.CreateLogger<RedisStreamReceiver>(), TimeProvider.System, _receiverOptions, _minIdTrimSupport);
        }

        public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            try
            {
                foreach (var @event in events)
                {
                    NameValueEntry streamNamespaceEntry = new(RedisStreamWireFormat.StreamNamespaceField, streamId.Namespace);
                    NameValueEntry streamKeyEntry = new(RedisStreamWireFormat.StreamKeyField, streamId.Key);
                    NameValueEntry eventTypeEntry = new(RedisStreamWireFormat.EventTypeField, @event!.GetType().Name);
                    NameValueEntry dataEntry = new(RedisStreamWireFormat.DataField, JsonSerializer.Serialize(@event));
                    var queueId = _hashRingBasedStreamQueueMapper.GetQueueForStream(streamId);
                    await _database.StreamAddAsync(RedisStreamWireFormat.StreamKey(queueId), [streamNamespaceEntry, streamKeyEntry, eventTypeEntry, dataEntry]);
                }
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
