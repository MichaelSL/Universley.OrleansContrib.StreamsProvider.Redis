namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    /// <summary>
    /// How published events are serialized into Redis. Receivers read entries in either format, whatever this is set to.
    /// </summary>
    public enum RedisStreamPayloadFormat
    {
        /// <summary>
        /// The Orleans serializer configured on the silo or client, the same one grain calls use. Events are delivered to
        /// every subscriber whose stream type they are assignable to, including base classes and interfaces. Silos on
        /// versions before this format existed cannot read these entries.
        /// </summary>
        OrleansCodec = 0,

        /// <summary>
        /// Legacy format: <c>System.Text.Json</c>, matched to subscribers by short type name. Use it only while silos on
        /// earlier versions still read the same streams.
        /// </summary>
        Json = 1,
    }
}
