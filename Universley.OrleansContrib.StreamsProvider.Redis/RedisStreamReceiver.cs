using Microsoft.Extensions.Logging;
using Orleans.Streams;
using StackExchange.Redis;
using System; // Added for TimeProvider
using Microsoft.Extensions.Options; // Added for IOptions

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    public class RedisStreamReceiver : IQueueAdapterReceiver
    {
        // One group per stream, and the queue id as consumer name. Orleans gives each queue to one silo at a time,
        // so whichever silo owns the queue reads as the same consumer and picks up entries a previous owner read but
        // never acknowledged. Both values are part of the wire format; do not change them.
        private const string GroupName = "consumer";
        private string ConsumerName => _streamKey;

        private const string NewMessages = ">";
        private const int MaxReadCount = 1000;

        private readonly QueueId _queueId;
        // The stream key (and consumer name): the queue id's string form, computed once.
        private readonly string _streamKey;
        private readonly IDatabase _database;
        private readonly ILogger<RedisStreamReceiver> _logger;
        // Until the pending list is drained, reads walk it from this cursor; afterwards they ask for new messages.
        private RedisValue _pendingCursor = "0";
        private bool _drainingPending = true;
        private Task? pendingTasks;
        private DateTimeOffset _lastTrimTime;

        private TimeProvider _timeProvider;
        private readonly RedisStreamReceiverOptions _receiverOptions; // Added options field

        // Changed: Constructor to accept TimeProvider and IOptions<RedisStreamReceiverOptions>
        public RedisStreamReceiver(QueueId queueId, 
                                 IDatabase database, 
                                 ILogger<RedisStreamReceiver> logger, 
                                 TimeProvider? timeProvider = null, 
                                 IOptions<RedisStreamReceiverOptions>? receiverOptions = null)
        {
            _queueId = queueId;
            _streamKey = queueId.ToString();
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _timeProvider = timeProvider ?? TimeProvider.System;
            _receiverOptions = receiverOptions?.Value ?? new RedisStreamReceiverOptions(); // Use provided options or default
            _lastTrimTime = _timeProvider.GetUtcNow(); 
        }

        // This method might be less relevant if options are passed via constructor, 
        // but kept for now if direct TimeProvider manipulation is still needed for some tests.
        public void SetTimeProvider(TimeProvider timeProvider)
        {
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
            _lastTrimTime = _timeProvider.GetUtcNow();
        }

        public async Task<IList<IBatchContainer>?> GetQueueMessagesAsync(int maxCount)
        {
            try
            {
                var entries = await ReadEntriesAsync(maxCount);
                var batches = await ToBatchesAsync(entries);
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
                return default;
            }
            finally
            {
                pendingTasks = null;
            }
        }

        private async Task<StreamEntry[]> ReadEntriesAsync(int maxCount)
        {
            var count = maxCount is > 0 and < MaxReadCount ? maxCount : MaxReadCount;
            if (_drainingPending)
            {
                var pending = await ReadGroupAsync(_pendingCursor, count);
                if (pending.Length > 0)
                {
                    _pendingCursor = pending[^1].Id;
                    return pending;
                }

                _drainingPending = false;
            }

            return await ReadGroupAsync(NewMessages, count);
        }

        private async Task<StreamEntry[]> ReadGroupAsync(RedisValue position, int count)
        {
            var read = _database.StreamReadGroupAsync(_streamKey, GroupName, ConsumerName, position, count);
            pendingTasks = read;
            return await read;
        }

        private async Task<List<IBatchContainer>> ToBatchesAsync(StreamEntry[] entries)
        {
            var batches = new List<IBatchContainer>(entries.Length);
            List<RedisValue>? unreadable = null;
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
                    (unreadable ??= []).Add(entry.Id);
                }
            }

            if (unreadable is not null)
            {
                try
                {
                    await _database.StreamAcknowledgeAsync(_streamKey, GroupName, [.. unreadable]);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error acknowledging unreadable entries in stream {QueueId}", _queueId);
                    // Left pending, they are re-read and skipped again the next time the pending list is drained (restart or queue handoff).
                }
            }

            return batches;
        }

        public virtual async Task TrimStreamIfNeeded()
        {
            var now = _timeProvider.GetUtcNow();
            if (now - _lastTrimTime > TimeSpan.FromMinutes(_receiverOptions.TrimTimeMinutes))
            {
                // Recorded before the attempt, so a trim that keeps failing (e.g. a command the server does not support)
                // is retried once per interval rather than on every poll.
                _lastTrimTime = now;
                try
                {
                    var trimmed = _receiverOptions.TrimStrategy == RedisStreamTrimStrategy.MaxLength
                        ? await _database.StreamTrimAsync(_streamKey, _receiverOptions.MaxStreamLength, useApproximateMaxLength: true)
                        : await TrimAcknowledgedEntriesAsync();
                    _logger.LogDebug("Trimmed {Count} entries from stream {QueueId} using {TrimStrategy} at {Time}", trimmed, _queueId, _receiverOptions.TrimStrategy, _lastTrimTime);
                }
                catch (RedisServerException ex) when (_receiverOptions.TrimStrategy == RedisStreamTrimStrategy.AcknowledgedOnly
                                                      && ex.Message.Contains("syntax", StringComparison.OrdinalIgnoreCase))
                {
                    // Redis before 6.2 does not know XTRIM ... MINID and answers with a syntax error.
                    _logger.LogError(ex,
                        "Error trimming stream {QueueId}: the AcknowledgedOnly trim strategy needs Redis 6.2 or later. On older servers set RedisStreamReceiverOptions.TrimStrategy = RedisStreamTrimStrategy.MaxLength",
                        _queueId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error trimming stream {QueueId}", _queueId);
                }
            }
        }

        private async Task<long> TrimAcknowledgedEntriesAsync()
        {
            string? lastDeliveredId = null;
            foreach (var group in await _database.StreamGroupInfoAsync(_streamKey))
            {
                if (group.Name == GroupName)
                {
                    lastDeliveredId = group.LastDeliveredId;
                }
            }

            var pending = await _database.StreamPendingAsync(_streamKey, GroupName);
            var minId = GetAcknowledgedTrimId(lastDeliveredId, pending.PendingMessageCount, pending.LowestPendingMessageId);
            var trimmed = minId is { } id
                ? await _database.StreamTrimByMinIdAsync(_streamKey, id, useApproximateMaxLength: true)
                : 0;

            var remaining = await _database.StreamLengthAsync(_streamKey);
            if (remaining > _receiverOptions.MaxStreamLength)
            {
                _logger.LogWarning(
                    "Stream {QueueId} still holds {Remaining} entries after trimming, more than MaxStreamLength {MaxStreamLength}; consumers may be falling behind",
                    _queueId, remaining, _receiverOptions.MaxStreamLength);
            }

            return trimmed;
        }

        /// <summary>
        /// Returns the id below which every entry has been delivered and acknowledged, or null when nothing is safe to trim.
        /// Entries up to the group's last-delivered id were delivered; those not in the pending list were acknowledged.
        /// </summary>
        internal static RedisValue? GetAcknowledgedTrimId(string? lastDeliveredId, long pendingCount, RedisValue lowestPendingId)
        {
            if (pendingCount > 0)
            {
                return lowestPendingId;
            }

            if (string.IsNullOrEmpty(lastDeliveredId) || lastDeliveredId == "0-0")
            {
                return null;
            }

            return lastDeliveredId;
        }

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
                await _database.StreamCreateConsumerGroupAsync(_streamKey, GroupName, "0", createStream: true);
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
                _pendingCursor = "0";
                _drainingPending = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recreating consumer group for stream {QueueId}", _queueId);
            }
        }

        public async Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            var ids = messages.OfType<RedisStreamBatchContainer>().Select(m => (RedisValue)m.StreamEntryId).ToArray();
            if (ids.Length == 0)
            {
                return;
            }

            try
            {
                var ack = _database.StreamAcknowledgeAsync(_streamKey, GroupName, ids);
                pendingTasks = ack;
                await ack;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error acknowledging messages in stream {QueueId}", _queueId);
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
