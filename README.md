# Universley.OrleansContrib.StreamsProvider.Redis

## Summary
This library provides an integration of Redis Streams with Microsoft Orleans, allowing you to use Redis as a streaming provider within your Orleans applications. It enables seamless communication and data streaming between Orleans grains and external clients using Redis Streams.

### Supported Frameworks
- .NET 8.0
- .NET 10.0

### Requirements
- Redis 6.2 or later (the default trim strategy uses `XTRIM MINID`).
- On Redis older than 6.2, set `TrimStrategy = RedisStreamTrimStrategy.MaxLength` (see [Configuration Options](#configuration-options)). This includes Azure Cache for Redis Basic, Standard and Premium, which run Redis 6.0.

## How to Use the Redis Provider with Orleans

### 1. Silo (Server) Setup

Install the NuGet package:
```sh
dotnet add package Universley.OrleansContrib.StreamsProvider.Redis
dotnet add package StackExchange.Redis
```

Configure the silo:
```csharp
using Orleans.Configuration;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

var builder = new HostBuilder()
    .UseOrleans(silo =>
    {
        silo.UseLocalhostClustering();
        silo.Services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect("localhost"));
        silo.AddMemoryGrainStorage("PubSubStore");
        silo.AddPersistentStreams("RedisStream", RedisStreamFactory.Create, null);
        silo.AddMemoryGrainStorageAsDefault();
    });

builder.ConfigureServices(services =>
{
    services.AddOptions<HashRingStreamQueueMapperOptions>("RedisStream")
        .Configure(options => { options.TotalQueueCount = 8; });
    services.AddOptions<SimpleQueueCacheOptions>("RedisStream");
    services.AddOptions<RedisStreamReceiverOptions>("RedisStream")
        .Configure(options =>
        {
            options.MaxStreamLength = 1000; // max messages kept in Redis stream before trimming
            options.TrimTimeMinutes = 5;    // how often the stream is trimmed
        });
});
```

### 2. Receiving Messages in a Grain

Use `[ImplicitStreamSubscription]` to have the grain automatically receive messages published to a matching stream namespace:

```csharp
[ImplicitStreamSubscription("my-namespace")]
public class MyGrain : Grain, IMyGrain, IAsyncObserver<string>
{
    private readonly ILogger<MyGrain> _logger;

    public MyGrain(ILogger<MyGrain> logger) => _logger = logger;

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        var streamProvider = this.GetStreamProvider("RedisStream");
        var streamId = StreamId.Create("my-namespace", this.GetPrimaryKeyString());
        var stream = streamProvider.GetStream<string>(streamId);
        await stream.SubscribeAsync(this);
        await base.OnActivateAsync(ct);
    }

    public Task OnNextAsync(string item, StreamSequenceToken? token = null)
    {
        _logger.LogInformation("Received: {Item}", item);
        return Task.CompletedTask;
    }

    public Task OnCompletedAsync() => Task.CompletedTask;
    public Task OnErrorAsync(Exception ex) => Task.CompletedTask;
}
```

### 3. Sending and Receiving from an External Client

```csharp
using Orleans.Configuration;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

using IHost host = new HostBuilder()
    .UseOrleansClient(clientBuilder =>
    {
        clientBuilder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect("localhost"));
        clientBuilder.UseLocalhostClustering();
        clientBuilder.AddPersistentStreams("RedisStream", RedisStreamFactory.Create, null);
        clientBuilder.ConfigureServices(services =>
        {
            services.AddOptions<HashRingStreamQueueMapperOptions>("RedisStream")
                .Configure(options => { options.TotalQueueCount = 8; });
        });
    })
    .Build();

await host.StartAsync();

var client = host.Services.GetRequiredService<IClusterClient>();
var streamProvider = client.GetStreamProvider("RedisStream");

// Publish messages to a stream
var streamId = StreamId.Create("my-namespace", "my-key");
var stream = streamProvider.GetStream<string>(streamId);
await stream.OnNextAsync("Hello, Orleans!");

// Subscribe to a stream
await stream.SubscribeAsync((msg, token) =>
{
    Console.WriteLine($"Received: {msg}");
    return Task.CompletedTask;
});

await host.StopAsync();
```

## Configuration Options

### RedisStreamReceiverOptions

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

## Delivery Guarantees

- **At-least-once.** An entry is acknowledged only after Orleans has delivered it. If a silo stops or crashes, the silo that takes over its queue redelivers everything that was read but not acknowledged. Make consumers idempotent; duplicates are possible.
- **No gaps, with two exceptions.** With `TrimStrategy.MaxLength`, entries can be trimmed before they are delivered when consumers fall behind. And if a stream key is lost (Redis restart without persistence, failover to an empty replica, or eviction), the events in it are gone: self-healing resumes delivery but cannot restore them. Set `maxmemory-policy noeviction` on the Redis instance so Redis never evicts stream keys.
- **Publish failures surface to the producer.** If Redis rejects a write, `OnNextAsync` throws, so the producer can retry. If one call publishes several events and a later one fails, the earlier ones are already in the stream, so a retry can duplicate them.
- **No silent trimming of undelivered events.** With the default `TrimStrategy.AcknowledgedOnly` the stream grows while consumers are behind, and a warning is logged once it passes `MaxStreamLength`. Watch the stream length in Redis (`XLEN`) if memory matters.
- **Self-healing.** If a stream key disappears (Redis restart without persistence, failover, eviction), the receiver recreates its consumer group and carries on.
- **Unreadable entries are skipped.** An entry without the expected fields is logged at error level and acknowledged (or, if that acknowledgement fails, retried with the next one), so it cannot block the entries around it.

## Limitations

- Events are matched by **short type name**. A subscriber to `GetStream<T>` receives only events whose runtime type has the same `Name` as `T`. Subscribing with a base class or interface receives nothing.
- Payloads are serialized with `System.Text.Json`, so event types must round-trip through it.
- Orleans `RequestContext` is not carried with events.
- Streams are not rewindable: subscribers cannot resume from an earlier sequence token.
- Stream keys are derived only from the provider name and queue number, with no `ServiceId` prefix. Two services or environments that share one Redis database and use the same provider name consume each other's events. Give each its own Redis database or a different provider name.
- An event whose JSON cannot be deserialized into `T` fails at delivery time; the unreadable-entry handling covers only entries missing required fields. The provider's failure handler then faults the subscription.

## Upgrading from earlier versions

The first receiver on this version drains each queue's whole pending list before reading new entries. Events that earlier versions left stuck there are delivered late, and possibly out of order relative to newer events. Entries that were trimmed while still pending are logged at error level and skipped.

## Dependencies
- Microsoft.Orleans.Streaming 10.0.1
- Microsoft.Orleans.Sdk 10.0.1
- StackExchange.Redis 2.11.3

## Credit
This library is based on the original repository by [sammychinedu2ky](https://github.com/sammychinedu2ky/RedisStreamsInOrleans).