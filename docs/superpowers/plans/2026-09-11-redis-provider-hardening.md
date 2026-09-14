# Redis Streams Provider Production Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `Universley.OrleansContrib.StreamsProvider.Redis` safe to run in production until `Microsoft.Orleans.Streaming.Redis` (currently `10.3.1-alpha.1`) reaches beta, then migrate off it.

**Architecture:** Keep the existing design: one Redis stream per Orleans queue, read through a single consumer group `consumer` whose consumer name is the queue id (so whichever silo owns the queue reads as the same consumer), JSON payloads. Fix the ways it can lose or stall messages today. Also add the two test layers it lacks: receiver-level integration tests against a real Redis (Testcontainers) and end-to-end tests through an Orleans `TestCluster`. No re-architecture and no wire-format change.

**Tech Stack:** .NET 10 SDK, C# (library multi-targets `net8.0;net10.0`), Microsoft Orleans 10.0.1, StackExchange.Redis 2.11.3, xunit 2.9.3, Moq 4.20.72, Testcontainers.Redis 4.15.0, Microsoft.Orleans.TestingHost 10.0.1, NUKE build, GitHub Actions.

**Spec:** No separate spec document. The requirements are the findings of the 2026-09-11 review, summarized in **Background** below.

## Global Constraints

- Library target frameworks stay `net8.0;net10.0`. Test projects target `net10.0`.
- Library package references stay as they are: `Microsoft.Orleans.Streaming` 10.0.1, `Microsoft.Orleans.Sdk` 10.0.1, `StackExchange.Redis` 2.11.3. No StackExchange.Redis 3.x.
- The Redis wire format must not change:
  - Stream key: `QueueId.ToString()`.
  - Entry fields: `streamNamespace`, `streamKey`, `eventType`, `data`.
  - Consumer group: `consumer`. Consumer name: the queue's `QueueId.ToString()` (same value as the stream key), exactly as the original code used.
  - Old and new silos must be able to run side by side during a rolling deploy.
- Minimum Redis server is 6.2, because `XTRIM MINID` needs it.
  - Do not use Redis 8.x-only commands or options: `ACKED`, `KEEPREF`, `DELREF`, `XACKDEL`, `XDELEX`, `IDMP`, `XNACK`.
  - Integration tests run against the `redis:7.4` image.
- Public API: do not remove any public member or change its signature. Adding public members is allowed.
- No new build warnings in library files you touch.
- Unit tests (`RedisStreamsProvider.UnitTests`) must not need Docker. Everything that needs a real Redis goes in `RedisStreamsProvider.IntegrationTests`.
- Local integration runs on this machine use rootless Podman. Export these first:
  `export DOCKER_HOST=unix:///run/user/1000/podman/podman.sock TESTCONTAINERS_RYUK_DISABLED=true`
  (CI uses the runner's Docker and needs neither.)
- Use conventional commit prefixes (`feat:`, `fix:`, `test:`, `chore:`, `docs:`), as the repo already does. Work on a feature branch, never on `main`.
- Redis literals: the "new messages" position for `XREADGROUP` is the literal `">"`. Do **not** use `StackExchange.Redis.StreamPosition.NewMessages`, whose value is `"$"`.

## Background (review findings this plan addresses)

| # | Finding | Where | Task |
|---|---|---|---|
| F1 | CI installs .NET 9 while every project targets `net10.0`. There is no `global.json`. | `.github/workflows/ci-pipeline.yml` | 1 |
| F2 | Only mocked unit tests exist. Nothing exercises a real Redis or a real Orleans cluster. | test projects | 2, 3, 10 |
| F3 | A failed publish is logged and swallowed, so the producer's `OnNextAsync` "succeeds" and the event is lost. | `RedisStreamAdapter.cs:59-62` | 4 |
| F4 | One malformed entry, or one deleted while pending, throws inside the batch `Select`. The whole read returns `null`. Every entry in that read stays pending forever and is never delivered. | `RedisStreamReceiver.cs:51` | 5 |
| F5 | On startup the receiver reads pending entries (`"0"`) only once, up to `maxCount`, then switches to `">"`. Any further pending entries are never redelivered. | `RedisStreamReceiver.cs:48-50` | 6 |
| F6 | If the stream key disappears (Redis restart without persistence, eviction, failover), every read fails with `NOGROUP` forever, because the group is only created in `Initialize`. | `RedisStreamReceiver.cs:44-67, 87-99` | 7 |
| F7 | The group is created at `"$"`, so entries published before a queue's first receiver started are skipped. | `RedisStreamReceiver.cs:91` | 7 |
| F8 | `BUSYGROUP` is detected by matching the message text `"name already exists"`. | `RedisStreamReceiver.cs:94` | 7 |
| F9 | One `XACK` round-trip per message. | `RedisStreamReceiver.cs:105-113` | 8 |
| F10 | Trimming is `MAXLEN ~ MaxStreamLength` whether or not entries were acknowledged. A consumer that falls behind silently loses events. | `RedisStreamReceiver.cs:76` | 9 |
| F11 | Delivery semantics and limitations are not documented. | `README.md` | 11 |

### Deliberately out of scope

Each item below was considered and left out, with the reason:

- **Matching events by namespace-qualified type name.** It would break setups where producer and consumer have differently namespaced DTOs with the same class name, which work today. Documented as a limitation instead (Task 11).
- **Base-type/interface subscriptions, `RequestContext` propagation, rewindable streams.** New features, not hardening. The Microsoft provider already has them.
- **Prefixing keys with `ServiceId`.** It changes stream keys and would orphan in-flight data.
- **Bumping Orleans, moving to StackExchange.Redis 3.x, adding `net11.0`.** Revisit at .NET 11 GA (Nov 2026). The migration target is the Microsoft provider anyway.
- **Redis 8.2 `ACKED` trimming / `XACKDEL`.** Needs Redis ≥ 8.2. Task 9 gets the same safety on Redis 6.2+.
- **Metrics/OpenTelemetry and `[LoggerMessage]` source generation.**

## File Structure

```
global.json                                                   (Task 1) pins the SDK band
.github/workflows/ci-pipeline.yml                             (Task 1) installs the right SDK
build/Build.cs                                                (Task 2) per-project TRX results
RedisStreamsInOrleans.sln                                     (Task 2) adds integration project
Universley.OrleansContrib.StreamsProvider.Redis/
  RedisStreamAdapter.cs                                       (Task 4) rethrow publish failures
  RedisStreamReceiver.cs                                      (Tasks 5-9) read/ack/trim logic
  RedisStreamReceiverOptions.cs                               (Task 9) TrimStrategy option
  RedisStreamTrimStrategy.cs                                  (Task 9) new enum
  Universley.OrleansContrib.StreamsProvider.Redis.csproj      (Task 9) InternalsVisibleTo
RedisStreamsProvider.UnitTests/
  RedisStreamAdapterTests.cs                                  (Task 4)
  RedisStreamReceiverTests.cs                                 (Tasks 5-8)
  RedisStreamReceiverTrimTests.cs                             (Task 9)
RedisStreamsProvider.IntegrationTests/                        (new, Task 2)
  RedisStreamsProvider.IntegrationTests.csproj
  RedisFixture.cs            one Redis container shared by receiver-level tests
  ProviderHarness.cs         builds adapter + receiver on a unique stream key per test
  TestEvent.cs               JSON payload type used by tests
  Eventually.cs              polling helper for asynchronous delivery
  ReceiverTests.cs           receiver-level tests (grows in Tasks 2, 5, 6, 7, 8)
  TrimmingTests.cs           (Task 9)
  Cluster/ClusterFixture.cs  (Task 3) Redis container + 2-silo TestCluster
  Cluster/EventCollectorGrain.cs (Task 3) implicit-subscription grain + static recorder
  Cluster/StreamDeliveryTests.cs (Task 3)
  Cluster/SiloFailoverTests.cs   (Task 10)
README.md                                                     (Tasks 9, 11)
```

---

### Task 1: Pin the SDK and fix CI

**Files:**
- Create: `global.json`
- Modify: `.github/workflows/ci-pipeline.yml:32-35`

**Interfaces:**
- Consumes: nothing.
- Produces: the build uses a .NET 10 SDK (≥ 10.0.100) both locally and in CI.

- [ ] **Step 1: Create a feature branch**

```bash
git checkout -b hardening/production-readiness
```

- [ ] **Step 2: Create `global.json`**

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature"
  }
}
```

- [ ] **Step 3: Point CI at the .NET 10 SDK**

In `.github/workflows/ci-pipeline.yml`, replace:

```yaml
      - name: Setup .NET 9
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 9.x
```

with:

```yaml
      - name: Setup .NET 10
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
```

- [ ] **Step 4: Verify locally**

Run: `dotnet --version`
Expected: a `10.0.x` version, for example `10.0.401`.

Run: `./build.sh Test`
Expected: the NUKE build ends with `Build succeeded` and the test summary shows `Passed: 36`.

- [ ] **Step 5: Commit**

```bash
git add global.json .github/workflows/ci-pipeline.yml
git commit -m "chore: pin .NET 10 SDK and install it in CI"
```

---

### Task 2: Integration test project with a real Redis

**Files:**
- Create: `RedisStreamsProvider.IntegrationTests/RedisStreamsProvider.IntegrationTests.csproj`
- Create: `RedisStreamsProvider.IntegrationTests/RedisFixture.cs`
- Create: `RedisStreamsProvider.IntegrationTests/ProviderHarness.cs`
- Create: `RedisStreamsProvider.IntegrationTests/TestEvent.cs`
- Create: `RedisStreamsProvider.IntegrationTests/Eventually.cs`
- Create: `RedisStreamsProvider.IntegrationTests/ReceiverTests.cs`
- Modify: `RedisStreamsInOrleans.sln` (via `dotnet sln add`)
- Modify: `build/Build.cs:81-89` (Test target)

**Interfaces:**
- Consumes: public types `RedisStreamAdapter`, `RedisStreamReceiver`, `RedisStreamReceiverOptions`, `RedisStreamBatchContainer`.
- Produces (used by Tasks 5–9):
  - `RedisFixture` (xunit collection fixture) with `IConnectionMultiplexer Connection`.
  - `RedisCollection.Name` = `"Redis"`.
  - `ProviderHarness(IConnectionMultiplexer connection, RedisStreamReceiverOptions? receiverOptions = null)`, exposing:
    - `IDatabase Database`
    - `RedisStreamReceiverOptions ReceiverOptions`
    - `QueueId QueueId`
    - `RedisKey Key`
    - `RedisStreamAdapter Adapter`
    - `StreamId StreamId`
    - `RedisStreamReceiver CreateReceiver(TimeProvider? timeProvider = null, ILogger<RedisStreamReceiver>? logger = null)`
    - `Task PublishAsync<T>(params T[] events)`
    - `static Task<List<IBatchContainer>> ReadAsync(RedisStreamReceiver receiver, int expected, int maxCount = 100)`
    - `static string EntryId(IBatchContainer batch)`
  - `record TestEvent(int Id, string Name)`.
  - `Eventually.WaitUntilAsync(Func<bool> condition, TimeSpan timeout)`.

- [ ] **Step 1: Create the project file**

`RedisStreamsProvider.IntegrationTests/RedisStreamsProvider.IntegrationTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
    <PackageReference Include="Testcontainers.Redis" Version="4.15.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Universley.OrleansContrib.StreamsProvider.Redis\Universley.OrleansContrib.StreamsProvider.Redis.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create the Redis fixture**

