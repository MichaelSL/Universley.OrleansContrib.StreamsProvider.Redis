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
            [System.Diagnostics.CodeAnalysis.DoesNotReturn]  static void ThrowArgumentException() => throw new ArgumentException(message: $"Invalid {nameof(id)}", paramName: nameof(id));
            var redisValueId = id.ToString();
            
            var splitIndex = redisValueId.IndexOf('-');  
            if (splitIndex < 0) 
                ThrowArgumentException();  
            SequenceNumber = long.Parse(redisValueId.AsSpan(0, splitIndex));  
            EventIndex = int.Parse(redisValueId.AsSpan(splitIndex+1));
        }
        public RedisStreamSequenceToken(long sequenceNumber, int eventIndex)
        {
            SequenceNumber = sequenceNumber;
            EventIndex = eventIndex;
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
