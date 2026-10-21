using Orleans.Serialization;
using Orleans.Streams;
using StackExchange.Redis;
using System.Text.Json;

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    [GenerateSerializer]
    [Alias("Universley.OrleansContrib.StreamsProvider.Redis.RedisStreamBatchContainer")]
    public class RedisStreamBatchContainer : IBatchContainer
    {
        [Id(0)]
        public StreamId StreamId { get; }

        [Id(1)]
        public StreamSequenceToken SequenceToken { get; }

        /// <summary>The short type name of the event. Set only for entries in the legacy JSON format.</summary>
        [Id(2)]
        public string? EventType { get; }

        /// <summary>The event as JSON. Set only for entries in the legacy JSON format.</summary>
        [Id(3)]
        public string? Data { get; }

        [Id(4)]
        public string StreamEntryId { get; }

        /// <summary>The event. Set only for entries in <see cref="RedisStreamPayloadFormat.OrleansCodec"/>.</summary>
        [Id(5)]
        public object? Event { get; }

        /// <exception cref="ArgumentException"><paramref name="streamEntry"/> was not written by this provider, or its payload cannot be deserialized.</exception>
        public RedisStreamBatchContainer(StreamEntry streamEntry, Serializer serializer)
        {
            if (!RedisStreamWireFormat.TryDecode(streamEntry, serializer, out var decoded, out var error))
            {
                throw new ArgumentException($"Stream entry {streamEntry.Id} is not an event written by this provider", nameof(streamEntry), error);
            }

            StreamId = decoded.StreamId;
            SequenceToken = decoded.SequenceToken;
            EventType = decoded.EventType;
            Data = decoded.Data;
            StreamEntryId = decoded.StreamEntryId;
            Event = decoded.Event;
        }

        internal RedisStreamBatchContainer(StreamId streamId, RedisStreamSequenceToken sequenceToken, object @event, string streamEntryId)
        {
            StreamId = streamId;
            SequenceToken = sequenceToken;
            Event = @event;
            StreamEntryId = streamEntryId;
        }

        internal RedisStreamBatchContainer(StreamId streamId, RedisStreamSequenceToken sequenceToken, string eventType, string data, string streamEntryId)
        {
            StreamId = streamId;
            SequenceToken = sequenceToken;
            EventType = eventType;
            Data = data;
            StreamEntryId = streamEntryId;
        }

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            List<Tuple<T, StreamSequenceToken>> events = new();
            if (Event is not null)
            {
                if (Event is T @event)
                {
                    events.Add(new(@event, SequenceToken));
                }
            }
            else if (typeof(T).Name == EventType)
            {
                var @event = JsonSerializer.Deserialize<T>(Data!);
                events.Add(new(@event!, SequenceToken));
            }
            return events;
        }

        public bool ImportRequestContext()
        {
            return false;
        }
    }
}
