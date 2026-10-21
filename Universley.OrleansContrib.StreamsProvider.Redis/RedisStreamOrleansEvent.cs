using System.Diagnostics.CodeAnalysis;

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    /// <summary>
    /// The event of an entry in <see cref="RedisStreamPayloadFormat.OrleansCodec"/>, which is also the payload stored in
    /// Redis: its alias and field ids are part of the wire format, and new fields can be added with new ids.
    /// </summary>
    [GenerateSerializer]
    [Alias("Universley.OrleansContrib.StreamsProvider.Redis.RedisStreamOrleansEvent")]
    internal sealed class RedisStreamOrleansEvent : IRedisStreamEvent
    {
        public RedisStreamOrleansEvent(object @event)
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
