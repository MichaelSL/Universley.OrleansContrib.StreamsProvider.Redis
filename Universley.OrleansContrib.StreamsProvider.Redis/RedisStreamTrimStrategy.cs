namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    /// <summary>How the receiver removes old entries from its Redis stream.</summary>
    public enum RedisStreamTrimStrategy
    {
        /// <summary>
        /// Remove only entries that were delivered and acknowledged. Never drops an undelivered event, so the stream
        /// grows while consumers are behind. Requires Redis 6.2 or later.
        /// </summary>
        AcknowledgedOnly = 0,

        /// <summary>
        /// Legacy behavior: cap the stream at roughly <see cref="RedisStreamReceiverOptions.MaxStreamLength"/> entries.
        /// Bounds memory, but deletes entries that were not delivered yet when consumers fall behind.
        /// </summary>
        MaxLength = 1,
    }
}
