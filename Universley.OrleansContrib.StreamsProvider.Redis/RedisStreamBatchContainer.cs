using Orleans.Streams;
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

        [Id(4)]
        public string StreamEntryId { get; }

        /// <summary>The event, or a <see cref="RedisStreamLegacyJsonEvent"/> for an entry in the legacy JSON format.</summary>
        [Id(5)]
        internal object Event { get; }

        internal RedisStreamBatchContainer(StreamId streamId, RedisStreamSequenceToken sequenceToken, object @event, string streamEntryId)
        {
            StreamId = streamId;
            SequenceToken = sequenceToken;
            Event = @event;
            StreamEntryId = streamEntryId;
        }

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            List<Tuple<T, StreamSequenceToken>> events = new();
            switch (Event)
            {
                case RedisStreamLegacyJsonEvent legacy:
                    if (legacy.TypeName == typeof(T).Name)
                    {
                        events.Add(new(JsonSerializer.Deserialize<T>(legacy.Json)!, SequenceToken));
                    }
                    break;
                case T @event:
                    events.Add(new(@event, SequenceToken));
                    break;
            }
            return events;
        }

        public bool ImportRequestContext()
        {
            return false;
        }
    }
}