`RedisStreamsProvider.IntegrationTests/RedisFixture.cs`:

```csharp
using StackExchange.Redis;
using Testcontainers.Redis;

namespace RedisStreamsProvider.IntegrationTests;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7.4").Build();

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Connection = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await Connection.CloseAsync();
        Connection.Dispose();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "Redis";
}
```

- [ ] **Step 3: Create the test payload and the polling helper**

`RedisStreamsProvider.IntegrationTests/TestEvent.cs`:

```csharp
namespace RedisStreamsProvider.IntegrationTests;

public sealed record TestEvent(int Id, string Name);
```

`RedisStreamsProvider.IntegrationTests/Eventually.cs`:

```csharp
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
```

- [ ] **Step 4: Create the provider harness**

`RedisStreamsProvider.IntegrationTests/ProviderHarness.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Streams;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace RedisStreamsProvider.IntegrationTests;

internal sealed class ProviderHarness
{
    public ProviderHarness(IConnectionMultiplexer connection, RedisStreamReceiverOptions? receiverOptions = null)
    {
        Database = connection.GetDatabase();
        ReceiverOptions = receiverOptions ?? new RedisStreamReceiverOptions();

        // A unique queue prefix gives every test its own Redis stream key.
        var mapper = new HashRingBasedStreamQueueMapper(
            new HashRingStreamQueueMapperOptions { TotalQueueCount = 1 },
            $"it-{Guid.NewGuid():N}");
        QueueId = mapper.GetAllQueues().Single();
        Adapter = new RedisStreamAdapter(Database, "RedisStream", mapper, NullLoggerFactory.Instance, MsOptions.Create(ReceiverOptions));
    }

    public IDatabase Database { get; }

    public RedisStreamReceiverOptions ReceiverOptions { get; }

    public QueueId QueueId { get; }

    public RedisKey Key => QueueId.ToString();

    public RedisStreamAdapter Adapter { get; }

    public StreamId StreamId { get; } = StreamId.Create("it-namespace", "it-key");

    public RedisStreamReceiver CreateReceiver(TimeProvider? timeProvider = null, ILogger<RedisStreamReceiver>? logger = null) =>
        new(QueueId, Database, logger ?? NullLogger<RedisStreamReceiver>.Instance, timeProvider, MsOptions.Create(ReceiverOptions));

    public Task PublishAsync<T>(params T[] events) =>
        Adapter.QueueMessageBatchAsync(StreamId, events, null!, new Dictionary<string, object>());

    /// <summary>Polls the receiver (at most 10 reads) until it has returned <paramref name="expected"/> batches.</summary>
    public static async Task<List<IBatchContainer>> ReadAsync(RedisStreamReceiver receiver, int expected, int maxCount = 100)
    {
        var result = new List<IBatchContainer>();
        for (var attempt = 0; attempt < 10 && result.Count < expected; attempt++)
        {
            var batch = await receiver.GetQueueMessagesAsync(maxCount);
            if (batch is not null)
            {
                result.AddRange(batch);
            }
        }

        return result;
    }

    public static string EntryId(IBatchContainer batch) => ((RedisStreamBatchContainer)batch).StreamEntryId;
}
```

- [ ] **Step 5: Write a baseline round-trip test**

`RedisStreamsProvider.IntegrationTests/ReceiverTests.cs`:

```csharp
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace RedisStreamsProvider.IntegrationTests;

[Collection(RedisCollection.Name)]
public sealed class ReceiverTests(RedisFixture redis)
{
    [Fact]
    public async Task Published_event_is_read_and_acknowledged()
    {
        var harness = new ProviderHarness(redis.Connection);
        var receiver = harness.CreateReceiver();
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        await harness.PublishAsync(new TestEvent(1, "one"));

        var read = await ProviderHarness.ReadAsync(receiver, expected: 1);
        await receiver.MessagesDeliveredAsync(read);

        var received = Assert.Single(read.SelectMany(b => b.GetEvents<TestEvent>()));
        Assert.Equal(new TestEvent(1, "one"), received.Item1);
        var pending = await harness.Database.StreamPendingAsync(harness.Key, "consumer");
        Assert.Equal(0, pending.PendingMessageCount);
    }
}
```

- [ ] **Step 6: Add the project to the solution and run the test**

```bash
dotnet sln RedisStreamsInOrleans.sln add RedisStreamsProvider.IntegrationTests/RedisStreamsProvider.IntegrationTests.csproj
export DOCKER_HOST=unix:///run/user/1000/podman/podman.sock TESTCONTAINERS_RYUK_DISABLED=true
dotnet test RedisStreamsProvider.IntegrationTests
```

Expected: `Passed: 1`. This test describes current behavior, so it must pass before any library change. If it fails, stop and debug; do not change library code in this task.

- [ ] **Step 7: Give each test project its own TRX file**

In `build/Build.cs`, replace the `Test` target body:

```csharp
           DotNetTasks.DotNetTest(s => s
                   .SetProjectFile(Solution)
                   .SetConfiguration(Configuration)
                   .SetLoggers($"trx;LogFileName={TestResultDirectory / "testresults.trx"}")
           );
```

with:

```csharp
           DotNetTasks.DotNetTest(s => s
                   .SetProjectFile(Solution)
                   .SetConfiguration(Configuration)
                   .SetResultsDirectory(TestResultDirectory)
                   .SetLoggers("trx")
           );
```

A fixed `LogFileName` would make the two test projects overwrite each other's results.

Run: `./build.sh Test`
Expected: both test projects run. `artifacts/test-results/` contains two `.trx` files.

- [ ] **Step 8: Commit**

```bash
git add RedisStreamsProvider.IntegrationTests RedisStreamsInOrleans.sln build/Build.cs
git commit -m "test: add Redis integration test project with Testcontainers"
```

---

### Task 3: End-to-end tests through an Orleans TestCluster

**Files:**
- Modify: `RedisStreamsProvider.IntegrationTests/RedisStreamsProvider.IntegrationTests.csproj`
- Create: `RedisStreamsProvider.IntegrationTests/Cluster/ClusterFixture.cs`
- Create: `RedisStreamsProvider.IntegrationTests/Cluster/EventCollectorGrain.cs`
- Create: `RedisStreamsProvider.IntegrationTests/Cluster/StreamDeliveryTests.cs`

**Interfaces:**
- Consumes: `Eventually.WaitUntilAsync` (Task 2), `RedisStreamFactory.Create` (library).
- Produces (used by Task 10):
  - `ClusterFixture` (class fixture) with `TestCluster Cluster`.
  - `ClusterFixture.ProviderName` = `"RedisStream"`.
  - `ClusterCollection.Name` = `"OrleansCluster"`.
  - `EventCollectorGrain.StreamNamespace` = `"it-collector"`.
  - `ReceivedEvents.For(string key) : int[]`.

