using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Orleans.Streams;
using StackExchange.Redis;

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    /// <summary>
    /// How this provider stores events in Redis. Streams and consumer groups written by earlier versions use the same
    /// format, so changing any of it breaks upgrades.
    /// </summary>
    internal static class RedisStreamWireFormat
    {
        public const string StreamNamespaceField = "streamNamespace";
        public const string StreamKeyField = "streamKey";
        public const string EventTypeField = "eventType";
        public const string DataField = "data";

        /// <summary>
        /// The one consumer group on every stream. Its only consumer is named after the stream key: Orleans gives each
        /// queue to one silo at a time, so whichever silo owns the queue reads as the same consumer and picks up entries
        /// a previous owner read but never acknowledged.
        /// </summary>
        public const string GroupName = "consumer";

        /// <summary>The Redis key of a queue's stream, which is also the name of the group's consumer.</summary>
        public static string StreamKey(QueueId queueId) => queueId.ToString();

        /// <summary>The fields of the stream entry that stores <paramref name="event"/>.</summary>
        public static NameValueEntry[] Encode<T>(StreamId streamId, T @event) =>
        [
            new(StreamNamespaceField, streamId.Namespace),
            new(StreamKeyField, streamId.Key),
            new(EventTypeField, @event!.GetType().Name),
            new(DataField, JsonSerializer.Serialize(@event)),
        ];

        /// <summary>
        /// Reads an entry written by <see cref="Encode"/>. Fails for an entry with a missing field or a malformed id,
        /// such as one deleted while still pending, which comes back with no fields.
        /// </summary>
        public static bool TryDecode(StreamEntry entry, [NotNullWhen(true)] out RedisStreamBatchContainer? batch)
        {
            var streamNamespace = (string?)entry[StreamNamespaceField];
            var streamKey = (string?)entry[StreamKeyField];
            var eventType = (string?)entry[EventTypeField];
            var data = (string?)entry[DataField];
            if (string.IsNullOrWhiteSpace(streamNamespace) || string.IsNullOrWhiteSpace(streamKey)
                || string.IsNullOrWhiteSpace(eventType) || string.IsNullOrWhiteSpace(data)
                || !RedisStreamSequenceToken.TryParse(entry.Id, out var sequenceNumber, out var eventIndex))
            {
                batch = null;
                return false;
            }

            batch = new RedisStreamBatchContainer(
                StreamId.Create(streamNamespace, streamKey),
                new RedisStreamSequenceToken(sequenceNumber, eventIndex),
                eventType,
                data,
                entry.Id.ToString());
            return true;
        }
    }
}
