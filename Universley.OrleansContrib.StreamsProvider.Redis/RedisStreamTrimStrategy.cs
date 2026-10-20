namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    /// <summary>How the receiver removes old entries from its Redis stream.</summary>
    public enum RedisStreamTrimStrategy
    {
        /// <summary>
        /// <see cref="AcknowledgedOnly"/> on Redis 6.2 or later. On older servers, falls back to <see cref="MaxLength"/>
        /// and logs a warning once.
        /// </summary>
        Auto = 0,

        /// <summary>
        /// Remove only entries that were delivered and acknowledged. Never drops an undelivered event, so the stream
        /// grows while consumers are behind; <see cref="RedisStreamReceiverOptions.BacklogWarningLength"/> sets when that
        /// is logged. Requires Redis 6.2 or later, and never falls back: on older servers every trim logs an error.
        /// </summary>
        AcknowledgedOnly = 1,

        /// <summary>
        /// Legacy behavior: cap the stream at roughly <see cref="RedisStreamReceiverOptions.MaxStreamLength"/> entries.
        /// Bounds memory, but deletes entries that were not delivered yet when consumers fall behind.
        /// </summary>
        MaxLength = 2,
    }
}