- [ ] **Step 1: Add the Orleans test packages**

```bash
dotnet add RedisStreamsProvider.IntegrationTests package Microsoft.Orleans.TestingHost --version 10.0.1
dotnet add RedisStreamsProvider.IntegrationTests package Microsoft.Orleans.Sdk --version 10.0.1
```

`Microsoft.Orleans.Sdk` is needed so the test grain gets generated code.

- [ ] **Step 2: Create the grain and the recorder it writes to**

`RedisStreamsProvider.IntegrationTests/Cluster/EventCollectorGrain.cs`:

```csharp
using System.Collections.Concurrent;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;

namespace RedisStreamsProvider.IntegrationTests.Cluster;

/// <summary>
/// Silos run in the test process, so a static store survives grain deactivation and silo shutdown.
/// </summary>
public static class ReceivedEvents
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<int>> ByStreamKey = new();

    public static void Record(string streamKey, int item) => ByStreamKey.GetOrAdd(streamKey, _ => new()).Enqueue(item);

    public static int[] For(string streamKey) => ByStreamKey.TryGetValue(streamKey, out var items) ? items.ToArray() : [];
}

public interface IEventCollectorGrain : IGrainWithStringKey
{
}

[ImplicitStreamSubscription(StreamNamespace)]
public sealed class EventCollectorGrain : Grain, IEventCollectorGrain, IAsyncObserver<int>
{
    public const string StreamNamespace = "it-collector";

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamId = StreamId.Create(StreamNamespace, this.GetPrimaryKeyString());
        await this.GetStreamProvider(ClusterFixture.ProviderName).GetStream<int>(streamId).SubscribeAsync(this);
        await base.OnActivateAsync(cancellationToken);
    }

    public Task OnNextAsync(int item, StreamSequenceToken? token = null)
    {
        ReceivedEvents.Record(this.GetPrimaryKeyString(), item);
        return Task.CompletedTask;
    }

    public Task OnCompletedAsync() => Task.CompletedTask;

    public Task OnErrorAsync(Exception ex) => Task.CompletedTask;
}
```

- [ ] **Step 3: Create the cluster fixture**

`RedisStreamsProvider.IntegrationTests/Cluster/ClusterFixture.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using StackExchange.Redis;
using Testcontainers.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace RedisStreamsProvider.IntegrationTests.Cluster;

public sealed class ClusterFixture : IAsyncLifetime
{
    public const string ProviderName = "RedisStream";

    private readonly RedisContainer _redis = new RedisBuilder("redis:7.4").Build();

    // Silos and the client are created in-process from configurator types, so they read the
    // connection string from here. Every test class using this fixture is in ClusterCollection,
    // which runs them one at a time, so fixtures never overwrite each other's value mid-startup.
    internal static string RedisConnectionString { get; private set; } = "";

    public TestCluster Cluster { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        RedisConnectionString = _redis.GetConnectionString();

        var builder = new TestClusterBuilder(initialSilosCount: 2);
        builder.AddSiloBuilderConfigurator<RedisStreamSiloConfigurator>();
        builder.AddClientBuilderConfigurator<RedisStreamClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        await Cluster.StopAllSilosAsync();
        await Cluster.DisposeAsync();
        await _redis.DisposeAsync();
    }

    internal static void AddRedisServices(IServiceCollection services)
    {
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(RedisConnectionString));
        services.AddOptions<HashRingStreamQueueMapperOptions>(ProviderName).Configure(options => options.TotalQueueCount = 2);
    }
}

public sealed class RedisStreamSiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        ClusterFixture.AddRedisServices(siloBuilder.Services);
        siloBuilder.AddMemoryGrainStorage("PubSubStore");
        siloBuilder.AddPersistentStreams(ClusterFixture.ProviderName, RedisStreamFactory.Create, _ => { });
    }
}

public sealed class RedisStreamClientConfigurator : IClientBuilderConfigurator
{
    public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
    {
        ClusterFixture.AddRedisServices(clientBuilder.Services);
        clientBuilder.AddPersistentStreams(ClusterFixture.ProviderName, RedisStreamFactory.Create, _ => { });
    }
}

[CollectionDefinition(Name)]
public sealed class ClusterCollection
{
    public const string Name = "OrleansCluster";
}
```

- [ ] **Step 4: Write the delivery tests**

`RedisStreamsProvider.IntegrationTests/Cluster/StreamDeliveryTests.cs`:

```csharp
using Orleans;
using Orleans.Runtime;

namespace RedisStreamsProvider.IntegrationTests.Cluster;

[Collection(ClusterCollection.Name)]
public sealed class StreamDeliveryTests(ClusterFixture fixture) : IClassFixture<ClusterFixture>
{
    [Fact]
    public async Task Events_from_a_client_reach_the_implicitly_subscribed_grain_in_order()
    {
        var key = $"order-{Guid.NewGuid():N}";
        var stream = fixture.Cluster.Client.GetStreamProvider(ClusterFixture.ProviderName)
            .GetStream<int>(StreamId.Create(EventCollectorGrain.StreamNamespace, key));

        for (var i = 0; i < 20; i++)
        {
            await stream.OnNextAsync(i);
        }

        await Eventually.WaitUntilAsync(() => ReceivedEvents.For(key).Length >= 20, TimeSpan.FromSeconds(30));
        Assert.Equal(Enumerable.Range(0, 20), ReceivedEvents.For(key));
    }

    [Fact]
    public async Task Events_on_different_streams_reach_only_their_own_grain()
    {
        var keys = Enumerable.Range(0, 3).Select(i => $"multi-{i}-{Guid.NewGuid():N}").ToArray();
        var provider = fixture.Cluster.Client.GetStreamProvider(ClusterFixture.ProviderName);

        foreach (var key in keys)
        {
            var stream = provider.GetStream<int>(StreamId.Create(EventCollectorGrain.StreamNamespace, key));
            for (var i = 0; i < 10; i++)
            {
                await stream.OnNextAsync(i);
            }
        }

        foreach (var key in keys)
        {
            await Eventually.WaitUntilAsync(() => ReceivedEvents.For(key).Length >= 10, TimeSpan.FromSeconds(30));
            Assert.Equal(Enumerable.Range(0, 10), ReceivedEvents.For(key));
        }
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test RedisStreamsProvider.IntegrationTests --filter "FullyQualifiedName~Cluster"`
Expected: `Passed: 2`. These describe current behavior, the same flow as `example/`. If one fails, stop and use superpowers:systematic-debugging; the failure is a real provider bug and must be understood before continuing.

- [ ] **Step 6: Commit**

```bash
git add RedisStreamsProvider.IntegrationTests
git commit -m "test: add end-to-end stream delivery tests on an Orleans TestCluster"
```

---

### Task 4: Publishing failures reach the producer

**Files:**
- Modify: `Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamAdapter.cs:59-62`
- Test: `RedisStreamsProvider.UnitTests/RedisStreamAdapterTests.cs:51-81`

**Interfaces:**
- Consumes: nothing new.
- Produces: `RedisStreamAdapter.QueueMessageBatchAsync` logs, then rethrows the original exception.

- [ ] **Step 1: Change the unit test to expect the exception**

In `RedisStreamsProvider.UnitTests/RedisStreamAdapterTests.cs`, replace the whole `QueueMessageBatchAsync_ShouldLogError_OnException` test with:

```csharp
        [Fact]
        public async Task QueueMessageBatchAsync_ShouldLogAndRethrow_OnException()
        {
            // Arrange
            var streamId = StreamId.Create("namespace", "key");
            var events = new List<string> { "event1", "event2" };
            var token = new RedisStreamSequenceToken(123, 456);
            var requestContext = new Dictionary<string, object>();
            var mockDatabase = new Mock<IDatabase>();
            var mockLoggerFactory = new Mock<ILoggerFactory>();
            var mockLogger = new Mock<ILogger<RedisStreamAdapter>>();
            var mockReceiverOptions = new Mock<IOptions<RedisStreamReceiverOptions>>();
            mockReceiverOptions.Setup(o => o.Value).Returns(new RedisStreamReceiverOptions());
            mockLoggerFactory.Setup(factory => factory.CreateLogger(It.IsAny<string>())).Returns(mockLogger.Object);
            var adapter = new RedisStreamAdapter(mockDatabase.Object, "TestProvider", _mockQueueMapper.Object, mockLoggerFactory.Object, mockReceiverOptions.Object);
            mockDatabase.Setup(db => db.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<NameValueEntry[]>(), It.IsAny<RedisValue?>(), It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()))
                .ThrowsAsync(new Exception("Test exception"));

            // Act
            var thrown = await Assert.ThrowsAsync<Exception>(() => adapter.QueueMessageBatchAsync(streamId, events, token, requestContext));

            // Assert
            Assert.Equal("Test exception", thrown.Message);
            mockLogger.Verify(
                logger => logger.Log(
                    It.Is<LogLevel>(logLevel => logLevel == LogLevel.Error),
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v != null && v.ToString()!.Contains("Error adding event to stream")),
                    It.IsAny<Exception>(),
                    It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
                Times.Once);
        }
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test RedisStreamsProvider.UnitTests --filter "FullyQualifiedName~QueueMessageBatchAsync_ShouldLogAndRethrow_OnException"`
Expected: FAIL with `Assert.Throws() Failure: No exception was thrown`.

