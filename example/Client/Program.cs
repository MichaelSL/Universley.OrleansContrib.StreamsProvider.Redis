using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streams;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

using IHost host = new HostBuilder()
     .UseOrleansClient(clientBuilder =>
     {
         clientBuilder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
         {
             return ConnectionMultiplexer.Connect("localhost");
         });
         clientBuilder.UseLocalhostClustering();

         clientBuilder.AddPersistentStreams("RedisStream", RedisStreamFactory.Create, null);
         clientBuilder.ConfigureServices(services =>
         {
             services.AddOptions<HashRingStreamQueueMapperOptions>("RedisStream")
                 .Configure(options =>
                 {
                     options.TotalQueueCount = 8;
                 });

         });
     })
     .ConfigureLogging(logging => logging.AddConsole())
     .Build();
await host.StartAsync();

IClusterClient client = host.Services.GetRequiredService<IClusterClient>();
ILogger<Program> logger = host.Services.GetRequiredService<ILogger<Program>>();
var streamProvider = client.GetStreamProvider("RedisStream");

var streamId = StreamId.Create("numbergenerator", "consecutive");
var stream = streamProvider.GetStream<int>(streamId);

var newStreamId = StreamId.Create("numbergenerator", "consecutive-back");
var newStream = streamProvider.GetStream<int>(newStreamId);

await stream.SubscribeAsync((i, token) =>
{
    logger.LogInformation("Received back number {Number}", i);
    return Task.CompletedTask;
});

var task = Task.Run(async () =>
{
    var num = 0;
    while (true)
    {
        logger.LogInformation("Sending number {Number}", num);
        await stream.OnNextAsync(num++);

        if (num == 20)
        {
            break;
        }
        await Task.Delay(1000);
    }
});


Console.ReadLine();