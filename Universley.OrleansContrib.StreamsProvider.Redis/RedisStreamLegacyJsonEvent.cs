using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    /// <summary>
    /// The event of an entry in the legacy <see cref="RedisStreamPayloadFormat.Json"/> format. JSON can only be
    /// deserialized once the subscriber's type is known, so it is kept as is and matched to subscribers by
    /// <see cref="TypeName"/>, the short name of the published type.
    /// </summary>
    [GenerateSerializer]
    [Alias("Universley.OrleansContrib.StreamsProvider.Redis.RedisStreamLegacyJsonEvent")]
    internal sealed record RedisStreamLegacyJsonEvent([property: Id(0)] string TypeName, [property: Id(1)] string Json) : IRedisStreamEvent
    {
        /// <summary>Deserializes the event for a subscriber to <typeparamref name="T"/>, if its short name matches.</summary>
        /// <exception cref="JsonException">The JSON cannot be deserialized into <typeparamref name="T"/>.</exception>
        public bool TryRead<T>([MaybeNullWhen(false)] out T @event)
        {
            if (TypeName != typeof(T).Name)
            {
                @event = default;
                return false;
            }
            @event = JsonSerializer.Deserialize<T>(Json)!;
            return true;
        }
    }
}
