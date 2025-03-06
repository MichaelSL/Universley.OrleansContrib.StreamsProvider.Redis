using Moq;
using StackExchange.Redis;
using Microsoft.Extensions.Logging;
using Orleans.Streams;
using Orleans.Configuration;
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace RedisStreamsProvider.UnitTests
{
    public class RedisStreamAdapterTests
    {
        private readonly Mock<IDatabase> _mockDatabase;
        private readonly Mock<HashRingBasedStreamQueueMapper> _mockQueueMapper;
        private readonly Mock<ILoggerFactory> _mockLoggerFactory;
        private readonly Mock<ILogger<RedisStreamAdapter>> _mockLogger;
        private readonly RedisStreamAdapter _adapter;

        public RedisStreamAdapterTests()
        {
            _mockDatabase = new Mock<IDatabase>();
            var options = new HashRingStreamQueueMapperOptions { TotalQueueCount = 1 };
            _mockQueueMapper = new Mock<HashRingBasedStreamQueueMapper>(options, "queueNamePrefix");
            _mockLoggerFactory = new Mock<ILoggerFactory>();
            _mockLogger = new Mock<ILogger<RedisStreamAdapter>>();
            _mockLoggerFactory.Setup(factory => factory.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);
            _adapter = new RedisStreamAdapter(_mockDatabase.Object, "TestProvider", _mockQueueMapper.Object, _mockLoggerFactory.Object);
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
        public async Task QueueMessageBatchAsync_ShouldLogError_OnException()
        {
            // Arrange
            var streamId = StreamId.Create("namespace", "key");
            var events = new List<string> { "event1", "event2" };
            var token = new RedisStreamSequenceToken(123, 456);
            var requestContext = new Dictionary<string, object>();
            var mockDatabase = new Mock<IDatabase>();
            var mockLoggerFactory = new Mock<ILoggerFactory>();
            var mockLogger = new Mock<ILogger<RedisStreamAdapter>>();
            mockLoggerFactory.Setup(factory => factory.CreateLogger(It.IsAny<string>())).Returns(mockLogger.Object);
            var adapter = new RedisStreamAdapter(mockDatabase.Object, "TestProvider", _mockQueueMapper.Object, mockLoggerFactory.Object);
            mockDatabase.Setup(db => db.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<NameValueEntry[]>(), It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(), CommandFlags.None))
                .ThrowsAsync(new Exception("Test exception"));

            // Act
            await adapter.QueueMessageBatchAsync(streamId, events, token, requestContext);

            // Assert
            mockLogger.Verify(
                logger => logger.Log(
                    It.Is<LogLevel>(logLevel => logLevel == LogLevel.Error),
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Error adding event to stream")),
                    It.IsAny<Exception>(),
                    It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)),
                Times.Once);
        }
    }
}
