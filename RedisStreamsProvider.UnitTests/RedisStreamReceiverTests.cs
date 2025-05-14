using Microsoft.Extensions.Logging;
using Moq;
using Orleans.Streams;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace RedisStreamsProvider.UnitTests
{
    public class RedisStreamReceiverTests
    {
        private readonly Mock<IDatabase> _mockDatabase;
        private readonly Mock<ILogger<RedisStreamReceiver>> _mockLogger;
        private readonly QueueId _queueId;

        public RedisStreamReceiverTests()
        {
            _mockDatabase = new Mock<IDatabase>();
            _mockLogger = new Mock<ILogger<RedisStreamReceiver>>();
            _queueId = QueueId.GetQueueId("testQueue", 0, 0); // Added the missing 'hash' parameter
        }

        [Fact]
        public async Task GetQueueMessagesAsync_ReturnsBatches()
        {
            // Arrange
            var streamEntries = new[]
            {
                new StreamEntry("1-0", [
                    new("namespace", "testNamespace"),
                    new("key", "testKey"),
                    new("eventType", "testEventType" ),
                    new( "data", "testData" )
                ]),
                new StreamEntry("2-0", [
                    new("namespace", "testNamespace"),
                    new("key", "testKey"),
                    new("eventType", "testEventType" ),
                    new( "data", "testData" )
                ])
            };
            _mockDatabase.Setup(db => db.StreamReadGroupAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(),
                    It.IsAny<int?>(), It.IsAny<bool>(), CommandFlags.None))
                .ReturnsAsync(streamEntries);

            var receiver = new RedisStreamReceiver(_queueId, _mockDatabase.Object, _mockLogger.Object);

            // Act
            var result = await receiver.GetQueueMessagesAsync(10);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
        }

        [Fact]
        public async Task GetQueueMessagesAsync_ShouldReturnNull_OnException()
        {
            // Arrange
            _mockDatabase.Setup(db => db.StreamReadGroupAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(),
                    It.IsAny<int?>(), It.IsAny<bool>(), CommandFlags.None))
                .ThrowsAsync(new Exception("Test exception"));
            var receiver = new RedisStreamReceiver(_queueId, _mockDatabase.Object, _mockLogger.Object);

            // Act
            var result = await receiver.GetQueueMessagesAsync(10);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task Initialize_CreatesConsumerGroup()
        {
            // Arrange
            _mockDatabase.Setup(db => db.StreamCreateConsumerGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                    It.IsAny<RedisValue>(), It.IsAny<bool>(), CommandFlags.None))
                .ReturnsAsync(true);
            var receiver = new RedisStreamReceiver(_queueId, _mockDatabase.Object, _mockLogger.Object);

            // Act
            await receiver.Initialize(TimeSpan.FromSeconds(5));

            // Assert
            _mockDatabase.Verify(
                db => db.StreamCreateConsumerGroupAsync(_queueId.ToString(), "consumer", "$", true, CommandFlags.None),
                Times.Once);
        }

        [Fact]
        public async Task Initialize_ShouldLogError_OnException()
        {
            // Arrange
            var mockDatabase = new Mock<IDatabase>();
            var mockLoggerFactory = new Mock<ILoggerFactory>();
            var mockLogger = new Mock<ILogger<RedisStreamReceiver>>();
            mockLoggerFactory.Setup(factory => factory.CreateLogger(It.IsAny<string>())).Returns(mockLogger.Object);
            var receiver = new RedisStreamReceiver(_queueId, mockDatabase.Object, mockLogger.Object);
            mockDatabase.Setup(db => db.StreamCreateConsumerGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                    It.IsAny<RedisValue>(), It.IsAny<bool>(), CommandFlags.None))
                .ThrowsAsync(new Exception("Test exception"));

            // Act
            await receiver.Initialize(TimeSpan.FromSeconds(5));

            // Assert
            mockLogger.Verify(
                logger => logger.Log(
                    It.Is<LogLevel>(logLevel => logLevel == LogLevel.Error),
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v != null && v.ToString()!.Contains("Error initializing stream")),
                    It.IsAny<Exception>(),
                    It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
                Times.Once);
        }

        [Fact]
        public async Task MessagesDeliveredAsync_AcknowledgesMessages()
        {
            // Arrange
            var messages = new List<IBatchContainer>
            {
                new RedisStreamBatchContainer(new StreamEntry("1-0", [
                    new("namespace", "testNamespace"),
                    new("key", "testKey"),
                    new("eventType", "testEventType" ),
                    new( "data", "testData" )
                ])),
                new RedisStreamBatchContainer(new StreamEntry("2-0", [
                    new("namespace", "testNamespace"),
                    new("key", "testKey"),
                    new("eventType", "testEventType" ),
                    new( "data", "testData" )
                ]))
            };
            _mockDatabase.Setup(db => db.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                    It.IsAny<RedisValue>(), CommandFlags.None))
                .ReturnsAsync(2);

            var receiver = new RedisStreamReceiver(_queueId, _mockDatabase.Object, _mockLogger.Object);

            // Act
            await receiver.MessagesDeliveredAsync(messages);

            // Assert
            _mockDatabase.Verify(
                db => db.StreamAcknowledgeAsync(_queueId.ToString(), "consumer", "1-0", CommandFlags.None), Times.Once);
            _mockDatabase.Verify(
                db => db.StreamAcknowledgeAsync(_queueId.ToString(), "consumer", "2-0", CommandFlags.None), Times.Once);
        }

        [Fact]
        public async Task MessagesDeliveredAsync_ShouldLogError_OnException()
        {
            // Arrange
            var messages = new List<IBatchContainer>
            {
                new RedisStreamBatchContainer(new StreamEntry("1-0", [
                    new("namespace", "testNamespace"),
                    new("key", "testKey"),
                    new("eventType", "testEventType" ),
                    new( "data", "testData" )
                ]))
            };
            var mockDatabase = new Mock<IDatabase>();
            var mockLoggerFactory = new Mock<ILoggerFactory>();
            var mockLogger = new Mock<ILogger<RedisStreamReceiver>>();
            mockLoggerFactory.Setup(factory => factory.CreateLogger(It.IsAny<string>())).Returns(mockLogger.Object);
            var receiver = new RedisStreamReceiver(_queueId, mockDatabase.Object, mockLogger.Object);
            mockDatabase.Setup(db => db.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                    It.IsAny<RedisValue>(), CommandFlags.None))
                .ThrowsAsync(new Exception("Test exception"));

            // Act
            await receiver.MessagesDeliveredAsync(messages);

            // Assert
            mockLogger.Verify(
                logger => logger.Log(
                    It.Is<LogLevel>(logLevel => logLevel == LogLevel.Error),
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v != null && v.ToString()!.Contains("Error acknowledging messages in stream")),
                    It.IsAny<Exception>(),
                    It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
                Times.Once);
        }

        [Fact]
        public async Task Shutdown_WaitsForPendingTasks()
        {
            // Arrange
            var tcs = new TaskCompletionSource<StreamEntry[]>();
            _mockDatabase.Setup(db => db.StreamReadGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                    It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<int>(), CommandFlags.None))
                .Returns(tcs.Task);
            var receiver = new RedisStreamReceiver(_queueId, _mockDatabase.Object, _mockLogger.Object);

            // Act
            var getMessagesTask = receiver.GetQueueMessagesAsync(10);
            await receiver.Shutdown(TimeSpan.FromSeconds(5));

            // Assert
            Assert.True(getMessagesTask.IsCompleted);
        }
    }
}