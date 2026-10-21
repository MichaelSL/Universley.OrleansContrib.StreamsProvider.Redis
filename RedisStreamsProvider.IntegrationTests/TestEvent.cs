namespace RedisStreamsProvider.IntegrationTests;

[GenerateSerializer]
public sealed record TestEvent([property: Id(0)] int Id, [property: Id(1)] string Name);
