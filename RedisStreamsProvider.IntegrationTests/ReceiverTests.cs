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

    [Fact]
    public async Task Entry_deleted_while_pending_does_not_block_the_entries_around_it()
    {
        var harness = new ProviderHarness(redis.Connection);
        var previousOwner = harness.CreateReceiver();
        await previousOwner.Initialize(TimeSpan.FromSeconds(5));
        await harness.PublishAsync(new TestEvent(1, "a"), new TestEvent(2, "b"), new TestEvent(3, "c"));
        var readBeforeCrash = await ProviderHarness.ReadAsync(previousOwner, expected: 3);
        Assert.Equal(3, readBeforeCrash.Count);
        // previousOwner "crashes" without acknowledging, and the middle entry is deleted while still pending.
        await harness.Database.StreamDeleteAsync(harness.Key, [ProviderHarness.EntryId(readBeforeCrash[1])]);

        var newOwner = harness.CreateReceiver();
        await newOwner.Initialize(TimeSpan.FromSeconds(5));
        var redelivered = await ProviderHarness.ReadAsync(newOwner, expected: 2);
        await newOwner.MessagesDeliveredAsync(redelivered);

        Assert.Equal(new[] { 1, 3 }, redelivered.SelectMany(b => b.GetEvents<TestEvent>()).Select(e => e.Item1.Id));
        var pending = await harness.Database.StreamPendingAsync(harness.Key, "consumer");
        Assert.Equal(0, pending.PendingMessageCount);
    }

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
}
