using Microsoft.Extensions.Logging;
using Moq;

namespace RedisStreamsProvider.UnitTests
{
    internal static class LoggerMockExtensions
    {
        /// <summary>
        /// Verifies how often <paramref name="logger"/> logged at <paramref name="level"/> a message containing
        /// <paramref name="text"/>, with an exception matching <paramref name="exception"/> if one is given.
        /// </summary>
        public static void VerifyLogged<T>(this Mock<ILogger<T>> logger, LogLevel level, string text, Times times,
            Func<Exception?, bool>? exception = null) =>
            logger.Verify(l => l.Log(
                    level,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(text)),
                    It.Is<Exception?>(e => exception == null || exception(e)),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                times);
    }
}
