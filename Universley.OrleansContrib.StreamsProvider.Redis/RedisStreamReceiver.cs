using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Streams;
using StackExchange.Redis;

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    public class RedisStreamReceiver : IQueueAdapterReceiver
    {
        private const string NewMessages = ">";
        private const int MaxReadCount = 1000;

        private readonly QueueId _queueId;
        // The stream key, which is also the consumer name.
        private readonly string _streamKey;
        private readonly IDatabase _database;
        private readonly ILogger<RedisStreamReceiver> _logger;
        private RedisStreamTrimmer _trimmer;

        // The next three fields track where this receiver stands in one incarnation of the stream key, and
        // StartOverOnNewStream resets them together.
        // The newest entry read and handed on, to Orleans or to be acknowledged as unreadable. Reads return ids in
        // ascending order and each read starts past the previous one, so every entry this consumer has read and not
        // handed on is newer.
        private RedisValue _lastReturnedId = "0";
        // While catching up, reads walk this consumer's pending list from _lastReturnedId instead of asking for new
        // messages: on start, to redeliver what a previous owner never acknowledged, and after a failed read.
        private bool _catchingUp = true;
        // Entries whose XACK failed. They stay pending in Redis, so they are sent again with the next acknowledgement
        // (XACK is idempotent); otherwise nothing would acknowledge them before the next restart or queue handoff.
        private readonly List<RedisValue> _unacknowledged = [];

        // The read or acknowledgement in progress, which Shutdown waits for. Neither ever faults.
        private Task _inFlight = Task.CompletedTask;

        public RedisStreamReceiver(QueueId queueId,
                                 IDatabase database,
                                 ILogger<RedisStreamReceiver> logger,
                                 TimeProvider? timeProvider = null,
                                 IOptions<RedisStreamReceiverOptions>? receiverOptions = null)
            : this(queueId, database, logger, new RedisStreamTrimmer(queueId, database, logger, timeProvider ?? TimeProvider.System,
                receiverOptions?.Value ?? new RedisStreamReceiverOptions(), new MinIdTrimSupport()))
        {
        }

        internal RedisStreamReceiver(QueueId queueId, IDatabase database, ILogger<RedisStreamReceiver> logger, RedisStreamTrimmer trimmer)
        {
            _queueId = queueId;
            _streamKey = RedisStreamWireFormat.StreamKey(queueId);
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _trimmer = trimmer;
        }

        /// <summary>Replaces the time provider; the next trim is due one full interval from now.</summary>
        public void SetTimeProvider(TimeProvider timeProvider)
        {
            _trimmer = _trimmer.WithTimeProvider(timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
        }

        public Task<IList<IBatchContainer>?> GetQueueMessagesAsync(int maxCount)
        {
            var read = ReadBatchesAsync(maxCount);
            _inFlight = read;
            return read;
        }

        private async Task<IList<IBatchContainer>?> ReadBatchesAsync(int maxCount)
        {
            try
            {
                var entries = await ReadEntriesAsync(maxCount is > 0 and < MaxReadCount ? maxCount : MaxReadCount);
                var batches = ToBatches(entries, out var unreadable);
                await AcknowledgeAsync(unreadable);
                await TrimStreamIfNeeded();
                return batches;
            }
            catch (RedisServerException ex) when (ex.Message.StartsWith("NOGROUP", StringComparison.Ordinal))
            {
                // The stream key (and with it the group) is gone. Without this every later read would fail forever.
                _logger.LogWarning(ex, "Consumer group for stream {QueueId} is missing, recreating it", _queueId);
                await TryRecreateConsumerGroupAsync();
                return [];
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading from stream {QueueId}", _queueId);
                // The read may have failed after Redis had already moved entries to this consumer's pending list
                // (a timeout or dropped connection loses the reply). Catching up delivers those entries now rather
                // than after the next restart or queue handoff.
                _catchingUp = true;
                return default;
            }
        }

        /// <summary>Reads the next entries and advances past them; every entry returned must be handed on.</summary>
        private async Task<StreamEntry[]> ReadEntriesAsync(int count)
        {
            var entries = await ReadFromAsync(_catchingUp ? _lastReturnedId : NewMessages, count);
            if (entries.Length == 0 && _catchingUp)
            {
                _catchingUp = false;
                entries = await ReadFromAsync(NewMessages, count);
            }

            if (entries.Length > 0)
            {
                _lastReturnedId = entries[^1].Id;
            }

            return entries;
        }

        private Task<StreamEntry[]> ReadFromAsync(RedisValue position, int count) =>
            _database.StreamReadGroupAsync(_streamKey, RedisStreamWireFormat.GroupName, _streamKey, position, count);

        private List<IBatchContainer> ToBatches(StreamEntry[] entries, out List<RedisValue> unreadable)
        {
            var batches = new List<IBatchContainer>(entries.Length);
            unreadable = [];
            foreach (var entry in entries)
            {
                if (RedisStreamWireFormat.TryDecode(entry, out var batch))
                {
                    batches.Add(batch);
                }
                else
                {
                    // This entry can never be delivered, and leaving it pending would keep it (and everything read
                    // with it) stuck.
                    _logger.LogError("Acknowledging unreadable entry {EntryId} in stream {QueueId} without delivering it", entry.Id, _queueId);
                    unreadable.Add(entry.Id);
                }
            }

            return batches;
        }

        public virtual Task TrimStreamIfNeeded() => _trimmer.TrimIfDueAsync();

        public async Task Initialize(TimeSpan timeout)
        {
            try
            {
                await EnsureConsumerGroupAsync().WaitAsync(timeout);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing stream {QueueId}", _queueId);
            }
        }

        // Starts at "0" rather than "$" so entries published before the group existed are delivered, not skipped.
        private async Task EnsureConsumerGroupAsync()
        {
            try
            {
                await _database.StreamCreateConsumerGroupAsync(_streamKey, RedisStreamWireFormat.GroupName, "0", createStream: true);
            }
            catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP", StringComparison.Ordinal))
            {
                // The group already exists, which is the normal case on every start after the first.
            }
        }

        private async Task TryRecreateConsumerGroupAsync()
        {
            try
            {
                await EnsureConsumerGroupAsync();
                StartOverOnNewStream();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recreating consumer group for stream {QueueId}", _queueId);
            }
        }

        /// <summary>
        /// Forgets everything about the lost stream. Its ids say nothing about the new one, whose ids can even be lower:
        /// reading past them could skip new entries, and acknowledging them could acknowledge new entries not delivered yet.
        /// </summary>
        private void StartOverOnNewStream()
        {
            _lastReturnedId = "0";
            _catchingUp = true;
            _unacknowledged.Clear();
        }

        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            var acknowledgement = AcknowledgeAsync(messages.OfType<RedisStreamBatchContainer>().Select(m => (RedisValue)m.StreamEntryId).ToList());
            _inFlight = acknowledgement;
            return acknowledgement;
        }

        /// <summary>Acknowledges <paramref name="ids"/> together with every earlier id whose acknowledgement failed.</summary>
        private async Task AcknowledgeAsync(List<RedisValue> ids)
        {
            if (ids.Count == 0)
            {
                return;
            }

            _unacknowledged.AddRange(ids);
            try
            {
                await _database.StreamAcknowledgeAsync(_streamKey, RedisStreamWireFormat.GroupName, [.. _unacknowledged]);
                _unacknowledged.Clear();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error acknowledging messages in stream {QueueId}", _queueId);
            }
        }

        public async Task Shutdown(TimeSpan timeout)
        {
            await _inFlight.WaitAsync(timeout);
            _logger.LogInformation("Shutting down stream {QueueId}", _queueId);
        }
    }
}
