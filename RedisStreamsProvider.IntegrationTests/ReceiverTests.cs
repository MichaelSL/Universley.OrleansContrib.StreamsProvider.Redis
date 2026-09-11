using StackExchange.Redis;
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
}
