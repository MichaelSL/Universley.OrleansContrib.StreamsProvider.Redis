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
        private readonly RedisStreamAdapter _adapter;

        public RedisStreamAdapterTests()
        {
            _mockDatabase = new Mock<IDatabase>();
            var options = new HashRingStreamQueueMapperOptions { TotalQueueCount = 1 };
            _mockQueueMapper = new Mock<HashRingBasedStreamQueueMapper>(options, "queueNamePrefix");
            _mockLoggerFactory = new Mock<ILoggerFactory>();

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
    }
}
