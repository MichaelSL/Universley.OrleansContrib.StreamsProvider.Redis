using Moq;
using StackExchange.Redis;
using Microsoft.Extensions.Logging;
using Orleans.Streams;
using Orleans.Configuration;
using Universley.OrleansContrib.StreamsProvider.Redis;
using Microsoft.Extensions.Options;

namespace RedisStreamsProvider.UnitTests
{
    public class RedisStreamAdapterTests
    {
        private readonly Mock<IDatabase> _mockDatabase;
        private readonly Mock<HashRingBasedStreamQueueMapper> _mockQueueMapper;
        private readonly Mock<ILoggerFactory> _mockLoggerFactory;
        private readonly Mock<ILogger<RedisStreamAdapter>> _mockLogger;
        private readonly Mock<IOptions<RedisStreamReceiverOptions>> _mockReceiverOptions;
        private readonly RedisStreamAdapter _adapter;

        public RedisStreamAdapterTests()
        {
            _mockDatabase = new Mock<IDatabase>();
            var options = new HashRingStreamQueueMapperOptions { TotalQueueCount = 1 };
            _mockQueueMapper = new Mock<HashRingBasedStreamQueueMapper>(options, "queueNamePrefix");
            _mockLoggerFactory = new Mock<ILoggerFactory>();
            _mockLogger = new Mock<ILogger<RedisStreamAdapter>>();
            _mockLoggerFactory.Setup(factory => factory.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);
            _mockReceiverOptions = new Mock<IOptions<RedisStreamReceiverOptions>>();
            _mockReceiverOptions.Setup(o => o.Value).Returns(new RedisStreamReceiverOptions()); // Provide default options
            _adapter = new RedisStreamAdapter(_mockDatabase.Object, "TestProvider", _mockQueueMapper.Object, _mockLoggerFactory.Object, _mockReceiverOptions.Object);
        }

        [Fact]
        public void Constructor_ShouldInitializeProperties()
        {
            Assert.Equal("TestProvider", _adapter.Name);
            Assert.False(_adapter.IsRewindable);
            Assert.Equal(StreamProviderDirection.ReadWrite, _adapter.Direction);
        }

        [Fact]
        public void CreateReceiver_ShouldReturnRedisStreamReceiver()
        {
            var queueId = QueueId.GetQueueId("queueName", 0, 1);
            var receiver = _adapter.CreateReceiver(queueId);

            Assert.NotNull(receiver);
            Assert.IsType<RedisStreamReceiver>(receiver);
        }

        [Fact]
        public async Task QueueMessageBatchAsync_AddsAllEventsToTheQueuesStreamInOneTransaction()
        {
            // Arrange
            var streamId = StreamId.Create("namespace", "key");
            var streamKey = RedisStreamWireFormat.StreamKey(_mockQueueMapper.Object.GetQueueForStream(streamId));
            var transaction = new Mock<ITransaction>();
            transaction.Setup(t => t.ExecuteAsync(It.IsAny<CommandFlags>())).ReturnsAsync(true);
            _mockDatabase.Setup(db => db.CreateTransaction(It.IsAny<object>())).Returns(transaction.Object);

            // Act
            await _adapter.QueueMessageBatchAsync(streamId, new List<string> { "event1", "event2" }, null!, []);

            // Assert
            transaction.Verify(t => t.StreamAddAsync(streamKey, It.IsAny<NameValueEntry[]>(), It.IsAny<RedisValue?>(), It.IsAny<long?>(),
                It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()), Times.Exactly(2));
            transaction.Verify(t => t.ExecuteAsync(It.IsAny<CommandFlags>()), Times.Once);
            _mockDatabase.Verify(db => db.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<NameValueEntry[]>(), It.IsAny<RedisValue?>(), It.IsAny<long?>(),
                It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()), Times.Never);
        }

        [Fact]
        public async Task QueueMessageBatchAsync_ShouldLogAndRethrow_OnException()
        {
            // Arrange
            var streamId = StreamId.Create("namespace", "key");
            var transaction = new Mock<ITransaction>();
            transaction.Setup(t => t.ExecuteAsync(It.IsAny<CommandFlags>())).ThrowsAsync(new Exception("Test exception"));
            _mockDatabase.Setup(db => db.CreateTransaction(It.IsAny<object>())).Returns(transaction.Object);

            // Act
            var thrown = await Assert.ThrowsAsync<Exception>(() => _adapter.QueueMessageBatchAsync(streamId, new List<string> { "event1", "event2" }, null!, []));

            // Assert
            Assert.Equal("Test exception", thrown.Message);
            _mockLogger.VerifyLogged(LogLevel.Error, "Error adding event to stream", Times.Once());
        }

        [Fact]
        public async Task QueueMessageBatchAsync_Throws_WhenOneOfTheQueuedAddsFails()
        {
            // Arrange: EXEC succeeds, but Redis rejected one command inside it.
            var transaction = new Mock<ITransaction>();
            transaction.Setup(t => t.ExecuteAsync(It.IsAny<CommandFlags>())).ReturnsAsync(true);
            transaction.Setup(t => t.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<NameValueEntry[]>(), It.IsAny<RedisValue?>(), It.IsAny<long?>(),
                    It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()))
                .ThrowsAsync(new RedisServerException("WRONGTYPE Operation against a key holding the wrong kind of value"));
            _mockDatabase.Setup(db => db.CreateTransaction(It.IsAny<object>())).Returns(transaction.Object);

            // Act & Assert
            await Assert.ThrowsAsync<RedisServerException>(() => _adapter.QueueMessageBatchAsync(StreamId.Create("namespace", "key"), new List<string> { "event1" }, null!, []));
        }
    }
}
