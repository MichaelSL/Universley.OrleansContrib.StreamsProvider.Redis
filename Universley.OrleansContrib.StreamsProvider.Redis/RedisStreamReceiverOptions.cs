using System.ComponentModel.DataAnnotations;

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    public class RedisStreamReceiverOptions
    {
        [Range(1, int.MaxValue)]
        public int MaxStreamLength { get; set; } = 1000;

        [Range(1, int.MaxValue)]
        public int TrimTimeMinutes { get; set; } = 5;
    }
}
