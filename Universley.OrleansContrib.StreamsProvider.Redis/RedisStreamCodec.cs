using System.Diagnostics;
using System.Text.Json;
using Orleans.Serialization;
using StackExchange.Redis;
using static Universley.OrleansContrib.StreamsProvider.Redis.RedisStreamWireFormat;

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    /// <summary>
    /// Converts events to and from the stream entries described by <see cref="RedisStreamWireFormat"/>. It writes in
    /// the payload format it was created with, and reads both.
    /// </summary>
    internal sealed class RedisStreamCodec
    {
        private readonly Serializer _serializer;
        private readonly RedisStreamPayloadFormat _format;

        /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a defined <see cref="RedisStreamPayloadFormat"/>.</exception>
        public RedisStreamCodec(Serializer serializer, RedisStreamPayloadFormat format)
        {
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _format = Enum.IsDefined(format) ? format : throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown PayloadFormat");
        }

        /// <summary>The fields of the stream entry that stores <paramref name="event"/>.</summary>
        /// <exception cref="CodecNotFoundException">The Orleans serializer cannot serialize the event's type.</exception>
        public NameValueEntry[] Encode<T>(StreamId streamId, T @event) =>
        [
            new(StreamNamespaceField, streamId.Namespace),
            new(StreamKeyField, streamId.Key),
            // Entries in OrleansCodec do not need the event type to be read back, but it makes them readable with
            // redis-cli. It also makes silos on earlier versions fail delivery of an entry they cannot read, instead of
            // skipping it as unreadable.
            new(EventTypeField, @event!.GetType().Name),
            .. EncodePayload(@event),
        ];

        private NameValueEntry[] EncodePayload<T>(T @event) => _format switch
        {
            RedisStreamPayloadFormat.OrleansCodec =>
            [
                new(FormatField, OrleansCodecFormat),
                new(DataField, _serializer.SerializeToArray(new RedisStreamOrleansEvent(@event!))),
            ],
            RedisStreamPayloadFormat.Json => [new(DataField, JsonSerializer.Serialize(@event))],
            _ => throw new UnreachableException(),
        };

        /// <summary>Reads an entry written by <see cref="Encode"/> in either format.</summary>
        /// <exception cref="RedisStreamUnreadableEntryException">
        /// The entry has a missing field or a malformed id, such as one deleted while still pending, which comes back
        /// with no fields; it is in a format this version does not know; or the serializer cannot read its
        /// <see cref="RedisStreamPayloadFormat.OrleansCodec"/> payload, which includes an event type this silo does
        /// not have.
        /// </exception>
        public RedisStreamBatchContainer Decode(StreamEntry entry)
        {
            if (!RedisStreamSequenceToken.TryParse(entry.Id, out var sequenceNumber, out var eventIndex))
            {
                throw new RedisStreamUnreadableEntryException($"Stream entry id {entry.Id} is malformed");
            }

            var streamId = StreamId.Create(Text(entry, StreamNamespaceField), Text(entry, StreamKeyField));
            IRedisStreamEvent @event = (string?)entry[FormatField] switch
            {
                null => new RedisStreamLegacyJsonEvent(Text(entry, EventTypeField), Text(entry, DataField)),
                OrleansCodecFormat => DeserializeOrleansEvent(entry),
                // Written by a later version; reading it as JSON would fault the subscription at delivery.
                var format => throw new RedisStreamUnreadableEntryException($"Stream entry {entry.Id} has unknown format {format}"),
            };
            return new RedisStreamBatchContainer(streamId, new RedisStreamSequenceToken(sequenceNumber, eventIndex), @event, entry.Id.ToString());
        }

        private RedisStreamOrleansEvent DeserializeOrleansEvent(StreamEntry entry)
        {
            ReadOnlyMemory<byte> data = Field(entry, DataField);
            try
            {
                return _serializer.Deserialize<RedisStreamOrleansEvent>(data);
            }
            catch (Exception error)
            {
                // The bytes come from Redis, so any failure to read them is a property of the entry.
                throw new RedisStreamUnreadableEntryException($"Stream entry {entry.Id} has a payload the Orleans serializer cannot read", error);
            }
        }

        private static RedisValue Field(StreamEntry entry, string name) =>
            entry[name] is { IsNullOrEmpty: false } value ? value : throw MissingField(entry, name);

        private static string Text(StreamEntry entry, string name) =>
            Field(entry, name).ToString() is var text && !string.IsNullOrWhiteSpace(text) ? text : throw MissingField(entry, name);

        private static RedisStreamUnreadableEntryException MissingField(StreamEntry entry, string name) =>
            new($"Stream entry {entry.Id} has no {name} field");
    }
}
