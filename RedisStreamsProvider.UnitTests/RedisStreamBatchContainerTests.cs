using Orleans.Runtime;
using Orleans.Serialization;
using StackExchange.Redis;
using System.Text.Json;
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace RedisStreamsProvider.UnitTests
{
    public class RedisStreamBatchContainerTests
    {
        [Fact]
        public void Decode_ShouldInitializeProperties()
        {
            // Arrange
            var streamEntry = new StreamEntry("1-0", new NameValueEntry[]
            {
                        new NameValueEntry(RedisStreamWireFormat.StreamNamespaceField, "testNamespace"),
                        new NameValueEntry(RedisStreamWireFormat.StreamKeyField, "testKey"),
                        new NameValueEntry(RedisStreamWireFormat.EventTypeField, "TestEvent"),
                        new NameValueEntry(RedisStreamWireFormat.DataField, JsonSerializer.Serialize(new TestEvent { Id = 1, Name = "Test" }))
            });

            // Act
            var container = Codec.Decode(streamEntry);

            // Assert
            Assert.Equal("testNamespace", container.StreamId.GetNamespace());
            Assert.Equal("testKey", container.StreamId.GetKeyAsString());
            Assert.Equal(streamEntry.Id.ToString().Split('-').First(), container.SequenceToken.SequenceNumber.ToString());
        }

        [Fact]
        public void GetEvents_ShouldReturnDeserializedEvents()
        {
            // Arrange
            var streamEntry = new StreamEntry("1-0", new NameValueEntry[]
            {
                        new NameValueEntry(RedisStreamWireFormat.StreamNamespaceField, "testNamespace"),
                        new NameValueEntry(RedisStreamWireFormat.StreamKeyField, "testKey"),
                        new NameValueEntry(RedisStreamWireFormat.EventTypeField, "TestEvent"),
                        new NameValueEntry(RedisStreamWireFormat.DataField, JsonSerializer.Serialize(new TestEvent { Id = 1, Name = "Test" }))
            });
            var container = Codec.Decode(streamEntry);

            // Act
            var events = container.GetEvents<TestEvent>().ToList();

            // Assert
            Assert.Single(events);
            Assert.Equal(1, events[0].Item1.Id);
            Assert.Equal("Test", events[0].Item1.Name);
            Assert.Equal(streamEntry.Id.ToString().Split('-').First(), events[0].Item2.SequenceNumber.ToString());
        }

        [Fact]
        public void GetEvents_LegacyJson_ReturnsNothing_ForASubscriberToATypeWithAnotherShortName()
        {
            var container = Codec.Decode(new StreamEntry("1-0", new NameValueEntry[]
            {
                new(RedisStreamWireFormat.StreamNamespaceField, "testNamespace"),
                new(RedisStreamWireFormat.StreamKeyField, "testKey"),
                new(RedisStreamWireFormat.EventTypeField, "TestEvent"),
                new(RedisStreamWireFormat.DataField, JsonSerializer.Serialize(new TestEvent { Id = 1, Name = "Test" }))
            }));

            Assert.Empty(container.GetEvents<DerivedTestEvent>());
        }

        [Fact]
        public void ImportRequestContext_ShouldReturnFalse()
        {
            // Arrange
            var streamEntry = new StreamEntry("1-0", new NameValueEntry[]
            {
                        new NameValueEntry(RedisStreamWireFormat.StreamNamespaceField, "testNamespace"),
                        new NameValueEntry(RedisStreamWireFormat.StreamKeyField, "testKey"),
                        new NameValueEntry(RedisStreamWireFormat.EventTypeField, "TestEvent"),
                        new NameValueEntry(RedisStreamWireFormat.DataField, JsonSerializer.Serialize(new TestEvent { Id = 1, Name = "Test" }))
            });
            var container = Codec.Decode(streamEntry);

            // Act
            var result = container.ImportRequestContext();

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void GetEvents_OrleansCodec_DeliversToSubscribersOfTheTypeItsBaseTypesAndInterfaces()
        {
            var container = Encoded(new DerivedTestEvent(1, "one", "extra"));

            Assert.Equal(new DerivedTestEvent(1, "one", "extra"), Assert.Single(container.GetEvents<DerivedTestEvent>()).Item1);
            Assert.Equal(new DerivedTestEvent(1, "one", "extra"), Assert.Single(container.GetEvents<UnitTests.TestEvent>()).Item1);
            Assert.Equal(1, Assert.Single(container.GetEvents<ITestEvent>()).Item1.Id);
            Assert.Single(container.GetEvents<object>());
        }

        [Fact]
        public void GetEvents_OrleansCodec_TellsApartTypesWithTheSameShortName()
        {
            Assert.Equal(new Envelope<int>(1), Assert.Single(Encoded(new Envelope<int>(1)).GetEvents<Envelope<int>>()).Item1);
            Assert.Empty(Encoded(new Envelope<int>(1)).GetEvents<Envelope<string>>());
            Assert.Empty(Encoded(new UnitTests.TestEvent(1, "one")).GetEvents<Other.TestEvent>());
        }

        [Fact]
        public void GetEvents_OrleansCodec_ReturnsTheEntrysSequenceToken()
        {
            var container = Encoded(new UnitTests.TestEvent(1, "one"), "5-3");

            Assert.Equal(new RedisStreamSequenceToken(5, 3), Assert.Single(container.GetEvents<UnitTests.TestEvent>()).Item2);
        }

        [Fact]
        public void RoundTripsThroughTheOrleansSerializer_AsWhenDeliveredToAConsumerOnAnotherSilo()
        {
            var orleans = RoundTrip(Encoded(new UnitTests.TestEvent(1, "one"), "5-3"));
            var legacy = RoundTrip(Codec.Decode(new StreamEntry("1-0", new RedisStreamCodec(TestSerializer.Instance, RedisStreamPayloadFormat.Json).Encode(StreamId.Create("testNamespace", "testKey"), new UnitTests.TestEvent(2, "two")))));

            Assert.Equal("5-3", orleans.StreamEntryId);
            Assert.Equal(new RedisStreamSequenceToken(5, 3), orleans.SequenceToken);
            Assert.Equal(new UnitTests.TestEvent(1, "one"), Assert.Single(orleans.GetEvents<UnitTests.TestEvent>()).Item1);
            Assert.Equal(new UnitTests.TestEvent(2, "two"), Assert.Single(legacy.GetEvents<UnitTests.TestEvent>()).Item1);
        }

        private static RedisStreamBatchContainer RoundTrip(RedisStreamBatchContainer container) =>
            TestSerializer.Instance.Deserialize<RedisStreamBatchContainer>(TestSerializer.Instance.SerializeToArray(container));

        private static readonly RedisStreamCodec Codec = new(TestSerializer.Instance, RedisStreamPayloadFormat.OrleansCodec);

        private static RedisStreamBatchContainer Encoded<T>(T @event, string id = "1-0") =>
            Codec.Decode(new StreamEntry(id, Codec.Encode(StreamId.Create("testNamespace", "testKey"), @event)));

        // A plain class, as earlier versions required: legacy JSON entries need no Orleans serializer.
        private class TestEvent
        {
            public int Id { get; set; }
            public string? Name { get; set; }
        }
    }
}
