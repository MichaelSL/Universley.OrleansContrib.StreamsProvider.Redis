using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    /// <summary>
    /// Whether the Redis server accepts <c>XTRIM MINID</c>, which <see cref="RedisStreamTrimStrategy.Auto"/> needs to trim
    /// only acknowledged entries. One instance is shared by every receiver of an adapter, so the fallback to
    /// <see cref="RedisStreamTrimStrategy.MaxLength"/> is decided, and logged, once.
    /// </summary>
    internal sealed class MinIdTrimSupport
    {
        private static readonly Version MinimumVersion = new(6, 2);

        private int _unsupported;

        public bool IsSupported => Volatile.Read(ref _unsupported) == 0;

        /// <summary>
        /// Marks <c>XTRIM MINID</c> as unsupported if a connected primary reports a version below 6.2. Asks each server
        /// with <c>INFO</c> rather than reading <see cref="IServer.Version"/>, which falls back to a configured default
        /// when <c>INFO</c> is unavailable and could make a current server look old. Sends it as a raw command because
        /// <see cref="IServer.InfoAsync"/> needs admin mode. When no answer is conclusive, the first trim decides instead.
        /// </summary>
        public async Task DetectAsync(IConnectionMultiplexer multiplexer, ILogger logger)
        {
            try
            {
                foreach (var server in multiplexer.GetServers().Where(s => s.IsConnected && !s.IsReplica))
                {
                    var info = (string?)await server.ExecuteAsync("INFO", "server");
                    if (TryGetVersion(info, out var version) && version < MinimumVersion)
                    {
                        MarkUnsupported(logger);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read the Redis server version; the first trim decides whether XTRIM MINID is supported");
            }
        }

        private static bool TryGetVersion(string? info, out Version version)
        {
            const string Field = "redis_version:";
            var line = info?.Split('\n').FirstOrDefault(l => l.StartsWith(Field, StringComparison.Ordinal));
            return Version.TryParse(line?[Field.Length..].Trim(), out version!);
        }

        /// <summary>Records that <c>XTRIM MINID</c> is unsupported, logging a warning the first time.</summary>
        public void MarkUnsupported(ILogger logger, Exception? cause = null)
        {
            if (Interlocked.Exchange(ref _unsupported, 1) == 0)
            {
                logger.LogWarning(cause,
                    "Redis does not support XTRIM MINID, which needs Redis 6.2 or later. The Auto trim strategy falls back to MaxLength, " +
                    "which deletes entries that were not delivered yet when consumers fall behind. Upgrade Redis, or set " +
                    "RedisStreamReceiverOptions.TrimStrategy explicitly");
            }
        }
    }
}
