using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace RedisStreamsProvider.UnitTests
{
    public class MinIdTrimSupportTests
    {
        private readonly Mock<ILogger<MinIdTrimSupportTests>> _mockLogger = new();

        private static Mock<IServer> Server(string? version, bool isReplica = false, bool isConnected = true)
        {
            var server = new Mock<IServer>();
            server.Setup(s => s.IsConnected).Returns(isConnected);
            server.Setup(s => s.IsReplica).Returns(isReplica);
            var info = "# Server\r\n" + (version is null ? "" : $"redis_version:{version}\r\n") + "redis_mode:standalone\r\n";
            server.Setup(s => s.ExecuteAsync("INFO", It.IsAny<object[]>())).ReturnsAsync(RedisResult.Create((RedisValue)info));
            return server;
        }

        private async Task<MinIdTrimSupport> DetectAsync(params Mock<IServer>[] servers)
        {
            var multiplexer = new Mock<IConnectionMultiplexer>();
            multiplexer.Setup(m => m.GetServers()).Returns(servers.Select(s => s.Object).ToArray());
            var support = new MinIdTrimSupport();
            await support.DetectAsync(multiplexer.Object, _mockLogger.Object);
            return support;
        }

        [Theory]
        [InlineData("6.0.20", false)]
        [InlineData("6.2.0", true)]
        [InlineData("7.4.1", true)]
        [InlineData(null, true)]            // INFO without a version: undecided, so the first trim decides
        [InlineData("unstable", true)]
        public async Task DetectAsync_DecidesByTheReportedVersion(string? version, bool expected)
        {
            var support = await DetectAsync(Server(version));

            Assert.Equal(expected, support.IsSupported);
        }

        [Fact]
        public async Task DetectAsync_IgnoresReplicasAndDisconnectedServers()
        {
            var support = await DetectAsync(Server("7.4.1"), Server("6.0.20", isReplica: true), Server("6.0.20", isConnected: false));

            Assert.True(support.IsSupported);
        }

        [Fact]
        public async Task DetectAsync_FindsAnOldPrimaryAmongCurrentOnes()
        {
            var support = await DetectAsync(Server("7.4.1"), Server("6.0.20"));

            Assert.False(support.IsSupported);
            _mockLogger.VerifyLogged(LogLevel.Warning, "falls back to MaxLength", Times.Once());
        }

        [Fact]
        public async Task DetectAsync_LeavesMinIdSupported_WhenInfoFails()
        {
            // Some proxies and managed services disable INFO.
            var server = Server("6.0.20");
            server.Setup(s => s.ExecuteAsync("INFO", It.IsAny<object[]>())).ThrowsAsync(new RedisServerException("ERR unknown command 'INFO'"));

            var support = await DetectAsync(server);

            Assert.True(support.IsSupported);
        }

        [Fact]
        public void MarkUnsupported_WarnsOnlyTheFirstTime()
        {
            var support = new MinIdTrimSupport();

            support.MarkUnsupported(_mockLogger.Object);
            support.MarkUnsupported(_mockLogger.Object);

            Assert.False(support.IsSupported);
            _mockLogger.VerifyLogged(LogLevel.Warning, "falls back to MaxLength", Times.Once());
        }
    }
}
