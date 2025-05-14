using Microsoft.Extensions.Logging;
using Orleans.Streams;
using StackExchange.Redis;
using System; // Added for TimeProvider
using Microsoft.Extensions.Options; // Added for IOptions

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
            // Changed: Use _timeProvider.GetUtcNow() and options for trim parameters
            if (_timeProvider.GetUtcNow() - _lastTrimTime > TimeSpan.FromMinutes(_receiverOptions.TrimTimeMinutes))
            {
                try
                {
                    var trim = await _database.StreamTrimAsync(_queueId.ToString(), _receiverOptions.MaxStreamLength, useApproximateMaxLength: true);
                    _lastTrimTime = _timeProvider.GetUtcNow();
                    _logger.LogDebug("Trimmed stream {QueueId} to {MaxStreamLength} entries at {Time}", _queueId, _receiverOptions.MaxStreamLength, _lastTrimTime);
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
