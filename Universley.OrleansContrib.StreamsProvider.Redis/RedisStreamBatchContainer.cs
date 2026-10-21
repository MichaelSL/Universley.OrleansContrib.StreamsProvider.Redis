using Orleans.Streams;

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
            if (Event is RedisStreamLegacyJsonEvent legacy)
            {
                return legacy.TryRead<T>(out var read) ? [new(read, SequenceToken)] : [];
            }
            return Event is T @event ? [new(@event, SequenceToken)] : [];
        }

        public bool ImportRequestContext()
        {
            return false;
        }
    }
}
