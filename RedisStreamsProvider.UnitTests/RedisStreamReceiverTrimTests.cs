using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Moq.Language.Flow;
using Orleans.Streams;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace RedisStreamsProvider.UnitTests
{
    public class RedisStreamReceiverTrimTests
    {
        private readonly Mock<IDatabase> _mockDatabase = new();
        private readonly Mock<ILogger<RedisStreamReceiver>> _mockLogger = new();
        private readonly QueueId _queueId = new();
        private readonly FakeTimeProvider _fakeTimeProvider = new(new DateTimeOffset(2025, 5, 13, 12, 0, 0, TimeSpan.Zero));
        private readonly RedisStreamReceiverOptions _receiverOptions =
            new() { TrimTimeMinutes = 1, MaxStreamLength = 128, TrimStrategy = RedisStreamTrimStrategy.MaxLength };
        private readonly RedisStreamReceiver _receiver;

        public RedisStreamReceiverTrimTests()
        {
            _receiver = CreateReceiver(_receiverOptions);
        }

        private RedisStreamReceiver CreateReceiver(RedisStreamReceiverOptions options, MinIdTrimSupport? minIdTrimSupport = null) =>
            new(_queueId, _mockDatabase.Object, _mockLogger.Object, _fakeTimeProvider, Options.Create(options), minIdTrimSupport ?? new MinIdTrimSupport());

        private static RedisStreamReceiverOptions AutoOptions() =>
            new() { TrimTimeMinutes = 1, MaxStreamLength = 128, TrimStrategy = RedisStreamTrimStrategy.Auto };

        private void AdvancePastTrimInterval() => _fakeTimeProvider.Advance(TimeSpan.FromMinutes(_receiverOptions.TrimTimeMinutes + 1));

        private ISetup<IDatabase, Task<long>> SetupMinIdTrim() =>
            _mockDatabase.Setup(db => db.StreamTrimByMinIdAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()));

        private void VerifyMinIdTrim(Times times) =>
            _mockDatabase.Verify(db => db.StreamTrimByMinIdAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()),
                times);

        private ISetup<IDatabase, Task<long>> SetupMaxLengthTrim() =>
            _mockDatabase.Setup(db => db.StreamTrimAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()));

        private void VerifyMaxLengthTrim(Times times) =>
            _mockDatabase.Verify(db => db.StreamTrimAsync(
                    _queueId.ToString(), _receiverOptions.MaxStreamLength, true, It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()),
                times);

        [Fact]
        public async Task TrimStreamIfNeeded_ShouldNotTrim_WhenTimeIntervalNotExceeded()
        {
            // Arrange
            _fakeTimeProvider.Advance(TimeSpan.FromMinutes(_receiverOptions.TrimTimeMinutes - 1));

            // Act
            await _receiver.TrimStreamIfNeeded();

            // Assert
            VerifyMaxLengthTrim(Times.Never());
        }

        [Fact]
        public async Task TrimStreamIfNeeded_ShouldTrim_WhenTimeIntervalExceeded()
        {
            // Arrange
            AdvancePastTrimInterval();
            SetupMaxLengthTrim().ReturnsAsync(10);

            // Act
            await _receiver.TrimStreamIfNeeded();

            // Assert
            VerifyMaxLengthTrim(Times.Once());
        }

        [Fact]
        public async Task TrimStreamIfNeeded_ShouldUpdateLastTrimTime_WhenTrimSucceeds()
        {
            // Arrange
            AdvancePastTrimInterval();
            SetupMaxLengthTrim().ReturnsAsync(10);
            await _receiver.TrimStreamIfNeeded();
            _mockDatabase.Invocations.Clear();

            // Act: the next trim comes one minute later, not past the interval since the last one.
            _fakeTimeProvider.Advance(TimeSpan.FromMinutes(1));
            await _receiver.TrimStreamIfNeeded();

            // Assert
            VerifyMaxLengthTrim(Times.Never());
        }

        [Fact]
        public async Task TrimStreamIfNeeded_ShouldLogError_WhenTrimFails()
        {
            // Arrange
            var exception = new RedisException("Test exception");
            AdvancePastTrimInterval();
            SetupMaxLengthTrim().ThrowsAsync(exception);

            // Act
            await _receiver.TrimStreamIfNeeded();

            // Assert
            _mockLogger.VerifyLogged(LogLevel.Error, "Error trimming stream", Times.Once(), ex => ex == exception);
        }

        [Fact]
        public async Task TrimStreamIfNeeded_RetriesAFailedTrimOnlyOncePerInterval()
        {
            // Arrange: every trim fails, e.g. because the server does not support the command.
            AdvancePastTrimInterval();
            SetupMaxLengthTrim().ThrowsAsync(new RedisServerException("ERR syntax error"));
            await _receiver.TrimStreamIfNeeded();
            _mockDatabase.Invocations.Clear();

            // Act: the next poll comes well within the trim interval, the one after that past it.
            _fakeTimeProvider.Advance(TimeSpan.FromSeconds(30));
            await _receiver.TrimStreamIfNeeded();
            var callsWithinInterval = _mockDatabase.Invocations.Count;
            _fakeTimeProvider.Advance(TimeSpan.FromMinutes(_receiverOptions.TrimTimeMinutes));
            await _receiver.TrimStreamIfNeeded();

            // Assert
            Assert.Equal(0, callsWithinInterval);
            VerifyMaxLengthTrim(Times.Once());
        }

        [Fact]
        public async Task TrimStreamIfNeeded_ExplainsTheRedisVersionRequirement_WhenAcknowledgedOnlyTrimIsASyntaxError()
        {
            // Arrange: Redis before 6.2 answers XTRIM ... MINID with a syntax error.
            var receiver = CreateReceiver(new RedisStreamReceiverOptions { TrimTimeMinutes = 1, TrimStrategy = RedisStreamTrimStrategy.AcknowledgedOnly });
            AdvancePastTrimInterval();
            SetupMinIdTrim().ThrowsAsync(new RedisServerException("ERR syntax error"));

            // Act
            await receiver.TrimStreamIfNeeded();

            // Assert: an explicit AcknowledgedOnly never falls back to trimming undelivered entries.
            _mockLogger.VerifyLogged(LogLevel.Error, "Error trimming stream", Times.Once(),
                ex => ex is NotSupportedException { InnerException: RedisServerException }
                      && ex.Message.Contains("Redis 6.2")
                      && ex.Message.Contains("RedisStreamTrimStrategy.MaxLength"));
            _mockDatabase.Verify(db => db.StreamTrimAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()), Times.Never());
        }

        [Fact]
        public async Task TrimStreamIfNeeded_Auto_TrimsAcknowledgedEntries_WhenMinIdIsSupported()
        {
            // Arrange
            var receiver = CreateReceiver(AutoOptions());
            AdvancePastTrimInterval();
            SetupMinIdTrim().ReturnsAsync(10);

            // Act
            await receiver.TrimStreamIfNeeded();

            // Assert
            VerifyMinIdTrim(Times.Once());
            VerifyMaxLengthTrim(Times.Never());
        }

        [Fact]
        public async Task TrimStreamIfNeeded_Auto_FallsBackToMaxLength_WhenMinIdIsASyntaxError()
        {
            // Arrange: Redis before 6.2 answers XTRIM ... MINID with a syntax error.
            var minIdTrimSupport = new MinIdTrimSupport();
            var receiver = CreateReceiver(AutoOptions(), minIdTrimSupport);
            AdvancePastTrimInterval();
            SetupMinIdTrim().ThrowsAsync(new RedisServerException("ERR syntax error"));
            SetupMaxLengthTrim().ReturnsAsync(10);

            // Act
            await receiver.TrimStreamIfNeeded();

            // Assert: the same trim falls back, and the fallback is recorded and logged as a warning, not an error.
            VerifyMaxLengthTrim(Times.Once());
            Assert.False(minIdTrimSupport.IsSupported);
            _mockLogger.VerifyLogged(LogLevel.Warning, "falls back to MaxLength", Times.Once(), ex => ex is NotSupportedException);
            _mockLogger.VerifyLogged(LogLevel.Error, "Error trimming stream", Times.Never());
        }

        [Fact]
        public async Task TrimStreamIfNeeded_Auto_SkipsMinId_OnceAnotherReceiverFoundItUnsupported()
        {
            // Arrange
            var minIdTrimSupport = new MinIdTrimSupport();
            minIdTrimSupport.MarkUnsupported(new Mock<ILogger>().Object);
            var receiver = CreateReceiver(AutoOptions(), minIdTrimSupport);
            AdvancePastTrimInterval();
            SetupMaxLengthTrim().ReturnsAsync(10);

            // Act
            await receiver.TrimStreamIfNeeded();

            // Assert
            VerifyMinIdTrim(Times.Never());
            VerifyMaxLengthTrim(Times.Once());
            _mockLogger.VerifyLogged(LogLevel.Warning, "falls back to MaxLength", Times.Never());
        }

        [Fact]
        public async Task TrimStreamIfNeeded_WarnsWhenTheBacklogExceedsBacklogWarningLength_NotMaxStreamLength()
        {
            // Arrange
            var receiver = CreateReceiver(new RedisStreamReceiverOptions
            {
                TrimTimeMinutes = 1, BacklogWarningLength = 10, MaxStreamLength = 1000, TrimStrategy = RedisStreamTrimStrategy.AcknowledgedOnly
            });
            AdvancePastTrimInterval();
            _mockDatabase.Setup(db => db.StreamLengthAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(11);

            // Act
            await receiver.TrimStreamIfNeeded();

            // Assert
            _mockLogger.VerifyLogged(LogLevel.Warning, "consumers may be falling behind", Times.Once());
        }

        [Fact]
        public async Task GetQueueMessagesAsync_ShouldCallTrimStreamIfNeeded()
        {
            // Arrange
            var testReceiver = new TestReceiver(_queueId, _mockDatabase.Object, _mockLogger.Object, _fakeTimeProvider, Options.Create(_receiverOptions));

            // Act
            await testReceiver.GetQueueMessagesAsync(10);

            // Assert
            Assert.True(testReceiver.WasTrimCalled, "TrimStreamIfNeeded should have been called");
        }

        [Fact]
        public void TrimStrategy_DefaultsToAuto()
        {
            Assert.Equal(RedisStreamTrimStrategy.Auto, new RedisStreamReceiverOptions().TrimStrategy);
        }

        [Fact]
        public void Constructor_Throws_ForAnUnknownTrimStrategy()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateReceiver(new RedisStreamReceiverOptions { TrimStrategy = (RedisStreamTrimStrategy)42 }));
        }

        [Theory]
        [InlineData(null, 0, null, "0")]           // group never delivered anything: trims nothing
        [InlineData("0-0", 0, null, "0-0")]        // group created, nothing delivered yet: trims nothing
        [InlineData("9-0", 0, null, "9-0")]        // everything delivered is acknowledged
        [InlineData("9-0", 2, "5-0", "5-0")]       // oldest unacknowledged entry bounds the trim
        public void GetAcknowledgedTrimId_ReturnsOldestEntryStillNeeded(string? lastDeliveredId, int pendingCount, string? lowestPendingId, string expected)
        {
            var lowest = lowestPendingId is null ? RedisValue.Null : (RedisValue)lowestPendingId;

            var result = RedisStreamTrimmer.GetAcknowledgedTrimId(lastDeliveredId, pendingCount, lowest);

            Assert.Equal(expected, result.ToString());
        }

        private class TestReceiver(
            QueueId queueId, IDatabase database, ILogger<RedisStreamReceiver> logger, TimeProvider timeProvider, IOptions<RedisStreamReceiverOptions> receiverOptions)
            : RedisStreamReceiver(queueId, database, logger, timeProvider, receiverOptions)
        {
            public bool WasTrimCalled { get; private set; }

            public override Task TrimStreamIfNeeded()
            {
                WasTrimCalled = true;
                return base.TrimStreamIfNeeded();
            }
        }
    }
}