- [ ] **Step 3: Rethrow after logging**

In `Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamAdapter.cs`, replace:

```csharp
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding event to stream {StreamId}", streamId);
            }
```

with:

```csharp
            catch (Exception ex)
            {
                // Rethrow so the producer's OnNextAsync fails and it can retry; swallowing loses the event.
                _logger.LogError(ex, "Error adding event to stream {StreamId}", streamId);
                throw;
            }
```

- [ ] **Step 4: Run the unit tests**

Run: `dotnet test RedisStreamsProvider.UnitTests`
Expected: `Passed: 36`, `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamAdapter.cs RedisStreamsProvider.UnitTests/RedisStreamAdapterTests.cs
git commit -m "fix: surface stream publish failures to producers instead of swallowing them"
```

---

### Task 5: Unreadable entries no longer take the whole batch down

**Files:**
- Modify: `Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamReceiver.cs` (fields, `GetQueueMessagesAsync`, `Initialize`, `MessagesDeliveredAsync`; new `ToBatchesAsync`)
- Test: `RedisStreamsProvider.UnitTests/RedisStreamReceiverTests.cs`
- Test: `RedisStreamsProvider.IntegrationTests/ReceiverTests.cs`

**Interfaces:**
- Consumes: `ProviderHarness`, `RedisCollection` (Task 2).
- Produces (used by Tasks 6–9):
  - private constants `GroupName = "consumer"` and `ConsumerName = "consumer"` in `RedisStreamReceiver`.
  - `private async Task<List<IBatchContainer>> ToBatchesAsync(StreamEntry[] entries)`.
  - test helper `private static StreamEntry Entry(string id)` in `RedisStreamReceiverTests`.

- [ ] **Step 1: Write the failing unit test**

Add to `RedisStreamsProvider.UnitTests/RedisStreamReceiverTests.cs`, inside the class:

```csharp
        private static StreamEntry Entry(string id) => new(id, [
            new("streamNamespace", "testNamespace"),
            new("streamKey", "testKey"),
            new("eventType", "testEventType"),
            new("data", "testData")
        ]);

        [Fact]
        public async Task GetQueueMessagesAsync_SkipsAndAcknowledgesUnreadableEntries()
        {
            // Arrange: "2-0" has no data field, like an entry deleted while it was still pending.
            var unreadable = new StreamEntry("2-0", [
                new("streamNamespace", "testNamespace"),
                new("streamKey", "testKey"),
                new("eventType", "testEventType")
            ]);
            _mockDatabase.Setup(db => db.StreamReadGroupAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(),
                    It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(new[] { Entry("1-0"), unreadable, Entry("3-0") });
            var receiver = new RedisStreamReceiver(_queueId, _mockDatabase.Object, _mockLogger.Object);

            // Act
            var result = await receiver.GetQueueMessagesAsync(10);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(new[] { "1-0", "3-0" }, result.Cast<RedisStreamBatchContainer>().Select(b => b.StreamEntryId));
            _mockDatabase.Verify(db => db.StreamAcknowledgeAsync(
                    _queueId.ToString(), "consumer",
                    It.Is<RedisValue[]>(ids => ids.Length == 1 && ids[0] == "2-0"),
                    It.IsAny<CommandFlags>()),
                Times.Once);
        }
```

- [ ] **Step 2: Write the failing integration test**

Add to `RedisStreamsProvider.IntegrationTests/ReceiverTests.cs`, inside the class (add `using StackExchange.Redis;` at the top of the file):

```csharp
    [Fact]
    public async Task Unreadable_entry_does_not_block_the_valid_entries_read_with_it()
    {
        var harness = new ProviderHarness(redis.Connection);
        var receiver = harness.CreateReceiver();
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        await harness.PublishAsync(new TestEvent(1, "before"));
        await harness.Database.StreamAddAsync(harness.Key, [new NameValueEntry("garbage", "x")]);
        await harness.PublishAsync(new TestEvent(2, "after"));

        var read = await ProviderHarness.ReadAsync(receiver, expected: 2);
        await receiver.MessagesDeliveredAsync(read);

        Assert.Equal(new[] { 1, 2 }, read.SelectMany(b => b.GetEvents<TestEvent>()).Select(e => e.Item1.Id));
        var pending = await harness.Database.StreamPendingAsync(harness.Key, "consumer");
        Assert.Equal(0, pending.PendingMessageCount);
    }
```

- [ ] **Step 3: Run both and watch them fail**

Run: `dotnet test RedisStreamsProvider.UnitTests --filter "FullyQualifiedName~SkipsAndAcknowledgesUnreadableEntries"`
Expected: FAIL. `Assert.NotNull() Failure: Value is null`, because the constructor exception makes the whole read return `null`.

Run: `dotnet test RedisStreamsProvider.IntegrationTests --filter "FullyQualifiedName~Unreadable_entry_does_not_block"`
Expected: FAIL. The event-id assertion gets an empty sequence.

- [ ] **Step 4: Implement**

In `Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamReceiver.cs`:

a) Add these constants as the first members of the class:

```csharp
        // One group per stream, and the queue id as consumer name. Orleans gives each queue to one silo at a time,
        // so whichever silo owns the queue reads as the same consumer and picks up entries a previous owner read but
        // never acknowledged. Both values are part of the wire format; do not change them.
        private const string GroupName = "consumer";
        private string ConsumerName => _queueId.ToString();
```

(Corrected by controller ruling R6: `ConsumerName` must stay the queue id, as in the original code.)

b) Replace the body of `GetQueueMessagesAsync` with:

```csharp
            try
            {
                var events = _database.StreamReadGroupAsync(_queueId.ToString(), GroupName, ConsumerName, _lastId, maxCount);
                pendingTasks = events;
                _lastId = ">";
                var batches = await ToBatchesAsync(await events);
                await TrimStreamIfNeeded();

                return batches;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading from stream {QueueId}", _queueId);
                return default;
            }
            finally
            {
                pendingTasks = null;
            }
```

c) Add this method directly below `GetQueueMessagesAsync`:

```csharp
        private async Task<List<IBatchContainer>> ToBatchesAsync(StreamEntry[] entries)
        {
            var batches = new List<IBatchContainer>(entries.Length);
            List<RedisValue>? unreadable = null;
            foreach (var entry in entries)
            {
                try
                {
                    batches.Add(new RedisStreamBatchContainer(entry));
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
                {
                    // Entries deleted while still pending come back with no fields. Either way this entry can never
                    // be delivered, and leaving it pending would keep it (and everything read with it) stuck.
                    _logger.LogError(ex, "Acknowledging unreadable entry {EntryId} in stream {QueueId} without delivering it", entry.Id, _queueId);
                    (unreadable ??= []).Add(entry.Id);
                }
            }

            if (unreadable is not null)
            {
                await _database.StreamAcknowledgeAsync(_queueId.ToString(), GroupName, [.. unreadable]);
            }

            return batches;
        }
```

d) In `Initialize`, replace `"consumer"` with `GroupName`:

```csharp
                var task = _database.StreamCreateConsumerGroupAsync(_queueId.ToString(), GroupName, "$", true);
```

e) In `MessagesDeliveredAsync`, replace `"consumer"` with `GroupName`:

```csharp
                        var ack = _database.StreamAcknowledgeAsync(_queueId.ToString(), GroupName, container.StreamEntryId);
```

- [ ] **Step 5: Run all tests**

Run: `dotnet test RedisStreamsProvider.UnitTests`
Expected: `Passed: 37`, `Failed: 0`.

Run: `dotnet test RedisStreamsProvider.IntegrationTests`
Expected: all pass (4 tests).

- [ ] **Step 6: Commit**

```bash
git add Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamReceiver.cs RedisStreamsProvider.UnitTests/RedisStreamReceiverTests.cs RedisStreamsProvider.IntegrationTests/ReceiverTests.cs
git commit -m "fix: acknowledge unreadable stream entries instead of failing the whole read"
```

---

### Task 6: Redeliver every pending entry after a restart or queue handoff

**Files:**
- Modify: `Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamReceiver.cs` (fields, `GetQueueMessagesAsync`; new `ReadEntriesAsync`, `ReadGroupAsync`)
- Test: `RedisStreamsProvider.UnitTests/RedisStreamReceiverTests.cs`
- Test: `RedisStreamsProvider.IntegrationTests/ReceiverTests.cs`

**Interfaces:**
- Consumes: `GroupName`, `ConsumerName`, `ToBatchesAsync`, `Entry(string)` (Task 5); `ProviderHarness` (Task 2).
- Produces (used by Task 7):
  - fields `private RedisValue _pendingCursor = "0";` and `private bool _drainingPending = true;` (the `_lastId` field is removed).
  - constants `NewMessages = ">"` and `MaxReadCount = 1000`.
  - `private async Task<StreamEntry[]> ReadEntriesAsync(int maxCount)`.

- [ ] **Step 1: Write the failing unit tests**

Add to `RedisStreamsProvider.UnitTests/RedisStreamReceiverTests.cs`, inside the class:

