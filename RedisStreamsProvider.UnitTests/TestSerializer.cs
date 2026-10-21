using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace RedisStreamsProvider.UnitTests
{
    internal static class TestSerializer
    {
        /// <summary>A serializer that knows the event types in this assembly, like the one Orleans gives a silo.</summary>
        public static Serializer Instance { get; } = new ServiceCollection()
            .AddSerializer(serializer => serializer.AddAssembly(typeof(TestSerializer).Assembly))
            .BuildServiceProvider()
            .GetRequiredService<Serializer>();
    }
}
