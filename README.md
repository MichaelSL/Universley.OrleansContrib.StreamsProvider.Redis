# Universley.OrleansContrib.StreamsProvider.Redis

## Summary
This library provides an integration of Redis Streams with Microsoft Orleans, allowing you to use Redis as a streaming provider within your Orleans applications. It enables seamless communication and data streaming between Orleans grains and external clients using Redis Streams.

### Supported Frameworks
- .NET 8.0
- .NET 10.0

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
        // Maximum number of messages to keep in the Redis stream before trimming. Default: 1000
        options.MaxStreamLength = 1000;
        // Interval in minutes between stream trim operations. Default: 5
        options.TrimTimeMinutes = 5;
    });
```

## Dependencies
- Microsoft.Orleans.Streaming 10.0.1
- Microsoft.Orleans.Sdk 10.0.1
- StackExchange.Redis 2.11.3

## Credit
This library is based on the original repository by [sammychinedu2ky](https://github.com/sammychinedu2ky/RedisStreamsInOrleans).