using System.ComponentModel.DataAnnotations;

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    public class RedisStreamReceiverOptions
    {
        /// <summary>
        /// With <see cref="RedisStreamTrimStrategy.MaxLength"/>: roughly how many entries are kept after each trim.
        /// With <see cref="RedisStreamTrimStrategy.AcknowledgedOnly"/>: a warning is logged when more entries than this
        /// are still in the stream after trimming.
        /// </summary>
        [Range(1, int.MaxValue)]
        public int MaxStreamLength { get; set; } = 1000;

        /// <summary>Minutes between trim operations.</summary>
        [Range(1, int.MaxValue)]
        public int TrimTimeMinutes { get; set; } = 5;

        /// <summary>How entries are removed from the stream.</summary>
        public RedisStreamTrimStrategy TrimStrategy { get; set; } = RedisStreamTrimStrategy.AcknowledgedOnly;
    }
}
