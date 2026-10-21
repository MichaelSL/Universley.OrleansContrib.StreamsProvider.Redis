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
    }
}
