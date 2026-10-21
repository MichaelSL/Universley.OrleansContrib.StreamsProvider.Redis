using Universley.OrleansContrib.StreamsProvider.Redis;

namespace RedisStreamsProvider.UnitTests
{
    public class RedisStreamPublisherOptionsTests
    {
        [Fact]
        public void PayloadFormat_DefaultsToOrleansCodec()
        {
            Assert.Equal(RedisStreamPayloadFormat.OrleansCodec, new RedisStreamPublisherOptions().PayloadFormat);
        }

        [Fact]
        public void PayloadFormat_Throws_ForAnUndefinedValue()
        {
            var options = new RedisStreamPublisherOptions();

            Assert.Throws<ArgumentOutOfRangeException>(() => options.PayloadFormat = (RedisStreamPayloadFormat)42);
            Assert.Equal(RedisStreamPayloadFormat.OrleansCodec, options.PayloadFormat);
        }
    }
}
