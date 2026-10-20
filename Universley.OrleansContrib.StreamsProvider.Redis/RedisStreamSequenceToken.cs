using Orleans.Streams;
using StackExchange.Redis;

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    [GenerateSerializer]
    public class RedisStreamSequenceToken : StreamSequenceToken
    {
        [Id(0)]
        public sealed override long SequenceNumber { get; protected set; }
        [Id(1)]
        public sealed override int EventIndex { get; protected set; }

        public RedisStreamSequenceToken(RedisValue id)
        {
            if (!TryParse(id, out var sequenceNumber, out var eventIndex))
            {
                throw new ArgumentException(message: $"Invalid {nameof(id)}", paramName: nameof(id));
            }

            SequenceNumber = sequenceNumber;
            EventIndex = eventIndex;
        }

        public RedisStreamSequenceToken(long sequenceNumber, int eventIndex)
        {
            SequenceNumber = sequenceNumber;
            EventIndex = eventIndex;
        }

        /// <summary>Parses a stream entry id of the form <c>milliseconds-sequence</c>.</summary>
        internal static bool TryParse(RedisValue id, out long sequenceNumber, out int eventIndex)
        {
            var value = id.ToString().AsSpan();
            var splitIndex = value.IndexOf('-');
            sequenceNumber = 0;
            eventIndex = 0;
            return splitIndex >= 0
                && long.TryParse(value[..splitIndex], out sequenceNumber)
                && int.TryParse(value[(splitIndex + 1)..], out eventIndex);
        }

        public override int CompareTo(StreamSequenceToken other)
        {
            if (other is null) throw new ArgumentNullException(nameof(other));
            if (other is RedisStreamSequenceToken token)
            {
                if (SequenceNumber == token.SequenceNumber)
                {
                    return EventIndex.CompareTo(token.EventIndex);
                }
                return SequenceNumber.CompareTo(token.SequenceNumber);
            }
            throw new ArgumentException("Invalid token type", nameof(other));
        }

        public override bool Equals(StreamSequenceToken? other)
        {
            var token = other as RedisStreamSequenceToken;
            return token != null && SequenceNumber == token.SequenceNumber && EventIndex == token.EventIndex;
        }
    }
}
