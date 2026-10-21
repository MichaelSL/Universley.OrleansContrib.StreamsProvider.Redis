using System.Text.Json;
using Orleans.Runtime;
using Orleans.Serialization;
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
        private static readonly StreamId Stream = StreamId.Create("namespace", "key");

        [Fact]
        public void Names_MatchWhatEarlierVersionsStored()
        {
            Assert.Equal("streamNamespace", RedisStreamWireFormat.StreamNamespaceField);
            Assert.Equal("streamKey", RedisStreamWireFormat.StreamKeyField);
            Assert.Equal("eventType", RedisStreamWireFormat.EventTypeField);
            Assert.Equal("data", RedisStreamWireFormat.DataField);
            Assert.Equal("consumer", RedisStreamWireFormat.GroupName);
            Assert.Equal("format", RedisStreamWireFormat.FormatField);
            Assert.Equal("orleans", RedisStreamWireFormat.OrleansCodecFormat);
        }

        [Fact]
        public void StreamKey_IsTheQueueIdString()
        {
            var queueId = QueueId.GetQueueId("provider", 3, 42);

            Assert.Equal(queueId.ToString(), RedisStreamWireFormat.StreamKey(queueId));
        }

        [Fact]
        public void PayloadFormat_DefaultsToOrleansCodec()
        {
            Assert.Equal(RedisStreamPayloadFormat.OrleansCodec, new RedisStreamReceiverOptions().PayloadFormat);
        }

        [Theory]
        [InlineData(RedisStreamPayloadFormat.OrleansCodec)]
        [InlineData(RedisStreamPayloadFormat.Json)]
        public void TryDecode_ReadsWhatEncodeWrote(RedisStreamPayloadFormat format)
        {
            var entry = new StreamEntry("5-3", Encode(new TestEvent(7, "seven"), format));

            Assert.True(RedisStreamWireFormat.TryDecode(entry, TestSerializer.Instance, out var batch, out var error));
            Assert.Null(error);
            Assert.Equal(Stream, batch.StreamId);
            Assert.Equal("5-3", batch.StreamEntryId);
            Assert.Equal(new RedisStreamSequenceToken(5, 3), batch.SequenceToken);
            Assert.Equal(new TestEvent(7, "seven"), Assert.Single(batch.GetEvents<TestEvent>()).Item1);
        }

        [Fact]
        public void Encode_Json_WritesExactlyTheFieldsEarlierVersionsWrote()
        {
            var fields = Encode(new TestEvent(7, "seven"), RedisStreamPayloadFormat.Json);

            Assert.Equal(
                new NameValueEntry[]
                {
                    new(RedisStreamWireFormat.StreamNamespaceField, "namespace"),
                    new(RedisStreamWireFormat.StreamKeyField, "key"),
                    new(RedisStreamWireFormat.EventTypeField, nameof(TestEvent)),
                    new(RedisStreamWireFormat.DataField, """{"Id":7,"Name":"seven"}"""),
                },
                fields);
        }

        [Fact]
        public void Encode_OrleansCodec_MarksTheFormat()
        {
            var fields = Encode(new TestEvent(7, "seven"), RedisStreamPayloadFormat.OrleansCodec);

            Assert.Contains(new NameValueEntry(RedisStreamWireFormat.FormatField, RedisStreamWireFormat.OrleansCodecFormat), fields);
        }

        [Fact]
        public void Encode_OrleansCodec_Throws_ForATypeTheSerializerDoesNotKnow()
        {
            Assert.Throws<CodecNotFoundException>(() => Encode(new UnserializableEvent(1), RedisStreamPayloadFormat.OrleansCodec));
        }

        [Fact]
        public void Encode_Throws_ForAnUnknownFormat()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Encode(new TestEvent(1, "one"), (RedisStreamPayloadFormat)42));
        }

        [Fact]
        public void TryDecode_ReadsAnEntryWrittenByEarlierVersions()
        {
            var entry = new StreamEntry("5-3", [
                new(RedisStreamWireFormat.StreamNamespaceField, "namespace"),
                new(RedisStreamWireFormat.StreamKeyField, "key"),
                new(RedisStreamWireFormat.EventTypeField, nameof(TestEvent)),
                new(RedisStreamWireFormat.DataField, JsonSerializer.Serialize(new TestEvent(7, "seven"))),
            ]);

            Assert.True(RedisStreamWireFormat.TryDecode(entry, TestSerializer.Instance, out var batch, out _));
            Assert.Equal(new TestEvent(7, "seven"), Assert.Single(batch.GetEvents<TestEvent>()).Item1);
        }

        [Theory]
        [InlineData(RedisStreamPayloadFormat.OrleansCodec, RedisStreamWireFormat.StreamNamespaceField)]
        [InlineData(RedisStreamPayloadFormat.OrleansCodec, RedisStreamWireFormat.StreamKeyField)]
        [InlineData(RedisStreamPayloadFormat.OrleansCodec, RedisStreamWireFormat.DataField)]
        [InlineData(RedisStreamPayloadFormat.Json, RedisStreamWireFormat.StreamNamespaceField)]
        [InlineData(RedisStreamPayloadFormat.Json, RedisStreamWireFormat.StreamKeyField)]
        [InlineData(RedisStreamPayloadFormat.Json, RedisStreamWireFormat.EventTypeField)]
        [InlineData(RedisStreamPayloadFormat.Json, RedisStreamWireFormat.DataField)]
        public void TryDecode_Fails_WhenAFieldIsMissing(RedisStreamPayloadFormat format, string missingField)
        {
            var fields = Encode(new TestEvent(1, "one"), format);
            var entry = new StreamEntry("1-0", fields.Where(f => f.Name != missingField).ToArray());

            Assert.False(RedisStreamWireFormat.TryDecode(entry, TestSerializer.Instance, out _, out _));
        }

        [Fact]
        public void TryDecode_OrleansCodec_DoesNotNeedTheEventType()
        {
            var fields = Encode(new TestEvent(1, "one"), RedisStreamPayloadFormat.OrleansCodec);
            var entry = new StreamEntry("1-0", fields.Where(f => f.Name != RedisStreamWireFormat.EventTypeField).ToArray());

            Assert.True(RedisStreamWireFormat.TryDecode(entry, TestSerializer.Instance, out _, out _));
        }

        [Fact]
        public void TryDecode_Fails_WithTheError_ForAPayloadTheSerializerCannotRead()
        {
            var entry = new StreamEntry("1-0", [
                new(RedisStreamWireFormat.StreamNamespaceField, "namespace"),
                new(RedisStreamWireFormat.StreamKeyField, "key"),
                new(RedisStreamWireFormat.FormatField, RedisStreamWireFormat.OrleansCodecFormat),
                new(RedisStreamWireFormat.DataField, new byte[] { 0xff, 0xfe, 0x01 }),
            ]);

            Assert.False(RedisStreamWireFormat.TryDecode(entry, TestSerializer.Instance, out _, out var error));
            Assert.NotNull(error);
        }

        [Theory]
        [InlineData("1")]
        [InlineData("x-0")]
        [InlineData("1-x")]
        [InlineData("99999999999999999999-0")]
        public void TryDecode_Fails_ForAMalformedId(string id)
        {
            var entry = new StreamEntry(id, Encode(new TestEvent(1, "one"), RedisStreamPayloadFormat.OrleansCodec));

            Assert.False(RedisStreamWireFormat.TryDecode(entry, TestSerializer.Instance, out _, out _));
        }

        [Fact]
        public void TryDecode_Fails_ForAnEntryDeletedWhilePending()
        {
            // Redis returns such an entry with its id and no fields.
            Assert.False(RedisStreamWireFormat.TryDecode(new StreamEntry("1-0", []), TestSerializer.Instance, out _, out _));
        }

        private static NameValueEntry[] Encode<T>(T @event, RedisStreamPayloadFormat format) =>
            RedisStreamWireFormat.Encode(Stream, @event, format, TestSerializer.Instance);
    }
}