```csharp
        private void SetupRead(RedisValue position, params StreamEntry[] entries) =>
            _mockDatabase.Setup(db => db.StreamReadGroupAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                    It.Is<RedisValue?>(p => p == position),
                    It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(entries);

        private static string[] Ids(IList<IBatchContainer>? batches) =>
            batches!.Cast<RedisStreamBatchContainer>().Select(b => b.StreamEntryId).ToArray();

        [Fact]
        public async Task GetQueueMessagesAsync_DrainsAllPendingEntriesBeforeReadingNewOnes()
        {
            // Arrange: three entries are pending from a previous owner, one new entry is waiting.
            SetupRead("0", Entry("1-0"), Entry("2-0"));
            SetupRead("2-0", Entry("3-0"));
            SetupRead("3-0");
            SetupRead(">", Entry("4-0"));
            var receiver = new RedisStreamReceiver(_queueId, _mockDatabase.Object, _mockLogger.Object);

            // Act
            var first = await receiver.GetQueueMessagesAsync(2);
            var second = await receiver.GetQueueMessagesAsync(2);
            var third = await receiver.GetQueueMessagesAsync(2);

            // Assert
            Assert.Equal(new[] { "1-0", "2-0" }, Ids(first));
            Assert.Equal(new[] { "3-0" }, Ids(second));
            Assert.Equal(new[] { "4-0" }, Ids(third));
        }

        [Fact]
        public async Task GetQueueMessagesAsync_ReadsAtMost1000_WhenMaxCountIsUnlimited()
        {
            // Arrange
            var receiver = new RedisStreamReceiver(_queueId, _mockDatabase.Object, _mockLogger.Object);

            // Act
            await receiver.GetQueueMessagesAsync(QueueAdapterConstants.UNLIMITED_GET_QUEUE_MSG);

            // Assert
            _mockDatabase.Verify(db => db.StreamReadGroupAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(),
                    1000, It.IsAny<bool>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()),
                Times.AtLeastOnce);
        }
```

- [ ] **Step 2: Write the failing integration test**

Add to `RedisStreamsProvider.IntegrationTests/ReceiverTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task New_owner_redelivers_every_entry_the_previous_owner_never_acknowledged()
    {
        var harness = new ProviderHarness(redis.Connection);
        var previousOwner = harness.CreateReceiver();
        await previousOwner.Initialize(TimeSpan.FromSeconds(5));
        await harness.PublishAsync(new TestEvent(1, "a"), new TestEvent(2, "b"), new TestEvent(3, "c"));
        var readBeforeCrash = await ProviderHarness.ReadAsync(previousOwner, expected: 3);
        Assert.Equal(3, readBeforeCrash.Count);
        // previousOwner "crashes" here: it never acknowledges what it read.

        var newOwner = harness.CreateReceiver();
        await newOwner.Initialize(TimeSpan.FromSeconds(5));
        var redelivered = await ProviderHarness.ReadAsync(newOwner, expected: 3, maxCount: 2);

        Assert.Equal(readBeforeCrash.Select(ProviderHarness.EntryId), redelivered.Select(ProviderHarness.EntryId));
    }
```

- [ ] **Step 3: Run them and watch them fail**

Run: `dotnet test RedisStreamsProvider.UnitTests --filter "FullyQualifiedName~DrainsAllPendingEntries|FullyQualifiedName~ReadsAtMost1000"`
Expected: both FAIL. `DrainsAllPendingEntries` gets `["4-0"]` on the second read where `["3-0"]` is expected. `ReadsAtMost1000` sees the read called with `-1`.

Run: `dotnet test RedisStreamsProvider.IntegrationTests --filter "FullyQualifiedName~New_owner_redelivers"`
Expected: FAIL. Only 2 of the 3 entries are redelivered.

- [ ] **Step 4: Implement**

In `Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamReceiver.cs`:

a) Below `ConsumerName`, add:

```csharp
        private const string NewMessages = ">";
        private const int MaxReadCount = 1000;
```

b) Replace the field `private string _lastId = "0";` with:

```csharp
        // Until the pending list is drained, reads walk it from this cursor; afterwards they ask for new messages.
        private RedisValue _pendingCursor = "0";
        private bool _drainingPending = true;
```

c) Replace the `try` block of `GetQueueMessagesAsync` (keep its `catch` and `finally` unchanged) with:

```csharp
            try
            {
                var entries = await ReadEntriesAsync(maxCount);
                var batches = await ToBatchesAsync(entries);
                await TrimStreamIfNeeded();

                return batches;
            }
```

d) Add these methods directly below `GetQueueMessagesAsync`:

```csharp
        private async Task<StreamEntry[]> ReadEntriesAsync(int maxCount)
        {
            var count = maxCount is > 0 and < MaxReadCount ? maxCount : MaxReadCount;
            if (_drainingPending)
            {
                var pending = await ReadGroupAsync(_pendingCursor, count);
                if (pending.Length > 0)
                {
                    _pendingCursor = pending[^1].Id;
                    return pending;
                }

                _drainingPending = false;
            }

            return await ReadGroupAsync(NewMessages, count);
        }

        private async Task<StreamEntry[]> ReadGroupAsync(RedisValue position, int count)
        {
            var read = _database.StreamReadGroupAsync(_queueId.ToString(), GroupName, ConsumerName, position, count);
            pendingTasks = read;
            return await read;
        }
```

- [ ] **Step 5: Run all tests**

Run: `dotnet test RedisStreamsProvider.UnitTests`
Expected: `Passed: 39`, `Failed: 0`.

Run: `dotnet test RedisStreamsProvider.IntegrationTests`
Expected: all pass (5 tests).

- [ ] **Step 6: Commit**

```bash
git add Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamReceiver.cs RedisStreamsProvider.UnitTests/RedisStreamReceiverTests.cs RedisStreamsProvider.IntegrationTests/ReceiverTests.cs
git commit -m "fix: redeliver the whole pending list after restart or queue handoff"
```

---

### Task 7: Consumer group bootstrap and self-healing

**Files:**
- Modify: `Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamReceiver.cs` (`Initialize`, `GetQueueMessagesAsync` catch; new `EnsureConsumerGroupAsync`, `TryRecreateConsumerGroupAsync`)
- Test: `RedisStreamsProvider.UnitTests/RedisStreamReceiverTests.cs`
- Test: `RedisStreamsProvider.IntegrationTests/ReceiverTests.cs`

**Interfaces:**
- Consumes: `GroupName`, `_pendingCursor`, `_drainingPending` (Tasks 5–6).
- Produces:
  - `private async Task EnsureConsumerGroupAsync()`, which creates the group at `"0"` with `MKSTREAM` and ignores `BUSYGROUP`.
  - `GetQueueMessagesAsync` returns an empty list (not `null`) after recovering from `NOGROUP`.

- [ ] **Step 1: Update the existing test and add the new unit tests**

In `RedisStreamsProvider.UnitTests/RedisStreamReceiverTests.cs`, change the verification in `Initialize_CreatesConsumerGroup` from `"$"` to `"0"`:

```csharp
            _mockDatabase.Verify(
                db => db.StreamCreateConsumerGroupAsync(_queueId.ToString(), "consumer", "0", true, CommandFlags.None),
                Times.Once);
```

Then add, inside the class:

```csharp
        [Fact]
        public async Task Initialize_DoesNotLogError_WhenGroupAlreadyExists()
        {
            // Arrange
            _mockDatabase.Setup(db => db.StreamCreateConsumerGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                    It.IsAny<RedisValue?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()))
                .ThrowsAsync(new RedisServerException("BUSYGROUP Consumer Group name already exists"));
            var receiver = new RedisStreamReceiver(_queueId, _mockDatabase.Object, _mockLogger.Object);

            // Act
            await receiver.Initialize(TimeSpan.FromSeconds(5));

            // Assert
            _mockLogger.Verify(
                logger => logger.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Never);
        }

        [Fact]
        public async Task GetQueueMessagesAsync_RecreatesGroup_WhenGroupIsMissing()
        {
            // Arrange
            _mockDatabase.Setup(db => db.StreamReadGroupAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(),
                    It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()))
                .ThrowsAsync(new RedisServerException("NOGROUP No such key 'q' or consumer group 'consumer' in XREADGROUP with GROUP option"));
            var receiver = new RedisStreamReceiver(_queueId, _mockDatabase.Object, _mockLogger.Object);

            // Act
            var result = await receiver.GetQueueMessagesAsync(10);

            // Assert
            Assert.NotNull(result);
            Assert.Empty(result);
            _mockDatabase.Verify(
                db => db.StreamCreateConsumerGroupAsync(_queueId.ToString(), "consumer", "0", true, CommandFlags.None),
                Times.Once);
        }
```

- [ ] **Step 2: Add the failing integration tests**

