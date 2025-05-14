namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    public class RedisStreamReceiverOptions
    {
        public int MaxStreamLength { get; set; } = 1000; // Default value from previous const
        public int TrimTimeMinutes { get; set; } = 5;   // Default value from previous const
    }
}
