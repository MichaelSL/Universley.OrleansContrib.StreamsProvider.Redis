using System.ComponentModel.DataAnnotations;

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    public class RedisStreamReceiverOptions
    {
        /// <summary>
        /// Roughly how many entries are kept after each trim. Used by <see cref="RedisStreamTrimStrategy.MaxLength"/>, and by
        /// <see cref="RedisStreamTrimStrategy.Auto"/> on Redis older than 6.2.
        /// </summary>
        [Range(1, int.MaxValue)]
        public int MaxStreamLength { get; set; } = 1000;

        /// <summary>
        /// A warning is logged when more entries than this are still in the stream after trimming, which means consumers
        /// are falling behind. Used by <see cref="RedisStreamTrimStrategy.AcknowledgedOnly"/>, and by
        /// <see cref="RedisStreamTrimStrategy.Auto"/> on Redis 6.2 or later.
        /// </summary>
        [Range(1, int.MaxValue)]
        public int BacklogWarningLength { get; set; } = 1000;

        /// <summary>Minutes between trim operations.</summary>
        [Range(1, int.MaxValue)]
        public int TrimTimeMinutes { get; set; } = 5;

        /// <summary>How entries are removed from the stream.</summary>
        public RedisStreamTrimStrategy TrimStrategy { get; set; } = RedisStreamTrimStrategy.Auto;
    }
}
