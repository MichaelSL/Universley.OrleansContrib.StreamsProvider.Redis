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
        
        [Id(2)]
        public string EventType { get; }
        
        [Id(3)]
        public string Data { get; } 
        
        [Id(4)]
        public string StreamEntryId { get; }
        
        /// <exception cref="ArgumentException"><paramref name="streamEntry"/> was not written by this provider.</exception>
        public RedisStreamBatchContainer(StreamEntry streamEntry)
        {
            if (!RedisStreamWireFormat.TryDecode(streamEntry, out var decoded))
            {
                throw new ArgumentException($"Stream entry {streamEntry.Id} is not an event written by this provider", nameof(streamEntry));
            }

            StreamId = decoded.StreamId;
            SequenceToken = decoded.SequenceToken;
            EventType = decoded.EventType;
            Data = decoded.Data;
            StreamEntryId = decoded.StreamEntryId;
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
            var eventType = typeof(T).Name;
            if (eventType == EventType)
            {
                var data = Data;
                var @event = JsonSerializer.Deserialize<T>(data);
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
