using System.Diagnostics.CodeAnalysis;

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    /// <summary>The event of a stream entry, as read by <see cref="RedisStreamCodec"/> in one of the payload formats.</summary>
    internal interface IRedisStreamEvent
    {
        /// <summary>The event, if it is one a subscriber to <typeparamref name="T"/> receives.</summary>
        bool TryRead<T>([MaybeNullWhen(false)] out T @event);
    }
}
