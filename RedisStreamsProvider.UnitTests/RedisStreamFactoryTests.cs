using Microsoft.Extensions.Logging;
using Moq;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;
using Microsoft.Extensions.Options;

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
        private readonly Mock<IOptions<RedisStreamReceiverOptions>> _mockReceiverOptions;

        public RedisStreamFactoryTests()
        {
            _mockConnectionMultiplexer = new Mock<IConnectionMultiplexer>();
            _mockConnectionMultiplexer.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(new Mock<IDatabase>().Object);

            _mockLoggerFactory = new Mock<ILoggerFactory>();
            _mockServiceProvider = new Mock<IServiceProvider>();
            _mockStreamFailureHandler = new Mock<IStreamFailureHandler>();
            _simpleQueueCacheOptions = new SimpleQueueCacheOptions();
            _hashRingStreamQueueMapperOptions = new HashRingStreamQueueMapperOptions();
            _mockReceiverOptions = new Mock<IOptions<RedisStreamReceiverOptions>>();
            _mockReceiverOptions.Setup(o => o.Value).Returns(new RedisStreamReceiverOptions());
        }

        [Fact]
        public void Constructor_ShouldThrowArgumentNullException_WhenAnyArgumentIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => new RedisStreamFactory(null!, _mockLoggerFactory.Object, _providerName, _mockStreamFailureHandler.Object, _simpleQueueCacheOptions, _hashRingStreamQueueMapperOptions, _mockReceiverOptions.Object));
            Assert.Throws<ArgumentNullException>(() => new RedisStreamFactory(_mockConnectionMultiplexer.Object, null!, _providerName, _mockStreamFailureHandler.Object, _simpleQueueCacheOptions, _hashRingStreamQueueMapperOptions, _mockReceiverOptions.Object));
            Assert.Throws<ArgumentNullException>(() => new RedisStreamFactory(_mockConnectionMultiplexer.Object, _mockLoggerFactory.Object, null!, _mockStreamFailureHandler.Object, _simpleQueueCacheOptions, _hashRingStreamQueueMapperOptions, _mockReceiverOptions.Object));
            Assert.Throws<ArgumentNullException>(() => new RedisStreamFactory(_mockConnectionMultiplexer.Object, _mockLoggerFactory.Object, _providerName, null!, _simpleQueueCacheOptions, _hashRingStreamQueueMapperOptions, _mockReceiverOptions.Object));
            Assert.Throws<ArgumentNullException>(() => new RedisStreamFactory(_mockConnectionMultiplexer.Object, _mockLoggerFactory.Object, _providerName, _mockStreamFailureHandler.Object, null!, _hashRingStreamQueueMapperOptions, _mockReceiverOptions.Object));
            Assert.Throws<ArgumentNullException>(() => new RedisStreamFactory(_mockConnectionMultiplexer.Object, _mockLoggerFactory.Object, _providerName, _mockStreamFailureHandler.Object, _simpleQueueCacheOptions, null!, _mockReceiverOptions.Object));
            Assert.Throws<ArgumentNullException>(() => new RedisStreamFactory(_mockConnectionMultiplexer.Object, _mockLoggerFactory.Object, _providerName, _mockStreamFailureHandler.Object, _simpleQueueCacheOptions, _hashRingStreamQueueMapperOptions, null!));
        }

        [Fact]
        public async Task CreateAdapter_ShouldReturnRedisStreamAdapterInstance()
        {
            var factory = new RedisStreamFactory(_mockConnectionMultiplexer.Object, _mockLoggerFactory.Object, _providerName, _mockStreamFailureHandler.Object, _simpleQueueCacheOptions, _hashRingStreamQueueMapperOptions, _mockReceiverOptions.Object);

            var adapter = await factory.CreateAdapter();

            Assert.NotNull(adapter);
            Assert.IsType<RedisStreamAdapter>(adapter);
        }

        [Fact]
        public async Task GetDeliveryFailureHandler_ShouldReturnStreamFailureHandler()
        {
            var factory = new RedisStreamFactory(_mockConnectionMultiplexer.Object, _mockLoggerFactory.Object, _providerName, _mockStreamFailureHandler.Object, _simpleQueueCacheOptions, _hashRingStreamQueueMapperOptions, _mockReceiverOptions.Object);

            var handler = await factory.GetDeliveryFailureHandler(new QueueId());

            Assert.NotNull(handler);
            Assert.Equal(_mockStreamFailureHandler.Object, handler);
        }

        [Fact]
        public void GetQueueAdapterCache_ShouldReturnSimpleQueueAdapterCacheInstance()
        {
            var factory = new RedisStreamFactory(_mockConnectionMultiplexer.Object, _mockLoggerFactory.Object, _providerName, _mockStreamFailureHandler.Object, _simpleQueueCacheOptions, _hashRingStreamQueueMapperOptions, _mockReceiverOptions.Object);

            var cache = factory.GetQueueAdapterCache();

            Assert.NotNull(cache);
            Assert.IsType<SimpleQueueAdapterCache>(cache);
        }

        [Fact]
        public void GetStreamQueueMapper_ShouldReturnHashRingBasedStreamQueueMapperInstance()
        {
            var factory = new RedisStreamFactory(_mockConnectionMultiplexer.Object, _mockLoggerFactory.Object, _providerName, _mockStreamFailureHandler.Object, _simpleQueueCacheOptions, _hashRingStreamQueueMapperOptions, _mockReceiverOptions.Object);

            var mapper = factory.GetStreamQueueMapper();

            Assert.NotNull(mapper);
            Assert.IsType<HashRingBasedStreamQueueMapper>(mapper);
        }

        [Fact]
        public void Create_ShouldThrowException_WhenServiceProviderIsNull()
        {
            // Arrange
            // No need to mock GetService for IOptions<RedisStreamReceiverOptions> here, 
            // as the ArgumentNullException for services should be thrown first.
            
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => RedisStreamFactory.Create(null!, _providerName));
        }
    }
}
