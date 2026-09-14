using Orleans.Runtime;
using Orleans.Streams;
using StackExchange.Redis;
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

        [Fact]
        public void TryDecode_ReadsWhatEncodeWrote()
        {
            var streamId = StreamId.Create("namespace", "key");
            var entry = new StreamEntry("5-3", RedisStreamWireFormat.Encode(streamId, new TestEvent(7, "seven")));

            Assert.True(RedisStreamWireFormat.TryDecode(entry, out var batch));
            Assert.Equal(streamId, batch.StreamId);
            Assert.Equal("5-3", batch.StreamEntryId);
            Assert.Equal(new RedisStreamSequenceToken(5, 3), batch.SequenceToken);
            Assert.Equal(new TestEvent(7, "seven"), Assert.Single(batch.GetEvents<TestEvent>()).Item1);
        }

        [Theory]
        [InlineData(RedisStreamWireFormat.StreamNamespaceField)]
        [InlineData(RedisStreamWireFormat.StreamKeyField)]
        [InlineData(RedisStreamWireFormat.EventTypeField)]
        [InlineData(RedisStreamWireFormat.DataField)]
        public void TryDecode_Fails_WhenAFieldIsMissing(string missingField)
        {
            var fields = RedisStreamWireFormat.Encode(StreamId.Create("namespace", "key"), new TestEvent(1, "one"));
            var entry = new StreamEntry("1-0", fields.Where(f => f.Name != missingField).ToArray());

            Assert.False(RedisStreamWireFormat.TryDecode(entry, out _));
        }

        [Theory]
        [InlineData("1")]
        [InlineData("x-0")]
        [InlineData("1-x")]
        [InlineData("99999999999999999999-0")]
        public void TryDecode_Fails_ForAMalformedId(string id)
        {
            var entry = new StreamEntry(id, RedisStreamWireFormat.Encode(StreamId.Create("namespace", "key"), new TestEvent(1, "one")));

            Assert.False(RedisStreamWireFormat.TryDecode(entry, out _));
        }

        [Fact]
        public void TryDecode_Fails_ForAnEntryDeletedWhilePending()
        {
            // Redis returns such an entry with its id and no fields.
            Assert.False(RedisStreamWireFormat.TryDecode(new StreamEntry("1-0", []), out _));
        }

        public record TestEvent(int Id, string Name);
    }
}
