namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    /// <summary>
    /// A stream entry that can never be delivered, whoever reads it. The receiver acknowledges it without delivering it,
    /// so that it does not stay pending; any other error while reading an entry leaves it pending.
    /// </summary>
    internal sealed class RedisStreamUnreadableEntryException : Exception
    {
        public RedisStreamUnreadableEntryException(string message) : base(message)
        {
        }

        public RedisStreamUnreadableEntryException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
