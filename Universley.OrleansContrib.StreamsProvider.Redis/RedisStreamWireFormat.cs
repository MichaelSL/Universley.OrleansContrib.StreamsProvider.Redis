using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Orleans.Serialization;
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
        /// Present only on entries in <see cref="RedisStreamPayloadFormat.OrleansCodec"/>, with the value
        /// <see cref="OrleansCodecFormat"/>. Entries without it are in the legacy JSON format.
        /// </summary>
        public const string FormatField = "format";
        public const string OrleansCodecFormat = "orleans";

        /// <summary>
        /// The one consumer group on every stream. Its only consumer is named after the stream key: Orleans gives each
        /// queue to one silo at a time, so whichever silo owns the queue reads as the same consumer and picks up entries
        /// a previous owner read but never acknowledged.
        /// </summary>
        public const string GroupName = "consumer";

        /// <summary>The Redis key of a queue's stream, which is also the name of the group's consumer.</summary>
        public static string StreamKey(QueueId queueId) => queueId.ToString();

        /// <summary>The fields of the stream entry that stores <paramref name="event"/>.</summary>
        /// <exception cref="CodecNotFoundException">The Orleans serializer cannot serialize the event's type.</exception>
        public static NameValueEntry[] Encode<T>(StreamId streamId, T @event, RedisStreamPayloadFormat format, Serializer serializer) => format switch
        {
            // The event type is not needed to read the entry back, but makes it readable with redis-cli. It also makes
            // silos on earlier versions fail delivery of an entry they cannot read, instead of skipping it as unreadable.
            RedisStreamPayloadFormat.OrleansCodec =>
            [
                new(StreamNamespaceField, streamId.Namespace),
                new(StreamKeyField, streamId.Key),
                new(FormatField, OrleansCodecFormat),
                new(EventTypeField, @event!.GetType().Name),
                new(DataField, serializer.SerializeToArray(new RedisStreamEventBody(@event))),
            ],
            RedisStreamPayloadFormat.Json =>
            [
                new(StreamNamespaceField, streamId.Namespace),
                new(StreamKeyField, streamId.Key),
                new(EventTypeField, @event!.GetType().Name),
                new(DataField, JsonSerializer.Serialize(@event)),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };

        /// <summary>
        /// Reads an entry written by <see cref="Encode"/> in either format. Fails for an entry with a missing field or a
        /// malformed id, such as one deleted while still pending, which comes back with no fields, and for an
        /// <see cref="RedisStreamPayloadFormat.OrleansCodec"/> payload the serializer cannot read, which sets
        /// <paramref name="error"/>.
        /// </summary>
        public static bool TryDecode(StreamEntry entry, Serializer serializer, [NotNullWhen(true)] out RedisStreamBatchContainer? batch, out Exception? error)
        {
            batch = null;
            error = null;
            var streamNamespace = (string?)entry[StreamNamespaceField];
            var streamKey = (string?)entry[StreamKeyField];
            var data = entry[DataField];
            if (string.IsNullOrWhiteSpace(streamNamespace) || string.IsNullOrWhiteSpace(streamKey) || data.IsNullOrEmpty
                || !RedisStreamSequenceToken.TryParse(entry.Id, out var sequenceNumber, out var eventIndex))
            {
                return false;
            }

            var streamId = StreamId.Create(streamNamespace, streamKey);
            var sequenceToken = new RedisStreamSequenceToken(sequenceNumber, eventIndex);
            if ((string?)entry[FormatField] == OrleansCodecFormat)
            {
                object @event;
                try
                {
                    @event = serializer.Deserialize<RedisStreamEventBody>((byte[])data!).Event;
                }
                catch (Exception ex)
                {
                    error = ex;
                    return false;
                }

                batch = new RedisStreamBatchContainer(streamId, sequenceToken, @event, entry.Id.ToString());
                return true;
            }

            var eventType = (string?)entry[EventTypeField];
            var json = (string?)data;
            if (string.IsNullOrWhiteSpace(eventType) || string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            batch = new RedisStreamBatchContainer(streamId, sequenceToken, eventType, json, entry.Id.ToString());
            return true;
        }
    }
}
