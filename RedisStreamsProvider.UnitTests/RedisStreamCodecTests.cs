using System.Text.Json;
using Orleans.Runtime;
using Orleans.Serialization;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace RedisStreamsProvider.UnitTests
{
    public class RedisStreamCodecTests
    {
        private static readonly StreamId Stream = StreamId.Create("namespace", "key");
        private static readonly RedisStreamCodec Codec = new(TestSerializer.Instance);

        [Fact]
        public void Constructor_ShouldThrowArgumentNullException_WhenSerializerIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => new RedisStreamCodec(null!));
        }

        [Theory]
        [InlineData(RedisStreamPayloadFormat.OrleansCodec)]
        [InlineData(RedisStreamPayloadFormat.Json)]
        public void TryDecode_ReadsWhatEncodeWrote(RedisStreamPayloadFormat format)
        {
            var entry = new StreamEntry("5-3", Encode(new TestEvent(7, "seven"), format));

            Assert.True(Codec.TryDecode(entry, out var batch, out var error));
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
        public void Encode_Throws_ForAnUndefinedFormat()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Encode(new TestEvent(1, "one"), (RedisStreamPayloadFormat)42));
        }

        [Fact]
        public void TryDecode_Fails_ForAFormatThisVersionDoesNotKnow()
        {
            // As a later version might write: an unknown format must be unreadable, not mistaken for legacy JSON.
            var fields = Encode(new TestEvent(1, "one"), RedisStreamPayloadFormat.OrleansCodec)
                .Select(f => f.Name == RedisStreamWireFormat.FormatField ? new NameValueEntry(f.Name, "orleans2") : f)
                .ToArray();

            Assert.False(Codec.TryDecode(new StreamEntry("1-0", fields), out _, out var error));
            Assert.Contains("unknown format orleans2", Assert.IsType<FormatException>(error).Message);
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

            Assert.True(Codec.TryDecode(entry, out var batch, out _));
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

            Assert.False(Codec.TryDecode(entry, out _, out var error));
            Assert.Contains($"has no {missingField} field", Assert.IsType<FormatException>(error).Message);
        }

        [Fact]
        public void TryDecode_OrleansCodec_DoesNotNeedTheEventType()
        {
            var fields = Encode(new TestEvent(1, "one"), RedisStreamPayloadFormat.OrleansCodec);
            var entry = new StreamEntry("1-0", fields.Where(f => f.Name != RedisStreamWireFormat.EventTypeField).ToArray());

            Assert.True(Codec.TryDecode(entry, out _, out _));
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

            Assert.False(Codec.TryDecode(entry, out _, out var error));
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

            Assert.False(Codec.TryDecode(entry, out _, out var error));
            Assert.Contains($"id {id} is malformed", Assert.IsType<FormatException>(error).Message);
        }

        [Fact]
        public void TryDecode_Fails_ForAnEntryDeletedWhilePending()
        {
            // Redis returns such an entry with its id and no fields.
            Assert.False(Codec.TryDecode(new StreamEntry("1-0", []), out _, out _));
        }

        private static NameValueEntry[] Encode<T>(T @event, RedisStreamPayloadFormat format) =>
            Codec.Encode(Stream, @event, format);
    }
}
