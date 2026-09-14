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
        private readonly RedisStreamReceiverOptions _receiverOptions;
        private readonly MinIdTrimSupport _minIdTrimSupport;
        private RedisStreamTrimmer _trimmer;
        // The newest entry handed to Orleans (or acknowledged as unreadable). Reads return ids in ascending order and
        // each read starts past the previous one, so every entry this consumer has read and not handed on is newer.
        private RedisValue _lastReturnedId = "0";
        // While catching up, reads walk this consumer's pending list from _lastReturnedId instead of asking for new
        // messages: on start, to redeliver what a previous owner never acknowledged, and after a failed read.
        private bool _catchingUp = true;
        // Entries whose XACK failed. They stay pending in Redis, so they are sent again with the next acknowledgement
        // (XACK is idempotent); otherwise nothing would acknowledge them before the next restart or queue handoff.
        private readonly List<RedisValue> _unacknowledged = [];
        private Task? pendingTasks;

        public RedisStreamReceiver(QueueId queueId,
                                 IDatabase database,
                                 ILogger<RedisStreamReceiver> logger,
                                 TimeProvider? timeProvider = null,
                                 IOptions<RedisStreamReceiverOptions>? receiverOptions = null)
            : this(queueId, database, logger, timeProvider, receiverOptions, new MinIdTrimSupport())
        {
        }

        internal RedisStreamReceiver(QueueId queueId,
                                   IDatabase database,
                                   ILogger<RedisStreamReceiver> logger,
                                   TimeProvider? timeProvider,
                                   IOptions<RedisStreamReceiverOptions>? receiverOptions,
                                   MinIdTrimSupport minIdTrimSupport)
        {
            _queueId = queueId;
            _streamKey = RedisStreamWireFormat.StreamKey(queueId);
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _receiverOptions = receiverOptions?.Value ?? new RedisStreamReceiverOptions();
            _minIdTrimSupport = minIdTrimSupport;
            _trimmer = CreateTrimmer(timeProvider ?? TimeProvider.System);
        }

        /// <summary>Replaces the time provider; the next trim is due one full interval from now.</summary>
        public void SetTimeProvider(TimeProvider timeProvider)
        {
            _trimmer = CreateTrimmer(timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
        }

        private RedisStreamTrimmer CreateTrimmer(TimeProvider timeProvider) =>
            new(_queueId, _database, _logger, timeProvider, _receiverOptions, _minIdTrimSupport);

        public async Task<IList<IBatchContainer>?> GetQueueMessagesAsync(int maxCount)
        {
            try
            {
                var entries = await ReadEntriesAsync(maxCount is > 0 and < MaxReadCount ? maxCount : MaxReadCount);
                var batches = ToBatches(entries, out var unreadable);
                await AcknowledgeAsync(unreadable);
                await TrimStreamIfNeeded();

                if (entries.Length > 0)
                {
                    _lastReturnedId = entries[^1].Id;
                }

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

        private async Task<StreamEntry[]> ReadEntriesAsync(int count)
        {
            if (_catchingUp)
            {
                var pending = await TrackAsync(_database.StreamReadGroupAsync(_streamKey, RedisStreamWireFormat.GroupName, _streamKey, _lastReturnedId, count));
                if (pending.Length > 0)
                {
                    return pending;
                }

                _catchingUp = false;
            }

            return await TrackAsync(_database.StreamReadGroupAsync(_streamKey, RedisStreamWireFormat.GroupName, _streamKey, NewMessages, count));
        }

        private List<IBatchContainer> ToBatches(StreamEntry[] entries, out List<RedisValue> unreadable)
        {
            var batches = new List<IBatchContainer>(entries.Length);
            unreadable = [];
            foreach (var entry in entries)
            {
                try
                {
                    batches.Add(new RedisStreamBatchContainer(entry));
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
                {
                    // Entries deleted while still pending come back with no fields. Either way this entry can never
                    // be delivered, and leaving it pending would keep it (and everything read with it) stuck.
                    _logger.LogError(ex, "Acknowledging unreadable entry {EntryId} in stream {QueueId} without delivering it", entry.Id, _queueId);
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
                // Ids from the lost stream say nothing about the new one, whose ids can even be lower.
                _lastReturnedId = "0";
                _catchingUp = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recreating consumer group for stream {QueueId}", _queueId);
            }
        }

        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages) =>
            AcknowledgeAsync(messages.OfType<RedisStreamBatchContainer>().Select(m => (RedisValue)m.StreamEntryId).ToList());

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
                await TrackAsync(_database.StreamAcknowledgeAsync(_streamKey, RedisStreamWireFormat.GroupName, [.. _unacknowledged]));
                _unacknowledged.Clear();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error acknowledging messages in stream {QueueId}", _queueId);
            }
        }

        // Shutdown waits for the Redis call in flight.
        private async Task<T> TrackAsync<T>(Task<T> call)
        {
            pendingTasks = call;
            try
            {
                return await call;
            }
            finally
            {
                pendingTasks = null;
            }
        }

        public async Task Shutdown(TimeSpan timeout)
        {
            using (var cts = new CancellationTokenSource(timeout))
            {

                if (pendingTasks is not null)
                {
                    await pendingTasks.WaitAsync(timeout, cts.Token);
                }
            }
            _logger.LogInformation("Shutting down stream {QueueId}", _queueId);
        }
    }
}
