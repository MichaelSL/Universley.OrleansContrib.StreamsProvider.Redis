using System.Diagnostics.CodeAnalysis;

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    /// <summary>
    /// The payload of an entry in <see cref="RedisStreamPayloadFormat.OrleansCodec"/>. It is stored in Redis, so its alias
    /// and field ids are part of the wire format; new fields can be added with new ids.
    /// </summary>
    [GenerateSerializer]
    [Alias("Universley.OrleansContrib.StreamsProvider.Redis.RedisStreamEventBody")]
    internal sealed class RedisStreamEventBody : IRedisStreamEvent
    {
        public RedisStreamEventBody(object @event)
        {
            Event = @event;
        }

        [Id(0)]
        public object Event { get; }

        /// <summary>The event, for a subscriber to any type it is assignable to.</summary>
        public bool TryRead<T>([MaybeNullWhen(false)] out T @event)
        {
            if (Event is T read)
            {
                @event = read;
                return true;
            }
            @event = default;
            return false;
        }
    }
}
