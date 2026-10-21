using Orleans.Runtime;
using StackExchange.Redis;
using System.Text.Json;
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace RedisStreamsProvider.UnitTests
{
    public class RedisStreamBatchContainerTests
    {
        [Fact]
        public void Constructor_ShouldInitializeProperties()
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
            var container = new RedisStreamBatchContainer(streamEntry, TestSerializer.Instance);

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
            var container = new RedisStreamBatchContainer(streamEntry, TestSerializer.Instance);

            // Act
            var events = container.GetEvents<TestEvent>().ToList();

            // Assert
            Assert.Single(events);
            Assert.Equal(1, events[0].Item1.Id);
            Assert.Equal("Test", events[0].Item1.Name);
            Assert.Equal(streamEntry.Id.ToString().Split('-').First(), events[0].Item2.SequenceNumber.ToString());
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
            var container = new RedisStreamBatchContainer(streamEntry, TestSerializer.Instance);

            // Act
            var result = container.ImportRequestContext();

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void Constructor_ShouldThrowArgumentException_ForInvalidStreamEntry()
        {
            // Arrange
            var invalidStreamEntry = new StreamEntry("1-0", new NameValueEntry[]
            {
                new NameValueEntry(RedisStreamWireFormat.StreamNamespaceField, ""),
                new NameValueEntry(RedisStreamWireFormat.StreamKeyField, ""),
                new NameValueEntry(RedisStreamWireFormat.EventTypeField, ""),
                new NameValueEntry(RedisStreamWireFormat.DataField, "")
            });

            // Act & Assert
            Assert.Throws<ArgumentException>(() => new RedisStreamBatchContainer(invalidStreamEntry, TestSerializer.Instance));
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
        public void Constructor_ShouldThrowArgumentException_WithTheCause_ForAPayloadTheSerializerCannotRead()
        {
            var entry = new StreamEntry("1-0", [
                new(RedisStreamWireFormat.StreamNamespaceField, "testNamespace"),
                new(RedisStreamWireFormat.StreamKeyField, "testKey"),
                new(RedisStreamWireFormat.FormatField, RedisStreamWireFormat.OrleansCodecFormat),
                new(RedisStreamWireFormat.DataField, new byte[] { 0xff, 0xfe, 0x01 }),
            ]);

            var exception = Assert.Throws<ArgumentException>(() => new RedisStreamBatchContainer(entry, TestSerializer.Instance));
            Assert.NotNull(exception.InnerException);
        }

        private static RedisStreamBatchContainer Encoded<T>(T @event, string id = "1-0") =>
            new(new StreamEntry(id, RedisStreamWireFormat.Encode(StreamId.Create("testNamespace", "testKey"), @event,
                RedisStreamPayloadFormat.OrleansCodec, TestSerializer.Instance)), TestSerializer.Instance);

        // A plain class, as earlier versions required: legacy JSON entries need no Orleans serializer.
        private class TestEvent
        {
            public int Id { get; set; }
            public string? Name { get; set; }
        }
    }
}
