using Microsoft.Extensions.Logging;
using Orleans.Streams;
using StackExchange.Redis;
using System; // Added for TimeProvider

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    public class RedisStreamReceiver : IQueueAdapterReceiver
    {
        private readonly QueueId _queueId;
        private readonly IDatabase _database;
        private readonly ILogger<RedisStreamReceiver> _logger;
        private string _lastId = "0";
        private Task? pendingTasks;
        private DateTimeOffset _lastTrimTime;
        public const int MaxStreamLength = 128;
        public const int TrimTimeMinutes = 1;

        private TimeProvider _timeProvider;

        public RedisStreamReceiver(QueueId queueId, IDatabase database, ILogger<RedisStreamReceiver> logger, TimeProvider? timeProvider = null)
        {
            _queueId = queueId;
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _timeProvider = timeProvider ?? TimeProvider.System; // Use provided TimeProvider or default
            _lastTrimTime = _timeProvider.GetUtcNow(); 
        }

        public void SetTimeProvider(TimeProvider timeProvider)
        {
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
            _lastTrimTime = _timeProvider.GetUtcNow();
        }

        public async Task<IList<IBatchContainer>?> GetQueueMessagesAsync(int maxCount)
        {
            try
            {
                var events = _database.StreamReadGroupAsync(_queueId.ToString(), "consumer", _queueId.ToString(), _lastId, maxCount);
                pendingTasks = events;
                _lastId = ">";
                var batches = (await events).Select(e => new RedisStreamBatchContainer(e)).ToList<IBatchContainer>();
                await TrimStreamIfNeeded();

                return batches;
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

        public virtual async Task TrimStreamIfNeeded()
        {
            if (_timeProvider.GetUtcNow() - _lastTrimTime > TimeSpan.FromMinutes(TrimTimeMinutes))
            {
                try
                {
                    var trim = await _database.StreamTrimAsync(_queueId.ToString(), MaxStreamLength, useApproximateMaxLength: true);
                    _lastTrimTime = _timeProvider.GetUtcNow();
                    _logger.LogDebug("Trimmed stream {QueueId} to {MaxStreamLength} entries at {Time}", _queueId, MaxStreamLength, _lastTrimTime);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error trimming stream {QueueId}", _queueId);
                }
            }
        }

        public async Task Initialize(TimeSpan timeout)
        {
            try
            {
                using (var cts = new CancellationTokenSource(timeout))
                {
                    var task = _database.StreamCreateConsumerGroupAsync(_queueId.ToString(), "consumer", "$", true);
                    await task.WaitAsync(timeout, cts.Token);
                }
            }
            catch (Exception ex) when (ex.Message.Contains("name already exists")) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing stream {QueueId}", _queueId);
            }
        }

        public async Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            try
            {
                foreach (var message in messages)
                {
                    var container = message as RedisStreamBatchContainer;
                    if (container != null)
                    {
                        var ack = _database.StreamAcknowledgeAsync(_queueId.ToString(), "consumer", container.StreamEntryId);
                        pendingTasks = ack;
                        await ack;
                    }
                }
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
