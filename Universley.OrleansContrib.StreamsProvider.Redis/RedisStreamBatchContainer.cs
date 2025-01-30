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
        
        public RedisStreamBatchContainer(StreamEntry streamEntry)
        {
            var streamNamespace = streamEntry.Values[0].Value;
            var steamKey = streamEntry.Values[1].Value;
            var eventType = streamEntry.Values[2].Value;
            var data = streamEntry.Values[3].Value;
            StreamEntryId = streamEntry.Id.ToString() ?? throw new ArgumentNullException(nameof(streamEntry.Id));
            
            // Check incoming data
            if (string.IsNullOrWhiteSpace(streamNamespace))
            {
                throw new ArgumentNullException(nameof(streamNamespace));
            }
            if (string.IsNullOrWhiteSpace(steamKey))
            {
                throw new ArgumentNullException(nameof(steamKey));
            }
            if (string.IsNullOrWhiteSpace(eventType))
            {
                throw new ArgumentNullException(nameof(eventType));
            }
            if (string.IsNullOrWhiteSpace(data))
            {
                throw new ArgumentNullException(nameof(data));
            }
            
            StreamId = StreamId.Create(streamNamespace!, steamKey!);
            SequenceToken = new RedisStreamSequenceToken(streamEntry.Id);
            EventType = eventType!;
            Data = data!;
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
