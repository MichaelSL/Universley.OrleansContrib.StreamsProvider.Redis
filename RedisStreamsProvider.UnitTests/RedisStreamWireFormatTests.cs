using Orleans.Streams;
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace RedisStreamsProvider.UnitTests
{
    /// <summary>
    /// Pins the names stored in Redis. If one of these fails, the change breaks streams and consumer groups written by
    /// earlier versions; every other test uses the constants.
    /// </summary>
    public class RedisStreamWireFormatTests
    {
        [Fact]
        public void Names_MatchWhatEarlierVersionsStored()
        {
            Assert.Equal("streamNamespace", RedisStreamWireFormat.StreamNamespaceField);
            Assert.Equal("streamKey", RedisStreamWireFormat.StreamKeyField);
            Assert.Equal("eventType", RedisStreamWireFormat.EventTypeField);
            Assert.Equal("data", RedisStreamWireFormat.DataField);
            Assert.Equal("consumer", RedisStreamWireFormat.GroupName);
        }

        [Fact]
        public void StreamKey_IsTheQueueIdString()
        {
            var queueId = QueueId.GetQueueId("provider", 3, 42);

            Assert.Equal(queueId.ToString(), RedisStreamWireFormat.StreamKey(queueId));
        }
    }
}
