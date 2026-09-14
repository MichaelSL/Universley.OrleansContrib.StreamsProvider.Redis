using Orleans.Streams;

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    /// <summary>
    /// Names this provider stores in Redis. Streams and consumer groups written by earlier versions use them too, so
    /// changing any of them breaks upgrades.
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
    }
}
