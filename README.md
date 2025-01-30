# Universley.OrleansContrib.StreamsProvider.Redis

## Summary
This library provides an integration of Redis Streams with Microsoft Orleans, allowing you to use Redis as a streaming provider within your Orleans applications. It enables seamless communication and data streaming between Orleans grains and external clients using Redis Streams.

## How to Use the Redis Provider with Orleans

### 1. With Grain as a Client
To use Redis Streams with a grain as a client, follow these steps:

1. Install the necessary NuGet packages:
    ```sh
    dotnet add package Orleans.Streaming.Redis
    ```

2. Configure the Redis stream provider in your Orleans silo configuration:
    ```csharp
    var host = new SiloHostBuilder()
        .AddRedisStreams("RedisProvider", options =>
        {
            options.ConnectionString = "your_redis_connection_string";
        })
        .Build();
    ```

3. In your grain, use the stream provider to send and receive messages:
    ```csharp
    public class MyGrain : Grain, IMyGrain
    {
        private IAsyncStream<string> _stream;

        public override async Task OnActivateAsync()
        {
            var streamProvider = GetStreamProvider("RedisProvider");
            _stream = streamProvider.GetStream<string>(this.GetPrimaryKey(), "streamNamespace");
            await base.OnActivateAsync();
        }

        public async Task SendMessage(string message)
        {
            await _stream.OnNextAsync(message);
        }

        public async Task ReceiveMessages()
        {
            var handle = await _stream.SubscribeAsync((message, token) =>
            {
                Console.WriteLine($"Received message: {message}");
                return Task.CompletedTask;
            });
        }
    }
    ```

### 2. With External Client
To use Redis Streams with an external client, follow these steps:

1. Install the StackExchange.Redis package:
    ```sh
    dotnet add package StackExchange.Redis
    ```

2. Connect to the Redis server and interact with the stream:
    ```csharp
    using StackExchange.Redis;

    var redis = ConnectionMultiplexer.Connect("your_redis_connection_string");
    var db = redis.GetDatabase();

    // Add a message to the stream
    var messageId = await db.StreamAddAsync("mystream", "message", "Hello, Redis!");

    // Read messages from the stream
    var messages = await db.StreamReadAsync("mystream", "0-0");
    foreach (var message in messages)
    {
        Console.WriteLine($"Message ID: {message.Id}, Values: {string.Join(", ", message.Values)}");
    }
    ```

## Credit
This library is based on the original repository by [sammychinedu2ky](https://github.com/sammychinedu2ky/RedisStreamsInOrleans).