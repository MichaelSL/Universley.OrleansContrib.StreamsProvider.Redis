namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    /// <summary>How silos and clients publish events. Receivers read every payload format, whatever this is set to.</summary>
    public class RedisStreamPublisherOptions
    {
        /// <summary>How published events are serialized. An undefined value fails when the provider starts.</summary>
        public RedisStreamPayloadFormat PayloadFormat { get; set; } = RedisStreamPayloadFormat.OrleansCodec;
    }
}