Add to `RedisStreamsProvider.IntegrationTests/ReceiverTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task Entries_published_before_the_first_receiver_started_are_delivered()
    {
        var harness = new ProviderHarness(redis.Connection);
        await harness.PublishAsync(new TestEvent(1, "early"));

        var receiver = harness.CreateReceiver();
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        var read = await ProviderHarness.ReadAsync(receiver, expected: 1);

        Assert.Single(read);
    }

    [Fact]
    public async Task Receiver_recovers_when_the_stream_key_disappears()
    {
        var harness = new ProviderHarness(redis.Connection);
        var receiver = harness.CreateReceiver();
        await receiver.Initialize(TimeSpan.FromSeconds(5));

        // Same effect as a Redis restart without persistence, a failover to an empty replica, or key eviction.
        await harness.Database.KeyDeleteAsync(harness.Key);
        await receiver.GetQueueMessagesAsync(10);
        await harness.PublishAsync(new TestEvent(1, "after"));
        var read = await ProviderHarness.ReadAsync(receiver, expected: 1);

        Assert.Single(read);
    }
```

- [ ] **Step 3: Run them and watch them fail**

Run: `dotnet test RedisStreamsProvider.UnitTests --filter "FullyQualifiedName~Initialize|FullyQualifiedName~RecreatesGroup"`
Expected:
- `Initialize_CreatesConsumerGroup` and `GetQueueMessagesAsync_RecreatesGroup_WhenGroupIsMissing` FAIL.
- `Initialize_DoesNotLogError_WhenGroupAlreadyExists` already passes. It guards the rewrite and is not a red test.

Run: `dotnet test RedisStreamsProvider.IntegrationTests --filter "FullyQualifiedName~Entries_published_before|FullyQualifiedName~Receiver_recovers"`
Expected: both FAIL with `Assert.Single() Failure: The collection was empty`.

- [ ] **Step 4: Implement**

In `Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamReceiver.cs`:

a) Replace the whole `Initialize` method with:

```csharp
        public async Task Initialize(TimeSpan timeout)
        {
            try
            {
                await EnsureConsumerGroupAsync().WaitAsync(timeout);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing stream {QueueId}", _queueId);
            }
        }

        // Starts at "0" rather than "$" so entries published before the group existed are delivered, not skipped.
        private async Task EnsureConsumerGroupAsync()
        {
            try
            {
                await _database.StreamCreateConsumerGroupAsync(_queueId.ToString(), GroupName, "0", createStream: true);
            }
            catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP", StringComparison.Ordinal))
            {
                // The group already exists, which is the normal case on every start after the first.
            }
        }

        private async Task TryRecreateConsumerGroupAsync()
        {
            try
            {
                await EnsureConsumerGroupAsync();
                _pendingCursor = "0";
                _drainingPending = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recreating consumer group for stream {QueueId}", _queueId);
            }
        }
```

b) In `GetQueueMessagesAsync`, insert this `catch` clause **before** the existing `catch (Exception ex)`:

```csharp
            catch (RedisServerException ex) when (ex.Message.StartsWith("NOGROUP", StringComparison.Ordinal))
            {
                // The stream key (and with it the group) is gone. Without this every later read would fail forever.
                _logger.LogWarning(ex, "Consumer group for stream {QueueId} is missing, recreating it", _queueId);
                await TryRecreateConsumerGroupAsync();
                return [];
            }
```

- [ ] **Step 5: Run all tests**

Run: `dotnet test RedisStreamsProvider.UnitTests`
Expected: `Passed: 41`, `Failed: 0`.

Run: `dotnet test RedisStreamsProvider.IntegrationTests`
Expected: all pass (7 tests).

- [ ] **Step 6: Commit**

```bash
git add Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamReceiver.cs RedisStreamsProvider.UnitTests/RedisStreamReceiverTests.cs RedisStreamsProvider.IntegrationTests/ReceiverTests.cs
git commit -m "fix: create consumer group from the start of the stream and recreate it when missing"
```

---

### Task 8: Acknowledge a delivered batch in one call

**Files:**
- Modify: `Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamReceiver.cs` (`MessagesDeliveredAsync`)
- Test: `RedisStreamsProvider.UnitTests/RedisStreamReceiverTests.cs` (tests `MessagesDeliveredAsync_*`)
- Test: `RedisStreamsProvider.IntegrationTests/ReceiverTests.cs`

**Interfaces:**
- Consumes: `GroupName`, `Entry(string)` (Task 5).
- Produces: `MessagesDeliveredAsync` issues exactly one `StreamAcknowledgeAsync(RedisKey, RedisValue, RedisValue[], CommandFlags)` per non-empty call.

- [ ] **Step 1: Rewrite the acknowledgement unit tests**

In `RedisStreamsProvider.UnitTests/RedisStreamReceiverTests.cs`, replace the whole `MessagesDeliveredAsync_AcknowledgesMessages` test with:

```csharp
        [Fact]
        public async Task MessagesDeliveredAsync_AcknowledgesAllMessagesInOneCall()
        {
            // Arrange
            var messages = new List<IBatchContainer>
            {
                new RedisStreamBatchContainer(Entry("1-0")),
                new RedisStreamBatchContainer(Entry("2-0"))
            };
            var receiver = new RedisStreamReceiver(_queueId, _mockDatabase.Object, _mockLogger.Object);

            // Act
            await receiver.MessagesDeliveredAsync(messages);

            // Assert
            _mockDatabase.Verify(db => db.StreamAcknowledgeAsync(
                    _queueId.ToString(), "consumer",
                    It.Is<RedisValue[]>(ids => ids.Length == 2 && ids[0] == "1-0" && ids[1] == "2-0"),
                    CommandFlags.None),
                Times.Once);
            _mockDatabase.Verify(db => db.StreamAcknowledgeAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()),
                Times.Never);
        }

        [Fact]
        public async Task MessagesDeliveredAsync_DoesNothing_ForEmptyList()
        {
            // Arrange
            var receiver = new RedisStreamReceiver(_queueId, _mockDatabase.Object, _mockLogger.Object);

            // Act
            await receiver.MessagesDeliveredAsync(new List<IBatchContainer>());

            // Assert
            _mockDatabase.Verify(db => db.StreamAcknowledgeAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()),
                Times.Never);
        }
```

In `MessagesDeliveredAsync_ShouldLogError_OnException`, change the mock setup to the array overload:

```csharp
            mockDatabase.Setup(db => db.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                    It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
                .ThrowsAsync(new Exception("Test exception"));
```

- [ ] **Step 2: Add the integration test**

Add to `RedisStreamsProvider.IntegrationTests/ReceiverTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task Acknowledging_a_batch_clears_all_of_its_pending_entries()
    {
        var harness = new ProviderHarness(redis.Connection);
        var receiver = harness.CreateReceiver();
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        await harness.PublishAsync(Enumerable.Range(0, 50).Select(i => new TestEvent(i, "e")).ToArray());

        var read = await ProviderHarness.ReadAsync(receiver, expected: 50);
        await receiver.MessagesDeliveredAsync(read);

        var pending = await harness.Database.StreamPendingAsync(harness.Key, "consumer");
        Assert.Equal(0, pending.PendingMessageCount);
    }
```

- [ ] **Step 3: Run them and watch the new unit test fail**

Run: `dotnet test RedisStreamsProvider.UnitTests --filter "FullyQualifiedName~MessagesDeliveredAsync"`
Expected:
- `MessagesDeliveredAsync_AcknowledgesAllMessagesInOneCall` FAILS (the array overload is never called).
- `MessagesDeliveredAsync_ShouldLogError_OnException` FAILS (the array-overload setup never throws).
- `MessagesDeliveredAsync_DoesNothing_ForEmptyList` passes already.

- [ ] **Step 4: Implement**

In `Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamReceiver.cs`, replace the whole `MessagesDeliveredAsync` method with:

```csharp
        public async Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            var ids = messages.OfType<RedisStreamBatchContainer>().Select(m => (RedisValue)m.StreamEntryId).ToArray();
            if (ids.Length == 0)
            {
                return;
            }

            try
            {
                var ack = _database.StreamAcknowledgeAsync(_queueId.ToString(), GroupName, ids);
                pendingTasks = ack;
                await ack;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error acknowledging messages in stream {QueueId}", _queueId);
            }
            finally
            {
                pendingTasks = null;
            }
        }
```

- [ ] **Step 5: Run all tests**

Run: `dotnet test RedisStreamsProvider.UnitTests`
Expected: `Passed: 42`, `Failed: 0`.

Run: `dotnet test RedisStreamsProvider.IntegrationTests`
Expected: all pass (8 tests).

- [ ] **Step 6: Commit**

```bash
git add Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamReceiver.cs RedisStreamsProvider.UnitTests/RedisStreamReceiverTests.cs RedisStreamsProvider.IntegrationTests/ReceiverTests.cs
git commit -m "perf: acknowledge delivered batches with a single XACK"
```

---

### Task 9: Trim only acknowledged entries by default

**Files:**
- Create: `Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamTrimStrategy.cs`
- Modify: `Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamReceiverOptions.cs`
- Modify: `Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamReceiver.cs` (`TrimStreamIfNeeded`; new `TrimAcknowledgedEntriesAsync`, `GetAcknowledgedTrimId`)
- Modify: `Universley.OrleansContrib.StreamsProvider.Redis/Universley.OrleansContrib.StreamsProvider.Redis.csproj`
- Modify: `RedisStreamsProvider.IntegrationTests/RedisStreamsProvider.IntegrationTests.csproj`
- Modify: `README.md:125-138` (Configuration Options)
- Test: `RedisStreamsProvider.UnitTests/RedisStreamReceiverTrimTests.cs`
- Create: `RedisStreamsProvider.IntegrationTests/TrimmingTests.cs`

