using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Orleans.Serialization;
using StackExchange.Redis;
using static Universley.OrleansContrib.StreamsProvider.Redis.RedisStreamWireFormat;

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    /// <summary>Converts events to and from the stream entries described by <see cref="RedisStreamWireFormat"/>.</summary>
    internal sealed class RedisStreamCodec
    {
        private readonly Serializer _serializer;
        private readonly RedisStreamPayloadFormat _format;

        /// <param name="format">How <see cref="Encode"/> writes events. <see cref="TryDecode"/> reads both formats.</param>
        public RedisStreamCodec(Serializer serializer, RedisStreamPayloadFormat format)
        {
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _format = Enum.IsDefined(format) ? format : throw new ArgumentOutOfRangeException(nameof(format), format, null);
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
                new(DataField, _serializer.SerializeToArray(new RedisStreamEventBody(@event!))),
            ],
            _ => [new(DataField, JsonSerializer.Serialize(@event))],
        };

        /// <summary>
        /// Reads an entry written by <see cref="Encode"/> in either format. Fails, with <paramref name="error"/> saying
        /// why, for an entry with a missing field or a malformed id, such as one deleted while still pending, which
        /// comes back with no fields, and for an <see cref="RedisStreamPayloadFormat.OrleansCodec"/> payload the
        /// serializer cannot read.
        /// </summary>
        public bool TryDecode(StreamEntry entry, [NotNullWhen(true)] out RedisStreamBatchContainer? batch, [NotNullWhen(false)] out Exception? error)
        {
            try
            {
                batch = Decode(entry);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                batch = null;
                error = ex;
                return false;
            }
        }

        private RedisStreamBatchContainer Decode(StreamEntry entry)
        {
            if (!RedisStreamSequenceToken.TryParse(entry.Id, out var sequenceNumber, out var eventIndex))
            {
                throw new FormatException($"Stream entry id {entry.Id} is malformed");
            }

            var streamId = StreamId.Create(Text(entry, StreamNamespaceField), Text(entry, StreamKeyField));
            object @event = (string?)entry[FormatField] == OrleansCodecFormat
                ? _serializer.Deserialize<RedisStreamEventBody>((byte[])Field(entry, DataField)!).Event
                : new RedisStreamLegacyJsonEvent(Text(entry, EventTypeField), Text(entry, DataField));
            return new RedisStreamBatchContainer(streamId, new RedisStreamSequenceToken(sequenceNumber, eventIndex), @event, entry.Id.ToString());
        }

        private static RedisValue Field(StreamEntry entry, string name) =>
            entry[name] is { IsNullOrEmpty: false } value ? value : throw MissingField(entry, name);

        private static string Text(StreamEntry entry, string name) =>
            (string?)Field(entry, name) is { } text && !string.IsNullOrWhiteSpace(text) ? text : throw MissingField(entry, name);

        private static FormatException MissingField(StreamEntry entry, string name) =>
            new($"Stream entry {entry.Id} has no {name} field");
    }
}
