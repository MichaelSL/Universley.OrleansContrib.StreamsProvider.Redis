using Microsoft.Extensions.Logging;
using Orleans.Streams;
using StackExchange.Redis;

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    /// <summary>Removes old entries from one queue's stream, at most once per trim interval.</summary>
    internal sealed class RedisStreamTrimmer
    {
        private readonly QueueId _queueId;
        private readonly string _streamKey;
        private readonly IDatabase _database;
        private readonly ILogger _logger;
        private readonly TimeProvider _timeProvider;
        private readonly RedisStreamReceiverOptions _options;
        private readonly Func<Task<long>> _trim;
        private DateTimeOffset _lastTrimTime;

        public RedisStreamTrimmer(QueueId queueId, IDatabase database, ILogger logger, TimeProvider timeProvider, RedisStreamReceiverOptions options)
        {
            _queueId = queueId;
            _streamKey = RedisStreamWireFormat.StreamKey(queueId);
            _database = database;
            _logger = logger;
            _timeProvider = timeProvider;
            _options = options;
            _trim = options.TrimStrategy switch
            {
                RedisStreamTrimStrategy.AcknowledgedOnly => TrimAcknowledgedEntriesAsync,
                RedisStreamTrimStrategy.MaxLength => TrimToMaxLengthAsync,
                _ => throw new ArgumentOutOfRangeException(nameof(options), options.TrimStrategy, "Unknown trim strategy."),
            };
            _lastTrimTime = timeProvider.GetUtcNow();
        }

        public async Task TrimIfDueAsync()
        {
            var now = _timeProvider.GetUtcNow();
            if (now - _lastTrimTime <= TimeSpan.FromMinutes(_options.TrimTimeMinutes))
            {
                return;
            }

            // Recorded before the attempt, so a trim that keeps failing (e.g. a command the server does not support)
            // is retried once per interval rather than on every poll.
            _lastTrimTime = now;
            try
            {
                var trimmed = await _trim();
                _logger.LogDebug("Trimmed {Count} entries from stream {QueueId} using {TrimStrategy} at {Time}", trimmed, _queueId, _options.TrimStrategy, now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error trimming stream {QueueId}", _queueId);
            }
        }

        private Task<long> TrimToMaxLengthAsync() =>
            _database.StreamTrimAsync(_streamKey, _options.MaxStreamLength, useApproximateMaxLength: true);

        private async Task<long> TrimAcknowledgedEntriesAsync()
        {
            var groups = await _database.StreamGroupInfoAsync(_streamKey);
            var pending = await _database.StreamPendingAsync(_streamKey, RedisStreamWireFormat.GroupName);
            var minId = GetAcknowledgedTrimId(
                groups.FirstOrDefault(g => g.Name == RedisStreamWireFormat.GroupName).LastDeliveredId,
                pending.PendingMessageCount,
                pending.LowestPendingMessageId);

            long trimmed;
            try
            {
                // Without LIMIT, approximate trimming stops after 100 * stream-node-max-entries (10,000 by default) entries.
                trimmed = await _database.StreamTrimByMinIdAsync(_streamKey, minId, useApproximateMaxLength: true, limit: long.MaxValue);
            }
            catch (RedisServerException ex) when (ex.Message.Contains("syntax", StringComparison.OrdinalIgnoreCase))
            {
                // Redis before 6.2 knows neither MINID nor LIMIT and answers with a syntax error.
                throw new NotSupportedException(
                    "The AcknowledgedOnly trim strategy needs Redis 6.2 or later. On older servers set RedisStreamReceiverOptions.TrimStrategy = RedisStreamTrimStrategy.MaxLength",
                    ex);
            }

            var remaining = await _database.StreamLengthAsync(_streamKey);
            if (remaining > _options.BacklogWarningLength)
            {
                _logger.LogWarning(
                    "Stream {QueueId} still holds {Remaining} entries after trimming, more than BacklogWarningLength {BacklogWarningLength}; consumers may be falling behind",
                    _queueId, remaining, _options.BacklogWarningLength);
            }

            return trimmed;
        }

        /// <summary>
        /// Returns the id below which every entry has been delivered and acknowledged. Entries up to the group's
        /// last-delivered id were delivered; those not in the pending list were acknowledged. When nothing was delivered
        /// yet this is "0", below which there are no entries, so trimming to it removes nothing.
        /// </summary>
        internal static RedisValue GetAcknowledgedTrimId(string? lastDeliveredId, long pendingCount, RedisValue lowestPendingId) =>
            pendingCount > 0 ? lowestPendingId : lastDeliveredId ?? "0";
    }
}
