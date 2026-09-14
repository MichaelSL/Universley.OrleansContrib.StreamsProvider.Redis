using Microsoft.Extensions.Logging;
using Moq;
using Moq.Language.Flow;
using Orleans.Streams;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace RedisStreamsProvider.UnitTests
{
    public class RedisStreamReceiverTests
    {
        private readonly Mock<IDatabase> _mockDatabase = new();
        private readonly Mock<ILogger<RedisStreamReceiver>> _mockLogger = new();
        private readonly QueueId _queueId = QueueId.GetQueueId("testQueue", 0, 0);

        private RedisStreamReceiver CreateReceiver() => new(_queueId, _mockDatabase.Object, _mockLogger.Object);

        private static StreamEntry Entry(string id) => new(id, [
            new(RedisStreamWireFormat.StreamNamespaceField, "testNamespace"),
            new(RedisStreamWireFormat.StreamKeyField, "testKey"),
            new(RedisStreamWireFormat.EventTypeField, "testEventType"),
            new(RedisStreamWireFormat.DataField, "testData")
        ]);

        // No data field, like an entry deleted while it was still pending.
        private static StreamEntry Unreadable(string id) => new(id, [
            new(RedisStreamWireFormat.StreamNamespaceField, "testNamespace"),
            new(RedisStreamWireFormat.StreamKeyField, "testKey"),
            new(RedisStreamWireFormat.EventTypeField, "testEventType")
        ]);

        private static List<IBatchContainer> Delivered(params string[] ids) =>
            ids.Select(id => (IBatchContainer)new RedisStreamBatchContainer(Entry(id))).ToList();

        private static string[] Ids(IList<IBatchContainer>? batches) =>
            batches!.Cast<RedisStreamBatchContainer>().Select(b => b.StreamEntryId).ToArray();

        private static Task<StreamEntry[]> Reply(params StreamEntry[] entries) => Task.FromResult(entries);

        private ISetup<IDatabase, Task<StreamEntry[]>> SetupAnyRead() =>
            _mockDatabase.Setup(db => db.StreamReadGroupAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(),
                It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()));

        private ISetup<IDatabase, Task<StreamEntry[]>> SetupReadFrom(RedisValue position) =>
            _mockDatabase.Setup(db => db.StreamReadGroupAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.Is<RedisValue?>(p => p == position),
                It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()));

        /// <summary>Each read of new messages gets the next reply; once they run out, reads return nothing.</summary>
        private void SetupNewMessageReads(params Func<Task<StreamEntry[]>>[] replies)
        {
            var call = 0;
            SetupReadFrom(">").Returns(() => call < replies.Length ? replies[call++]() : Reply());
        }

        private static Func<Task<StreamEntry[]>> Returns(params StreamEntry[] entries) => () => Reply(entries);

        private static Func<Task<StreamEntry[]>> Throws(Exception ex) => () => Task.FromException<StreamEntry[]>(ex);

        /// <summary>Records the ids of every XACK; the first <paramref name="failFirst"/> of them fail.</summary>
        private List<string[]> RecordAcknowledgements(int failFirst)
        {
            var calls = new List<string[]>();
            _mockDatabase.Setup(db => db.StreamAcknowledgeAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey _, RedisValue _, RedisValue[] ids, CommandFlags _) =>
                {
                    calls.Add(ids.Select(id => id.ToString()).ToArray());
                    return calls.Count <= failFirst
                        ? Task.FromException<long>(new RedisTimeoutException("Timeout performing XACK", CommandStatus.Sent))
                        : Task.FromResult((long)ids.Length);
                });
            return calls;
        }

        [Fact]
        public async Task GetQueueMessagesAsync_DrainsAllPendingEntriesBeforeReadingNewOnes()
        {
            // Arrange: three entries are pending from a previous owner, one new entry is waiting.
            SetupReadFrom("0").Returns(Reply(Entry("1-0"), Entry("2-0")));
            SetupReadFrom("2-0").Returns(Reply(Entry("3-0")));
            SetupReadFrom("3-0").Returns(Reply());
            SetupReadFrom(">").Returns(Reply(Entry("4-0")));
            var receiver = CreateReceiver();

            // Act
            var first = await receiver.GetQueueMessagesAsync(2);
            var second = await receiver.GetQueueMessagesAsync(2);
            var third = await receiver.GetQueueMessagesAsync(2);

            // Assert
            Assert.Equal(new[] { "1-0", "2-0" }, Ids(first));
            Assert.Equal(new[] { "3-0" }, Ids(second));
            Assert.Equal(new[] { "4-0" }, Ids(third));
        }

        [Fact]
        public async Task GetQueueMessagesAsync_RereadsEntriesPastTheLastOneReturned_AfterAFailedRead()
        {
            // Arrange: the second read times out after Redis already moved "3-0" to this consumer's pending list.
            SetupReadFrom("0").Returns(Reply());
            SetupNewMessageReads(
                Returns(Entry("1-0"), Entry("2-0")),
                Throws(new RedisTimeoutException("Timeout performing XREADGROUP", CommandStatus.Sent)));
            SetupReadFrom("2-0").Returns(Reply(Entry("3-0")));
            var receiver = CreateReceiver();

            // Act
            var first = await receiver.GetQueueMessagesAsync(10);
            var failed = await receiver.GetQueueMessagesAsync(10);
            var recovered = await receiver.GetQueueMessagesAsync(10);

            // Assert
            Assert.Equal(new[] { "1-0", "2-0" }, Ids(first));
            Assert.Null(failed);
            Assert.Equal(new[] { "3-0" }, Ids(recovered));
        }

        [Fact]
        public async Task GetQueueMessagesAsync_RereadsThePendingListFromTheStart_AfterAFailedReadFromARecreatedGroup()
        {
            // Arrange: "5-0" came from the stream before it was lost; the recreated stream's ids need not be higher.
            SetupReadFrom("0").Returns(Reply());
            SetupNewMessageReads(
                Returns(Entry("5-0")),
                Throws(new RedisServerException("NOGROUP No such key or consumer group")),
                Throws(new RedisConnectionException(ConnectionFailureType.SocketFailure, "drop")));
            var receiver = CreateReceiver();
            await receiver.GetQueueMessagesAsync(10);
            await receiver.GetQueueMessagesAsync(10);
            await receiver.GetQueueMessagesAsync(10);
            _mockDatabase.Invocations.Clear();

            // Act
            await receiver.GetQueueMessagesAsync(10);

            // Assert
            _mockDatabase.Verify(db => db.StreamReadGroupAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.Is<RedisValue?>(p => p == "0"),
                    It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()),
                Times.Once);
        }

        [Fact]
        public async Task GetQueueMessagesAsync_ReadsAtMost1000_WhenMaxCountIsUnlimited()
        {
            // Arrange
            var receiver = CreateReceiver();

            // Act
            await receiver.GetQueueMessagesAsync(QueueAdapterConstants.UNLIMITED_GET_QUEUE_MSG);

            // Assert
            _mockDatabase.Verify(db => db.StreamReadGroupAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(),
                    1000, It.IsAny<bool>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()),
                Times.AtLeastOnce);
        }

        [Fact]
        public async Task GetQueueMessagesAsync_ReadsAsConsumerNamedAfterTheQueue()
        {
            // Arrange
            SetupAnyRead().Returns(Reply(Entry("1-0")));
            var receiver = CreateReceiver();

            // Act
            var result = await receiver.GetQueueMessagesAsync(10);

            // Assert
            Assert.Equal(new[] { "1-0" }, Ids(result));
            _mockDatabase.Verify(db => db.StreamReadGroupAsync(
                    _queueId.ToString(), RedisStreamWireFormat.GroupName, _queueId.ToString(), It.IsAny<RedisValue?>(),
                    It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()),
                Times.AtLeastOnce);
        }

        [Fact]
        public async Task GetQueueMessagesAsync_SkipsAndAcknowledgesUnreadableEntries()
        {
            // Arrange
            SetupAnyRead().Returns(Reply(Entry("1-0"), Unreadable("2-0"), Entry("3-0")));
            var receiver = CreateReceiver();

            // Act
            var result = await receiver.GetQueueMessagesAsync(10);

            // Assert
            Assert.Equal(new[] { "1-0", "3-0" }, Ids(result));
            _mockDatabase.Verify(db => db.StreamAcknowledgeAsync(
                    _queueId.ToString(), RedisStreamWireFormat.GroupName,
                    It.Is<RedisValue[]>(ids => ids.Length == 1 && ids[0] == "2-0"),
                    It.IsAny<CommandFlags>()),
                Times.Once);
        }

        [Fact]
        public async Task GetQueueMessagesAsync_ReturnsReadableEntries_WhenAcknowledgingUnreadableOnesFails()
        {
            // Arrange
            SetupAnyRead().Returns(Reply(Entry("1-0"), Unreadable("2-0"), Entry("3-0")));
            RecordAcknowledgements(failFirst: int.MaxValue);
            var receiver = CreateReceiver();

            // Act
            var result = await receiver.GetQueueMessagesAsync(10);

            // Assert
            Assert.Equal(new[] { "1-0", "3-0" }, Ids(result));
        }

        [Fact]
        public async Task GetQueueMessagesAsync_ShouldReturnNull_OnException()
        {
            // Arrange
            SetupAnyRead().ThrowsAsync(new Exception("Test exception"));
            var receiver = CreateReceiver();

            // Act
            var result = await receiver.GetQueueMessagesAsync(10);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task GetQueueMessagesAsync_RecreatesGroup_WhenGroupIsMissing()
        {
            // Arrange
            SetupAnyRead().ThrowsAsync(new RedisServerException("NOGROUP No such key 'q' or consumer group 'consumer' in XREADGROUP with GROUP option"));
            var receiver = CreateReceiver();

            // Act
            var result = await receiver.GetQueueMessagesAsync(10);

            // Assert
            Assert.NotNull(result);
            Assert.Empty(result);
            _mockDatabase.Verify(
                db => db.StreamCreateConsumerGroupAsync(_queueId.ToString(), RedisStreamWireFormat.GroupName, "0", true, CommandFlags.None),
                Times.Once);
        }

        [Fact]
        public async Task GetQueueMessagesAsync_LogsAndReturnsEmpty_WhenRecreatingGroupFails()
        {
            // Arrange
            SetupAnyRead().ThrowsAsync(new RedisServerException("NOGROUP No such key 'q' or consumer group 'consumer' in XREADGROUP with GROUP option"));
            _mockDatabase.Setup(db => db.StreamCreateConsumerGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                    It.IsAny<RedisValue?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()))
                .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "redis down"));
            var receiver = CreateReceiver();

            // Act
            var result = await receiver.GetQueueMessagesAsync(10);

            // Assert
            Assert.NotNull(result);
            Assert.Empty(result);
            _mockLogger.VerifyLogged(LogLevel.Error, "Error recreating consumer group", Times.Once());
        }

        [Fact]
        public async Task Initialize_CreatesConsumerGroup()
        {
            // Arrange
            _mockDatabase.Setup(db => db.StreamCreateConsumerGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                    It.IsAny<RedisValue>(), It.IsAny<bool>(), CommandFlags.None))
                .ReturnsAsync(true);
            var receiver = CreateReceiver();

            // Act
            await receiver.Initialize(TimeSpan.FromSeconds(5));

            // Assert
            _mockDatabase.Verify(
                db => db.StreamCreateConsumerGroupAsync(_queueId.ToString(), RedisStreamWireFormat.GroupName, "0", true, CommandFlags.None),
                Times.Once);
        }

        [Fact]
        public async Task Initialize_ShouldLogError_OnException()
        {
            // Arrange
            _mockDatabase.Setup(db => db.StreamCreateConsumerGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                    It.IsAny<RedisValue>(), It.IsAny<bool>(), CommandFlags.None))
                .ThrowsAsync(new Exception("Test exception"));
            var receiver = CreateReceiver();

            // Act
            await receiver.Initialize(TimeSpan.FromSeconds(5));

            // Assert
            _mockLogger.VerifyLogged(LogLevel.Error, "Error initializing stream", Times.Once());
        }

        [Fact]
        public async Task Initialize_DoesNotLogError_WhenGroupAlreadyExists()
        {
            // Arrange
            _mockDatabase.Setup(db => db.StreamCreateConsumerGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                    It.IsAny<RedisValue?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()))
                .ThrowsAsync(new RedisServerException("BUSYGROUP Consumer Group name already exists"));
            var receiver = CreateReceiver();

            // Act
            await receiver.Initialize(TimeSpan.FromSeconds(5));

            // Assert
            _mockLogger.VerifyLogged(LogLevel.Error, "", Times.Never());
        }

        [Fact]
        public async Task MessagesDeliveredAsync_AcknowledgesAllMessagesInOneCall()
        {
            // Arrange
            var receiver = CreateReceiver();

            // Act
            await receiver.MessagesDeliveredAsync(Delivered("1-0", "2-0"));

            // Assert
            _mockDatabase.Verify(db => db.StreamAcknowledgeAsync(
                    _queueId.ToString(), RedisStreamWireFormat.GroupName,
                    It.Is<RedisValue[]>(ids => ids.Length == 2 && ids[0] == "1-0" && ids[1] == "2-0"),
                    CommandFlags.None),
                Times.Once);
            _mockDatabase.Verify(db => db.StreamAcknowledgeAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()),
                Times.Never);
        }

        [Fact]
        public async Task MessagesDeliveredAsync_RetriesFailedAcknowledgementsWithTheNextOne()
        {
            // Arrange
            var acks = RecordAcknowledgements(failFirst: 1);
            var receiver = CreateReceiver();

            // Act
            await receiver.MessagesDeliveredAsync(Delivered("1-0", "2-0"));
            await receiver.MessagesDeliveredAsync(Delivered("3-0"));
            await receiver.MessagesDeliveredAsync(Delivered("4-0"));

            // Assert: the failed ids ride along with the next XACK, and are dropped once it succeeds.
            Assert.Equal(3, acks.Count);
            Assert.Equal(new[] { "1-0", "2-0", "3-0" }, acks[1].Order());
            Assert.Equal(new[] { "4-0" }, acks[2]);
        }

        [Fact]
        public async Task MessagesDeliveredAsync_RetriesUnreadableEntriesWhoseAcknowledgementFailed()
        {
            // Arrange
            SetupAnyRead().Returns(Reply(Entry("1-0"), Unreadable("2-0"), Entry("3-0")));
            var acks = RecordAcknowledgements(failFirst: 1);
            var receiver = CreateReceiver();

            // Act
            var batches = await receiver.GetQueueMessagesAsync(10);
            await receiver.MessagesDeliveredAsync(batches!);

            // Assert
            Assert.Equal(2, acks.Count);
            Assert.Equal(new[] { "2-0" }, acks[0]);
            Assert.Equal(new[] { "1-0", "2-0", "3-0" }, acks[1].Order());
        }

        [Fact]
        public async Task MessagesDeliveredAsync_DoesNothing_ForEmptyList()
        {
            // Arrange
            var receiver = CreateReceiver();

            // Act
            await receiver.MessagesDeliveredAsync(new List<IBatchContainer>());

            // Assert
            _mockDatabase.Verify(db => db.StreamAcknowledgeAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()),
                Times.Never);
        }

        [Fact]
        public async Task MessagesDeliveredAsync_ShouldLogError_OnException()
        {
            // Arrange
            RecordAcknowledgements(failFirst: int.MaxValue);
            var receiver = CreateReceiver();

            // Act
            await receiver.MessagesDeliveredAsync(Delivered("1-0"));

            // Assert
            _mockLogger.VerifyLogged(LogLevel.Error, "Error acknowledging messages in stream", Times.Once());
        }

        [Fact]
        public async Task Shutdown_WaitsForTheReadInFlight()
        {
            // Arrange
            var read = new TaskCompletionSource<StreamEntry[]>();
            SetupAnyRead().Returns(read.Task);
            var receiver = CreateReceiver();
            var getMessages = receiver.GetQueueMessagesAsync(10);

            // Act
            var shutdown = receiver.Shutdown(TimeSpan.FromSeconds(5));
            var completedBeforeReadFinished = shutdown.IsCompleted;
            read.SetResult([]);
            await shutdown;

            // Assert
            Assert.False(completedBeforeReadFinished);
            Assert.True(getMessages.IsCompleted);
        }
    }
}
