# Universley.OrleansContrib.StreamsProvider.Redis

## Summary
This library provides an integration of Redis Streams with Microsoft Orleans, allowing you to use Redis as a streaming provider within your Orleans applications. It enables seamless communication and data streaming between Orleans grains and external clients using Redis Streams.

### Supported Frameworks
- .NET 8.0
- .NET 10.0

### Requirements
- Redis 6.2 or later is recommended, because trimming only acknowledged entries uses `XTRIM MINID`.
- Redis 6.0 also works, but the default `TrimStrategy.Auto` falls back to `MaxLength` there and logs a warning: when consumers fall behind, entries can be trimmed before they are delivered (see [Configuration Options](#configuration-options)). This includes Azure Cache for Redis Basic, Standard and Premium, which run Redis 6.0.

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
            options.BacklogWarningLength = 1000; // warn when more entries than this are still in the stream after trimming
            options.TrimTimeMinutes = 5;         // how often the stream is trimmed
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
        // How old entries are removed. Default: Auto.
        //   Auto             - AcknowledgedOnly on Redis 6.2+; on older servers MaxLength, with a warning logged once.
        //   AcknowledgedOnly - delete only delivered and acknowledged entries; never drops undelivered events.
        //                      Needs Redis 6.2+; on older servers every trim logs an error and nothing is trimmed.
        //   MaxLength        - legacy: cap the stream at ~MaxStreamLength entries, even if they were not delivered yet.
        options.TrimStrategy = RedisStreamTrimStrategy.Auto;
        // AcknowledgedOnly (and Auto on 6.2+): log a warning when more entries than this are still in the stream after trimming. Default: 1000
        options.BacklogWarningLength = 1000;
        // MaxLength (and Auto before 6.2): roughly how many entries are kept after trimming. Default: 1000
        options.MaxStreamLength = 1000;
        // Interval in minutes between stream trim operations. Default: 5
        options.TrimTimeMinutes = 5;
    });
```

### RedisStreamPublisherOptions

Applies to publishing, from silos and clients alike. Receivers read every payload format, whatever this is set to.

```csharp
services.AddOptions<RedisStreamPublisherOptions>("RedisStream")
    .Configure(options =>
    {
        // How published events are serialized. Default: OrleansCodec
        //   OrleansCodec - the Orleans serializer configured on the silo or client, as for grain calls.
        //   Json         - legacy System.Text.Json format; only for rolling deploys from earlier versions.
        options.PayloadFormat = RedisStreamPayloadFormat.OrleansCodec;
    });
