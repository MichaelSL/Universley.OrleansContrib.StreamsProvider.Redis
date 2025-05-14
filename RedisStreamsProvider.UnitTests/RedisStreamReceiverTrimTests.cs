using Microsoft.Extensions.Logging;
using Moq;
using Orleans.Streams;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;
using Microsoft.Extensions.Time.Testing;
using System;
using Microsoft.Extensions.Options; // Added for IOptions

namespace RedisStreamsProvider.UnitTests
{
    public class RedisStreamReceiverTrimTests
    {
        private readonly Mock<IDatabase> _mockDatabase;
        private readonly Mock<ILogger<RedisStreamReceiver>> _mockLogger;
        private readonly QueueId _queueId;
        private RedisStreamReceiver _receiver;
        private FakeTimeProvider _fakeTimeProvider;
        private DateTimeOffset _initialTime;
        private RedisStreamReceiverOptions _receiverOptions; // Added to hold options for tests

        public RedisStreamReceiverTrimTests()
        {
            _mockDatabase = new Mock<IDatabase>();
            _mockLogger = new Mock<ILogger<RedisStreamReceiver>>();
            _queueId = new QueueId();
            _initialTime = new DateTimeOffset(2025, 5, 13, 12, 0, 0, TimeSpan.Zero);
            _fakeTimeProvider = new FakeTimeProvider(_initialTime);
            // Initialize options for tests - these values should match what the tests expect
            _receiverOptions = new RedisStreamReceiverOptions { TrimTimeMinutes = 1, MaxStreamLength = 128 };
            IOptions<RedisStreamReceiverOptions> options = Options.Create(_receiverOptions);
            _receiver = new RedisStreamReceiver(_queueId, _mockDatabase.Object, _mockLogger.Object, _fakeTimeProvider, options);
        }

        [Fact]
        public async Task TrimStreamIfNeeded_ShouldNotTrim_WhenTimeIntervalNotExceeded()
        {
            // Arrange
            _fakeTimeProvider.Advance(TimeSpan.FromMinutes(_receiverOptions.TrimTimeMinutes - 1));

            // Act
            await _receiver.TrimStreamIfNeeded();

            // Assert
            _mockDatabase.Verify(
                db => db.StreamTrimAsync(It.IsAny<RedisKey>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()),
                Moq.Times.Never());
        }

        [Fact]
        public async Task TrimStreamIfNeeded_ShouldTrim_WhenTimeIntervalExceeded()
        {
            // Arrange
            _fakeTimeProvider.Advance(TimeSpan.FromMinutes(_receiverOptions.TrimTimeMinutes + 1));
            _mockDatabase
                .Setup(db => db.StreamTrimAsync(_queueId.ToString(), _receiverOptions.MaxStreamLength, true, It.IsAny<CommandFlags>()))
                .Returns(Task.FromResult<long>(10));

            // Act
            await _receiver.TrimStreamIfNeeded();

            // Assert
            _mockDatabase.Verify(
                db => db.StreamTrimAsync(_queueId.ToString(), _receiverOptions.MaxStreamLength, true, It.IsAny<CommandFlags>()),
                Moq.Times.Once());
        }

        [Fact]
        public async Task TrimStreamIfNeeded_ShouldUpdateLastTrimTime_WhenTrimSucceeds()
        {
            // Arrange
            var timeToTriggerTrim = TimeSpan.FromMinutes(_receiverOptions.TrimTimeMinutes + 1);
            _fakeTimeProvider.Advance(timeToTriggerTrim);

            _mockDatabase
                .Setup(db => db.StreamTrimAsync(_queueId.ToString(), _receiverOptions.MaxStreamLength, true, It.IsAny<CommandFlags>()))
                .Returns(Task.FromResult<long>(10));

            // Act
            await _receiver.TrimStreamIfNeeded();

            // Assert
            // After trimming, _lastTrimTime should be updated to the time of the trim.
            // To verify this, advance time slightly and try to trim again. It should not trim.
            _mockDatabase.Invocations.Clear();
            _fakeTimeProvider.Advance(TimeSpan.FromMinutes(1)); // Advance time slightly
            await _receiver.TrimStreamIfNeeded();

            _mockDatabase.Verify(
                db => db.StreamTrimAsync(_queueId.ToString(), _receiverOptions.MaxStreamLength, true, It.IsAny<CommandFlags>()),
                Moq.Times.Never());
        }

        [Fact]
        public async Task TrimStreamIfNeeded_ShouldLogError_WhenTrimFails()
        {
            // Arrange
            var exception = new RedisException("Test exception");
            _fakeTimeProvider.Advance(TimeSpan.FromMinutes(_receiverOptions.TrimTimeMinutes + 1));
            _mockDatabase
                .Setup(db => db.StreamTrimAsync(_queueId.ToString(), _receiverOptions.MaxStreamLength, true, It.IsAny<CommandFlags>()))
                .Returns(Task.FromException<long>(exception));

            // Act
            await _receiver.TrimStreamIfNeeded();

            // Assert
            _mockLogger.Verify(
                logger => logger.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Error trimming stream")),
                    It.Is<Exception>(ex => ex == exception),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Moq.Times.Once());
        }

        [Fact]
        public async Task GetQueueMessagesAsync_ShouldCallTrimStreamIfNeeded()
        {
            // Arrange
            var fakeTimeProviderForTestReceiver = new FakeTimeProvider(_initialTime);
            // Use the same options for the TestReceiver instance
            IOptions<RedisStreamReceiverOptions> testReceiverOptions = Options.Create(_receiverOptions);
            var testReceiver = new TestReceiver(_queueId, _mockDatabase.Object, _mockLogger.Object, fakeTimeProviderForTestReceiver, testReceiverOptions);
            fakeTimeProviderForTestReceiver.Advance(TimeSpan.FromMinutes(_receiverOptions.TrimTimeMinutes + 1));

            var streamEntries = Array.Empty<StreamEntry>();
            _mockDatabase
                .Setup(db => db.StreamReadGroupAsync(_queueId.ToString(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<int>(), false, It.IsAny<CommandFlags>()))
                .Returns(Task.FromResult(streamEntries));
            _mockDatabase
                .Setup(db => db.StreamTrimAsync(_queueId.ToString(), _receiverOptions.MaxStreamLength, true, It.IsAny<CommandFlags>()))
                .Returns(Task.FromResult<long>(0));

            // Act
            await testReceiver.GetQueueMessagesAsync(10);

            // Assert
            Assert.True(testReceiver.WasTrimCalled, "TrimStreamIfNeeded should have been called");
        }

        private class TestReceiver : RedisStreamReceiver
        {
            public bool WasTrimCalled { get; private set; }

            // Updated constructor to accept IOptions<RedisStreamReceiverOptions>
            public TestReceiver(QueueId queueId, IDatabase database, ILogger<RedisStreamReceiver> logger, TimeProvider timeProvider, IOptions<RedisStreamReceiverOptions> receiverOptions)
                : base(queueId, database, logger, timeProvider, receiverOptions)
            {
                WasTrimCalled = false;
            }

            public override async Task TrimStreamIfNeeded()
            {
                WasTrimCalled = true;
                await base.TrimStreamIfNeeded();
            }
        }
    }
}
