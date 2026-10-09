using System.Diagnostics;

namespace RedisStreamsProvider.IntegrationTests;

internal static class Eventually
{
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException($"Condition was not met within {timeout}.");
            }

            await Task.Delay(100);
        }
    }
}