```

## Event Serialization

Events are serialized with the Orleans serializer configured on the silo or client, so event types need the same treatment as grain call arguments:

```csharp
[GenerateSerializer]
[Alias("MyApp.OrderPlaced")]
public sealed record OrderPlaced([property: Id(0)] Guid OrderId, [property: Id(1)] decimal Total) : IOrderEvent;
```

- Events are kept in Redis until delivered, so give event types an `[Alias]`: renaming or moving a type without one makes entries already in the stream unreadable.
- To use another format, configure it on the Orleans serializer, for example `AddJsonSerializer` or `AddProtobufSerializer`; this provider uses whatever Orleans uses.
- A subscriber receives every event assignable to its stream type, so `GetStream<IOrderEvent>` receives `OrderPlaced`.
- If the Orleans serializer cannot serialize an event, `OnNextAsync` throws and nothing in the call is written.

## Delivery Guarantees

- **At-least-once.** An entry is acknowledged only after Orleans has delivered it. If a silo stops or crashes, the silo that takes over its queue redelivers everything that was read but not acknowledged. Make consumers idempotent; duplicates are possible.
- **No gaps, with two exceptions.** With `TrimStrategy.MaxLength`, or `Auto` on Redis older than 6.2, entries can be trimmed before they are delivered when consumers fall behind. And if a stream key is lost (Redis restart without persistence, failover to an empty replica, or eviction), the events in it are gone: self-healing resumes delivery but cannot restore them. Set `maxmemory-policy noeviction` on the Redis instance so Redis never evicts stream keys.
- **Publish failures surface to the producer.** If Redis rejects a write, `OnNextAsync` throws, so the producer can retry. The events of one call are written in a single `MULTI`/`EXEC` transaction, so a failed call normally leaves none of them in the stream. A retry can still duplicate them if the connection drops after Redis applied the write but before its reply arrived.
- **No silent trimming of undelivered events.** With `TrimStrategy.AcknowledgedOnly`, and the default `Auto` on Redis 6.2 or later, the stream grows while consumers are behind, and a warning is logged once it passes `BacklogWarningLength`. Watch the stream length in Redis (`XLEN`) if memory matters.
- **Self-healing.** If a stream key disappears (Redis restart without persistence, failover, eviction), the receiver recreates its consumer group and carries on.
- **Unreadable entries are skipped.** An entry without the expected fields, or whose payload the Orleans serializer cannot read (for example an event type the reading silo does not have), is logged at error level and acknowledged (or, if that acknowledgement fails, retried with the next one), so it cannot block the entries around it. Deploy new event types to every silo before publishing them. Any other error while reading an entry is logged and leaves the entries read with it pending, so they are delivered after the next restart or queue handoff instead of being lost.

## Limitations

- Entries in the legacy JSON format (written by earlier versions, or with `PayloadFormat = Json`) are still matched by **short type name**: a subscriber to `GetStream<T>` receives only those whose runtime type has the same `Name` as `T`, and an event whose JSON cannot be deserialized into `T` fails at delivery time and faults the subscription.
- Orleans `RequestContext` is not carried with events.
- Streams are not rewindable: subscribers cannot resume from an earlier sequence token.
- Stream keys are derived only from the provider name and queue number, with no `ServiceId` prefix. Two services or environments that share one Redis database and use the same provider name consume each other's events. Give each its own Redis database or a different provider name.

## Upgrading from earlier versions

### Orleans serializer (#3)

Events are now serialized with the Orleans serializer. Silos and clients on earlier versions cannot read entries in the new format: an earlier version that reads one fails its delivery and faults the subscription. For a rolling deploy:

1. Deploy this version with `RedisStreamPublisherOptions.PayloadFormat = RedisStreamPayloadFormat.Json` on every silo and client.
2. Once nothing older reads the same Redis streams, remove the setting (or set `OrleansCodec`) and deploy again.

Event types must be serializable by Orleans: mark them `[GenerateSerializer]` with `[Id]` members (see [Event Serialization](#event-serialization)), or register a fallback serializer such as `AddJsonSerializer`. Entries already in Redis in the JSON format are still delivered.

### Production hardening

The first receiver on this version drains each queue's whole pending list before reading new entries. Events that earlier versions left stuck there are delivered late, and possibly out of order relative to newer events. Entries that were trimmed while still pending are logged at error level and skipped.

Trimming now defaults to `TrimStrategy.Auto`. On Redis 6.2 or later it deletes only acknowledged entries and ignores `MaxStreamLength`, so the stream is no longer capped at that length. To keep the old cap, set `TrimStrategy = RedisStreamTrimStrategy.MaxLength`. On older servers `Auto` keeps the old cap and logs a warning at startup.

A failed publish now throws from the producer's `OnNextAsync` instead of being logged and dropped. Producers that never expected an exception there should catch it and retry or report it.

See [CHANGELOG.md](CHANGELOG.md) for the full list of changes in each release.

## Dependencies
- Microsoft.Orleans.Streaming 10.0.1
- Microsoft.Orleans.Sdk 10.0.1
- StackExchange.Redis 2.11.3

## Credit
This library is based on the original repository by [sammychinedu2ky](https://github.com/sammychinedu2ky/RedisStreamsInOrleans).