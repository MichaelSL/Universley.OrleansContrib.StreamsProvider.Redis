using Microsoft.Extensions.Logging;
using Moq;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace RedisStreamsProvider.UnitTests
{
    public class RedisStreamFactoryTests
    {
        private readonly Mock<IConnectionMultiplexer> _mockConnectionMultiplexer;
        private readonly Mock<ILoggerFactory> _mockLoggerFactory;
        private readonly Mock<IServiceProvider> _mockServiceProvider;
        private readonly Mock<IStreamFailureHandler> _mockStreamFailureHandler;
        private readonly SimpleQueueCacheOptions _simpleQueueCacheOptions;
        private readonly HashRingStreamQueueMapperOptions _hashRingStreamQueueMapperOptions;
        private readonly string _providerName = "TestProvider";

        public RedisStreamFactoryTests()
        {
            _mockConnectionMultiplexer = new Mock<IConnectionMultiplexer>();
            _mockConnectionMultiplexer.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(new Mock<IDatabase>().Object);

            _mockLoggerFactory = new Mock<ILoggerFactory>();
            _mockServiceProvider = new Mock<IServiceProvider>();
            _mockStreamFailureHandler = new Mock<IStreamFailureHandler>();
            _simpleQueueCacheOptions = new SimpleQueueCacheOptions();
            _hashRingStreamQueueMapperOptions = new HashRingStreamQueueMapperOptions();
        }

        [Fact]
        public void Constructor_ShouldThrowArgumentNullException_WhenAnyArgumentIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => new RedisStreamFactory(null, _mockLoggerFactory.Object, _providerName, _mockStreamFailureHandler.Object, _simpleQueueCacheOptions, _hashRingStreamQueueMapperOptions));
            Assert.Throws<ArgumentNullException>(() => new RedisStreamFactory(_mockConnectionMultiplexer.Object, null, _providerName, _mockStreamFailureHandler.Object, _simpleQueueCacheOptions, _hashRingStreamQueueMapperOptions));
            Assert.Throws<ArgumentNullException>(() => new RedisStreamFactory(_mockConnectionMultiplexer.Object, _mockLoggerFactory.Object, null, _mockStreamFailureHandler.Object, _simpleQueueCacheOptions, _hashRingStreamQueueMapperOptions));
            Assert.Throws<ArgumentNullException>(() => new RedisStreamFactory(_mockConnectionMultiplexer.Object, _mockLoggerFactory.Object, _providerName, null, _simpleQueueCacheOptions, _hashRingStreamQueueMapperOptions));
            Assert.Throws<ArgumentNullException>(() => new RedisStreamFactory(_mockConnectionMultiplexer.Object, _mockLoggerFactory.Object, _providerName, _mockStreamFailureHandler.Object, null, _hashRingStreamQueueMapperOptions));
            Assert.Throws<ArgumentNullException>(() => new RedisStreamFactory(_mockConnectionMultiplexer.Object, _mockLoggerFactory.Object, _providerName, _mockStreamFailureHandler.Object, _simpleQueueCacheOptions, null));
        }

        [Fact]
        public async Task CreateAdapter_ShouldReturnRedisStreamAdapterInstance()
        {
            var factory = new RedisStreamFactory(_mockConnectionMultiplexer.Object, _mockLoggerFactory.Object, _providerName, _mockStreamFailureHandler.Object, _simpleQueueCacheOptions, _hashRingStreamQueueMapperOptions);

            var adapter = await factory.CreateAdapter();

            Assert.NotNull(adapter);
            Assert.IsType<RedisStreamAdapter>(adapter);
        }

        [Fact]
        public async Task GetDeliveryFailureHandler_ShouldReturnStreamFailureHandler()
        {
            var factory = new RedisStreamFactory(_mockConnectionMultiplexer.Object, _mockLoggerFactory.Object, _providerName, _mockStreamFailureHandler.Object, _simpleQueueCacheOptions, _hashRingStreamQueueMapperOptions);

            var handler = await factory.GetDeliveryFailureHandler(new QueueId());

            Assert.NotNull(handler);
            Assert.Equal(_mockStreamFailureHandler.Object, handler);
        }

        [Fact]
        public void GetQueueAdapterCache_ShouldReturnSimpleQueueAdapterCacheInstance()
        {
            var factory = new RedisStreamFactory(_mockConnectionMultiplexer.Object, _mockLoggerFactory.Object, _providerName, _mockStreamFailureHandler.Object, _simpleQueueCacheOptions, _hashRingStreamQueueMapperOptions);

            var cache = factory.GetQueueAdapterCache();

            Assert.NotNull(cache);
            Assert.IsType<SimpleQueueAdapterCache>(cache);
        }

        [Fact]
        public void GetStreamQueueMapper_ShouldReturnHashRingBasedStreamQueueMapperInstance()
        {
            var factory = new RedisStreamFactory(_mockConnectionMultiplexer.Object, _mockLoggerFactory.Object, _providerName, _mockStreamFailureHandler.Object, _simpleQueueCacheOptions, _hashRingStreamQueueMapperOptions);

            var mapper = factory.GetStreamQueueMapper();

            Assert.NotNull(mapper);
            Assert.IsType<HashRingBasedStreamQueueMapper>(mapper);
        }
    }
}
