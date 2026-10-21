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

        [Id(5)]
        internal IRedisStreamEvent Event { get; }

        internal RedisStreamBatchContainer(StreamId streamId, RedisStreamSequenceToken sequenceToken, IRedisStreamEvent @event, string streamEntryId)
        {
            StreamId = streamId;
            SequenceToken = sequenceToken;
            Event = @event;
            StreamEntryId = streamEntryId;
        }

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            return Event.TryRead<T>(out var @event) ? [new(@event, SequenceToken)] : [];
        }

        public bool ImportRequestContext()
        {
            return false;
        }
    }
}