**Interfaces:**
- Consumes: `GroupName` (Task 5); `ProviderHarness.CreateReceiver(TimeProvider?, ILogger<RedisStreamReceiver>?)`, `ReadAsync`, `EntryId` (Task 2).
- Produces:
  - `public enum RedisStreamTrimStrategy { AcknowledgedOnly = 0, MaxLength = 1 }`.
  - `RedisStreamReceiverOptions.TrimStrategy` (default `AcknowledgedOnly`).
  - `internal static RedisValue? RedisStreamReceiver.GetAcknowledgedTrimId(string? lastDeliveredId, long pendingCount, RedisValue lowestPendingId)`.

- [ ] **Step 1: Add the option types**

Create `Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamTrimStrategy.cs`:

```csharp
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
```

Replace the contents of `Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamReceiverOptions.cs` with:

```csharp
using System.ComponentModel.DataAnnotations;

namespace Universley.OrleansContrib.StreamsProvider.Redis
{
    public class RedisStreamReceiverOptions
    {
        /// <summary>
        /// With <see cref="RedisStreamTrimStrategy.MaxLength"/>: roughly how many entries are kept after each trim.
        /// With <see cref="RedisStreamTrimStrategy.AcknowledgedOnly"/>: a warning is logged when more entries than this
        /// are still in the stream after trimming.
        /// </summary>
        [Range(1, int.MaxValue)]
        public int MaxStreamLength { get; set; } = 1000;

        /// <summary>Minutes between trim operations.</summary>
        [Range(1, int.MaxValue)]
        public int TrimTimeMinutes { get; set; } = 5;

        /// <summary>How entries are removed from the stream.</summary>
        public RedisStreamTrimStrategy TrimStrategy { get; set; } = RedisStreamTrimStrategy.AcknowledgedOnly;
    }
}
```

In `Universley.OrleansContrib.StreamsProvider.Redis/Universley.OrleansContrib.StreamsProvider.Redis.csproj`, add before `</Project>`:

```xml
	<ItemGroup>
		<InternalsVisibleTo Include="RedisStreamsProvider.UnitTests" />
	</ItemGroup>
```

- [ ] **Step 2: Keep the existing trim tests on the legacy strategy and add the new unit tests**

In `RedisStreamsProvider.UnitTests/RedisStreamReceiverTrimTests.cs`, change the options line in the constructor to:

```csharp
            _receiverOptions = new RedisStreamReceiverOptions { TrimTimeMinutes = 1, MaxStreamLength = 128, TrimStrategy = RedisStreamTrimStrategy.MaxLength };
```

Then add, inside the class:

```csharp
        [Fact]
        public void TrimStrategy_DefaultsToAcknowledgedOnly()
        {
            Assert.Equal(RedisStreamTrimStrategy.AcknowledgedOnly, new RedisStreamReceiverOptions().TrimStrategy);
        }

        [Theory]
        [InlineData(null, 0, null, null)]          // group never delivered anything
        [InlineData("0-0", 0, null, null)]         // group created, nothing delivered yet
        [InlineData("9-0", 0, null, "9-0")]        // everything delivered is acknowledged
        [InlineData("9-0", 2, "5-0", "5-0")]       // oldest unacknowledged entry bounds the trim
        public void GetAcknowledgedTrimId_ReturnsOldestEntryStillNeeded(string? lastDeliveredId, int pendingCount, string? lowestPendingId, string? expected)
        {
            var lowest = lowestPendingId is null ? RedisValue.Null : (RedisValue)lowestPendingId;

            var result = RedisStreamReceiver.GetAcknowledgedTrimId(lastDeliveredId, pendingCount, lowest);

            Assert.Equal(expected, result?.ToString());
        }
```

- [ ] **Step 3: Add the time and logging test packages to the integration project**

```bash
dotnet add RedisStreamsProvider.IntegrationTests package Microsoft.Extensions.TimeProvider.Testing --version 10.3.0
dotnet add RedisStreamsProvider.IntegrationTests package Microsoft.Extensions.Diagnostics.Testing --version 10.3.0
```

- [ ] **Step 4: Write the integration tests**

Create `RedisStreamsProvider.IntegrationTests/TrimmingTests.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace RedisStreamsProvider.IntegrationTests;

[Collection(RedisCollection.Name)]
public sealed class TrimmingTests(RedisFixture redis)
{
    private static readonly TimeSpan PastTrimInterval = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task Default_trimming_never_deletes_unacknowledged_entries()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var harness = new ProviderHarness(redis.Connection, new RedisStreamReceiverOptions { MaxStreamLength = 10, TrimTimeMinutes = 1 });
        var receiver = harness.CreateReceiver(time);
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        await harness.PublishAsync(Enumerable.Range(0, 250).Select(i => new TestEvent(i, "e")).ToArray());
        var read = await ProviderHarness.ReadAsync(receiver, expected: 250, maxCount: 250);

        // The oldest 50 stay unacknowledged, e.g. a slow stream sharing the queue with fast ones.
        await receiver.MessagesDeliveredAsync(read.Skip(50).ToList());
        time.Advance(PastTrimInterval);
        await receiver.TrimStreamIfNeeded();

        var remaining = (await harness.Database.StreamRangeAsync(harness.Key)).Select(e => e.Id.ToString()).ToHashSet();
        Assert.All(read.Take(50).Select(ProviderHarness.EntryId), id => Assert.Contains(id, remaining));
    }

    [Fact]
    public async Task Default_trimming_deletes_acknowledged_entries()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var harness = new ProviderHarness(redis.Connection, new RedisStreamReceiverOptions { TrimTimeMinutes = 1 });
        var receiver = harness.CreateReceiver(time);
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        await harness.PublishAsync(Enumerable.Range(0, 250).Select(i => new TestEvent(i, "e")).ToArray());
        var read = await ProviderHarness.ReadAsync(receiver, expected: 250, maxCount: 250);

        await receiver.MessagesDeliveredAsync(read.Take(200).ToList());
        time.Advance(PastTrimInterval);
        await receiver.TrimStreamIfNeeded();

        var remaining = (await harness.Database.StreamRangeAsync(harness.Key)).Select(e => e.Id.ToString()).ToHashSet();
        Assert.True(remaining.Count < 250, $"Expected acknowledged entries to be trimmed, but {remaining.Count} remain.");
        Assert.All(read.Skip(200).Select(ProviderHarness.EntryId), id => Assert.Contains(id, remaining));
    }

    [Fact]
    public async Task Default_trimming_warns_when_the_backlog_exceeds_MaxStreamLength()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logger = new FakeLogger<RedisStreamReceiver>();
        var harness = new ProviderHarness(redis.Connection, new RedisStreamReceiverOptions { MaxStreamLength = 10, TrimTimeMinutes = 1 });
        var receiver = harness.CreateReceiver(time, logger);
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        await harness.PublishAsync(Enumerable.Range(0, 20).Select(i => new TestEvent(i, "e")).ToArray());
        await ProviderHarness.ReadAsync(receiver, expected: 20);

        time.Advance(PastTrimInterval);
        await receiver.TrimStreamIfNeeded();

        Assert.Contains(logger.Collector.GetSnapshot(),
            r => r.Level == LogLevel.Warning && r.Message.Contains("consumers may be falling behind"));
    }
}
```

- [ ] **Step 5: Run them and watch them fail**

Run: `dotnet test RedisStreamsProvider.UnitTests`
Expected: build FAILS with `CS0117: 'RedisStreamReceiver' does not contain a definition for 'GetAcknowledgedTrimId'`.

Run: `dotnet test RedisStreamsProvider.IntegrationTests --filter "FullyQualifiedName~TrimmingTests"`
Expected:
- `Default_trimming_never_deletes_unacknowledged_entries` FAILS: the legacy `MAXLEN ~ 10` removes the first 100-entry block, which includes the 50 unacknowledged entries.
- `Default_trimming_warns_when_the_backlog_exceeds_MaxStreamLength` FAILS: there is no warning.
- `Default_trimming_deletes_acknowledged_entries` may already pass.

- [ ] **Step 6: Implement**

In `Universley.OrleansContrib.StreamsProvider.Redis/RedisStreamReceiver.cs`, replace the whole `TrimStreamIfNeeded` method with:

