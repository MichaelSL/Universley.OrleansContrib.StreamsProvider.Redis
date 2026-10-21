namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    /// <summary>How silos and clients publish events. Receivers read every payload format, whatever this is set to.</summary>
    public class RedisStreamPublisherOptions
    {
        private RedisStreamPayloadFormat _payloadFormat = RedisStreamPayloadFormat.OrleansCodec;

        /// <summary>How published events are serialized.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="RedisStreamPayloadFormat"/>.</exception>
        public RedisStreamPayloadFormat PayloadFormat
        {
            get => _payloadFormat;
            set => _payloadFormat = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown PayloadFormat");
        }
    }
}
