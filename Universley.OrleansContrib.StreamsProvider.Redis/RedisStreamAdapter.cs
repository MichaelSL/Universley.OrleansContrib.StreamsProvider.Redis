using Microsoft.Extensions.Logging;
using Orleans.Streams;
using StackExchange.Redis;
using System.Text.Json;

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    public class RedisStreamAdapter : IQueueAdapter
    {
        private readonly IDatabase _database;
        private readonly string _providerName;
        private readonly HashRingBasedStreamQueueMapper _hashRingBasedStreamQueueMapper;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<RedisStreamAdapter> _logger;

        public RedisStreamAdapter(IDatabase database, string providerName, HashRingBasedStreamQueueMapper hashRingBasedStreamQueueMapper, ILoggerFactory loggerFactory)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _providerName = providerName ?? throw new ArgumentNullException(nameof(providerName));
            _hashRingBasedStreamQueueMapper = hashRingBasedStreamQueueMapper ?? throw new ArgumentNullException(nameof(hashRingBasedStreamQueueMapper));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _logger = loggerFactory.CreateLogger<RedisStreamAdapter>();
        }

        public string Name => _providerName;

        public bool IsRewindable => false;

        public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            return new RedisStreamReceiver(queueId, _database, _loggerFactory.CreateLogger<RedisStreamReceiver>());
        }

        public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            try
            {
                foreach (var @event in events)
                {
                    NameValueEntry streamNamespaceEntry = new("streamNamespace", streamId.Namespace);
                    NameValueEntry streamKeyEntry = new("streamKey", streamId.Key);
                    NameValueEntry eventTypeEntry = new("eventType", @event!.GetType().Name);
                    NameValueEntry dataEntry = new("data", JsonSerializer.Serialize(@event));
                    var queueId = _hashRingBasedStreamQueueMapper.GetQueueForStream(streamId);
                    await _database.StreamAddAsync(queueId.ToString(), [streamNamespaceEntry, streamKeyEntry, eventTypeEntry, dataEntry]);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding event to stream {StreamId}", streamId);
            }
        }
    }
}