```csharp
        public virtual async Task TrimStreamIfNeeded()
        {
            if (_timeProvider.GetUtcNow() - _lastTrimTime > TimeSpan.FromMinutes(_receiverOptions.TrimTimeMinutes))
            {
                try
                {
                    var trimmed = _receiverOptions.TrimStrategy == RedisStreamTrimStrategy.MaxLength
                        ? await _database.StreamTrimAsync(_queueId.ToString(), _receiverOptions.MaxStreamLength, useApproximateMaxLength: true)
                        : await TrimAcknowledgedEntriesAsync();
                    _lastTrimTime = _timeProvider.GetUtcNow();
                    _logger.LogDebug("Trimmed {Count} entries from stream {QueueId} using {TrimStrategy} at {Time}", trimmed, _queueId, _receiverOptions.TrimStrategy, _lastTrimTime);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error trimming stream {QueueId}", _queueId);
                }
            }
        }

        private async Task<long> TrimAcknowledgedEntriesAsync()
        {
            var key = _queueId.ToString();
            string? lastDeliveredId = null;
            foreach (var group in await _database.StreamGroupInfoAsync(key))
            {
                if (group.Name == GroupName)
                {
                    lastDeliveredId = group.LastDeliveredId;
                }
            }

            var pending = await _database.StreamPendingAsync(key, GroupName);
            var minId = GetAcknowledgedTrimId(lastDeliveredId, pending.PendingMessageCount, pending.LowestPendingMessageId);
            var trimmed = minId is { } id
                ? await _database.StreamTrimByMinIdAsync(key, id, useApproximateMaxLength: true)
                : 0;

            var remaining = await _database.StreamLengthAsync(key);
            if (remaining > _receiverOptions.MaxStreamLength)
            {
                _logger.LogWarning(
                    "Stream {QueueId} still holds {Remaining} entries after trimming, more than MaxStreamLength {MaxStreamLength}; consumers may be falling behind",
                    _queueId, remaining, _receiverOptions.MaxStreamLength);
            }

            return trimmed;
        }

        /// <summary>
        /// Returns the id below which every entry has been delivered and acknowledged, or null when nothing is safe to trim.
        /// Entries up to the group's last-delivered id were delivered; those not in the pending list were acknowledged.
        /// </summary>
        internal static RedisValue? GetAcknowledgedTrimId(string? lastDeliveredId, long pendingCount, RedisValue lowestPendingId)
        {
            if (pendingCount > 0)
            {
                return lowestPendingId;
            }

            if (string.IsNullOrEmpty(lastDeliveredId) || lastDeliveredId == "0-0")
            {
                return null;
            }

            return lastDeliveredId;
        }
```

- [ ] **Step 7: Document the option**

In `README.md`, replace the code block under `### RedisStreamReceiverOptions` with:

````markdown
```csharp
services.AddOptions<RedisStreamReceiverOptions>("RedisStream")
    .Configure(options =>
    {
        // How old entries are removed. Default: AcknowledgedOnly.
        //   AcknowledgedOnly - delete only delivered and acknowledged entries; never drops undelivered events (Redis 6.2+).
        //   MaxLength        - legacy: cap the stream at ~MaxStreamLength entries, even if they were not delivered yet.
        options.TrimStrategy = RedisStreamTrimStrategy.AcknowledgedOnly;
        // MaxLength: entries kept after trimming. AcknowledgedOnly: backlog size that triggers a warning log. Default: 1000
        options.MaxStreamLength = 1000;
        // Interval in minutes between stream trim operations. Default: 5
        options.TrimTimeMinutes = 5;
    });
```
````

- [ ] **Step 8: Run all tests**

Run: `dotnet test RedisStreamsProvider.UnitTests`
Expected: `Passed: 47`, `Failed: 0`.

Run: `dotnet test RedisStreamsProvider.IntegrationTests`
Expected: all pass (11 tests).

Run: `dotnet build Universley.OrleansContrib.StreamsProvider.Redis --no-incremental 2>&1 | grep -E "RedisStream(Receiver|ReceiverOptions|TrimStrategy)\.cs.*warning" || echo "no new warnings"`
Expected: `no new warnings`.

- [ ] **Step 9: Commit**

```bash
git add Universley.OrleansContrib.StreamsProvider.Redis RedisStreamsProvider.UnitTests/RedisStreamReceiverTrimTests.cs RedisStreamsProvider.IntegrationTests README.md
git commit -m "fix: trim only acknowledged stream entries by default"
```

---

### Task 10: End-to-end test for losing a silo mid-stream

**Files:**
- Create: `RedisStreamsProvider.IntegrationTests/Cluster/SiloFailoverTests.cs`

**Interfaces:**
- Consumes: `ClusterFixture`, `ClusterCollection.Name`, `ClusterFixture.ProviderName`, `EventCollectorGrain.StreamNamespace`, `ReceivedEvents.For` (Task 3); `Eventually.WaitUntilAsync` (Task 2).
- Produces: nothing.

- [ ] **Step 1: Write the test**

`RedisStreamsProvider.IntegrationTests/Cluster/SiloFailoverTests.cs`:

```csharp
using Orleans;
using Orleans.Runtime;

namespace RedisStreamsProvider.IntegrationTests.Cluster;

// Gets its own ClusterFixture because it stops a silo; sharing a cluster with other tests would break them.
[Collection(ClusterCollection.Name)]
public sealed class SiloFailoverTests(ClusterFixture fixture) : IClassFixture<ClusterFixture>
{
    [Fact]
    public async Task No_events_are_lost_when_a_silo_leaves_mid_stream()
    {
        var key = $"failover-{Guid.NewGuid():N}";
        var stream = fixture.Cluster.Client.GetStreamProvider(ClusterFixture.ProviderName)
            .GetStream<int>(StreamId.Create(EventCollectorGrain.StreamNamespace, key));

        for (var i = 0; i < 25; i++)
        {
            await stream.OnNextAsync(i);
        }

        await fixture.Cluster.StopSiloAsync(fixture.Cluster.SecondarySilos[0]);

        for (var i = 25; i < 50; i++)
        {
            await stream.OnNextAsync(i);
        }

        // At-least-once: duplicates are allowed, gaps are not.
        await Eventually.WaitUntilAsync(() => ReceivedEvents.For(key).Distinct().Count() >= 50, TimeSpan.FromSeconds(60));
        Assert.Equal(Enumerable.Range(0, 50), ReceivedEvents.For(key).Distinct().Order());
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test RedisStreamsProvider.IntegrationTests --filter "FullyQualifiedName~SiloFailoverTests"`
Expected: PASS. This is a regression guard for Tasks 5–8, so it should pass on the first run. If it fails or times out, stop and use superpowers:systematic-debugging. A gap here is exactly the production failure this plan exists to prevent. Do not raise the timeout to make it pass.

- [ ] **Step 3: Run the full suite through the build**

Run: `./build.sh Test`
Expected: both test projects pass (47 unit tests, 12 integration tests).

- [ ] **Step 4: Commit**

```bash
git add RedisStreamsProvider.IntegrationTests/Cluster/SiloFailoverTests.cs
git commit -m "test: verify no stream events are lost when a silo leaves"
```

---

### Task 11: Document guarantees and limitations

**Files:**
- Modify: `README.md` (new sections after `## Configuration Options`, and the `### Supported Frameworks` section)

**Interfaces:**
- Consumes: behaviors delivered by Tasks 4–9.
- Produces: nothing.

- [ ] **Step 1: Add the Redis requirement**

In `README.md`, directly below the `### Supported Frameworks` list, add:

```markdown
### Requirements
- Redis 6.2 or later (the default trim strategy uses `XTRIM MINID`).
```

- [ ] **Step 2: Add the guarantees and limitations sections**

Do not add a section about migrating to `Microsoft.Orleans.Streaming.Redis`; the owner has decided that is premature.

In `README.md`, insert directly before `## Dependencies`:

````markdown
## Delivery Guarantees

- **At-least-once.** An entry is acknowledged only after Orleans has delivered it. If a silo stops or crashes, the silo that takes over its queue redelivers everything that was read but not acknowledged. Make consumers idempotent; duplicates are possible, gaps are not.
- **Publish failures surface to the producer.** If Redis rejects a write, `OnNextAsync` throws, so the producer can retry.
- **No silent trimming of undelivered events.** With the default `TrimStrategy.AcknowledgedOnly` the stream grows while consumers are behind, and a warning is logged once it passes `MaxStreamLength`. Watch the stream length in Redis (`XLEN`) if memory matters.
- **Self-healing.** If a stream key disappears (Redis restart without persistence, failover, eviction), the receiver recreates its consumer group and carries on.
- **Unreadable entries are skipped.** An entry without the expected fields is logged at error level and acknowledged, so it cannot block the entries around it.

## Limitations

- Events are matched by **short type name**. A subscriber to `GetStream<T>` receives only events whose runtime type has the same `Name` as `T`. Subscribing with a base class or interface receives nothing.
- Payloads are serialized with `System.Text.Json`, so event types must round-trip through it.
- Orleans `RequestContext` is not carried with events.
- Streams are not rewindable: subscribers cannot resume from an earlier sequence token.
````

- [ ] **Step 3: Check the result renders sensibly**

Run: `grep -n "^## \|^### " README.md`
Expected:
- `Requirements` appears directly after `Supported Frameworks`.
- `Delivery Guarantees` and `Limitations` appear, in that order, between `RedisStreamReceiverOptions` and `Dependencies`.
- All existing headings are unchanged.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: document delivery guarantees and limitations"
```

---

## Done Criteria

- `./build.sh Test` passes locally and in CI on the .NET 10 SDK, with both test projects.
- Every finding F1–F11 in **Background** is covered by the task named in its row.
- The wire format is unchanged. A silo on the previous package version and one on this branch can consume the same queue during a rolling deploy.
- Revisit when `Microsoft.Orleans.Streaming.Redis` publishes a beta; migration documentation is deferred until then.
