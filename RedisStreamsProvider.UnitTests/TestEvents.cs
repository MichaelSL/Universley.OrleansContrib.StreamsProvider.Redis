namespace RedisStreamsProvider.UnitTests
{
    public interface ITestEvent
    {
        int Id { get; }
    }

    [GenerateSerializer]
    public record TestEvent([property: Id(0)] int Id, [property: Id(1)] string Name) : ITestEvent;

    [GenerateSerializer]
    public record DerivedTestEvent(int Id, string Name, [property: Id(0)] string Extra) : TestEvent(Id, Name);

    [GenerateSerializer]
    public record Envelope<T>([property: Id(0)] T Value);

    /// <summary>Not known to the Orleans serializer.</summary>
    public record UnserializableEvent(int Id);

    namespace Other
    {
        /// <summary>Has the same short name as <see cref="UnitTests.TestEvent"/>.</summary>
        [GenerateSerializer]
        public record TestEvent([property: Id(0)] int Id);
    }
}
