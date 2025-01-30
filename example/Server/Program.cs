using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streams;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

var builder = new HostBuilder()
    .UseOrleans(silo =>
    {
        silo.UseLocalhostClustering();
        silo.Services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            return ConnectionMultiplexer.Connect("localhost");
        });
        silo.ConfigureLogging(logging => logging.AddConsole());
        silo.AddMemoryGrainStorage("PubSubStore");
        silo.AddPersistentStreams("RedisStream", RedisStreamFactory.Create, null);
        silo.AddMemoryGrainStorageAsDefault();
    }).UseConsoleLifetime();
builder.ConfigureServices(services =>
{
    services.AddOptions<HashRingStreamQueueMapperOptions>("RedisStream")
        .Configure(options =>
        {
            options.TotalQueueCount = 8;
        });
    services.AddOptions<SimpleQueueCacheOptions>("RedisStream");
});
using IHost host = builder.Build();
await host.RunAsync();


[ImplicitStreamSubscription("numbergenerator")]
public class NumberGeneratorGrain : Grain, INumberGeneratorGrain, IAsyncObserver<int>
{
    private ILogger<NumberGeneratorGrain> _logger { get; }

    public NumberGeneratorGrain(ILogger<NumberGeneratorGrain> logger)
    {
        _logger = logger;
    }

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        var streamProvider = this.GetStreamProvider("RedisStream");
        var streamId = StreamId.Create("numbergenerator", this.GetPrimaryKeyString());
        var stream = streamProvider.GetStream<int>(streamId);
        await stream.SubscribeAsync(this);
        await base.OnActivateAsync(ct);
    }
    public Task OnCompletedAsync()
    {
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(Exception ex)
    {
        _logger.LogError("Error: {Error}", ex.Message);
        return Task.CompletedTask;
    }

    public async Task OnNextAsync(int item, StreamSequenceToken? token = null)
    {
        _logger.LogInformation("Received number {Number}", item);
        await Task.Delay(2000);

        var newStreamId = StreamId.Create("numbergenerator", "consecutive-back");
        var newStream = this.GetStreamProvider("RedisStream").GetStream<int>(newStreamId);
        _logger.LogInformation("Sending number {Number} to new stream", item);
        await newStream.OnNextAsync(item);
        
    }
}



public interface INumberGeneratorGrain : IGrainWithStringKey { }
