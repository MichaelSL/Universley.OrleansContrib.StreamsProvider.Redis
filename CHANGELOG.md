# Changelog

Versions are `year.month.build`, assigned by CI when a change reaches `main`. Releases before this one have no entries.

## Unreleased

Events are serialized with the Orleans serializer instead of `System.Text.Json` ([#3](https://github.com/MichaelSL/Universley.OrleansContrib.StreamsProvider.Redis/issues/3)). **Breaking:** read the upgrade notes before a rolling deploy.

### Upgrade notes

- **Silos and clients on earlier versions cannot read the new format.** During a rolling deploy, set `PayloadFormat = RedisStreamPayloadFormat.Json` on every silo and client running this version, and switch it back to `OrleansCodec` (the default) once nothing older reads the same Redis streams. An earlier version that reads an entry in the new format fails delivery of it and faults the subscription.
- **Event types must be serializable by Orleans**, like grain call arguments: mark them `[GenerateSerializer]` with `[Id]` on their members, or register a fallback such as `AddJsonSerializer`. Publishing an event Orleans cannot serialize throws from `OnNextAsync`, and nothing in the call is written.
- **Event types are now stored in Redis by their Orleans type name.** Renaming or moving an event type makes entries still in the stream unreadable; give event types an `[Alias]` so they can be renamed safely.
- **Delivery is by type, not by short type name.** Subscribers to a base class or interface now receive the events that derive from or implement it, and events whose types merely share a short name (`Envelope<int>` and `Envelope<string>`, or `Foo` in two namespaces) are no longer delivered to each other's subscribers.
- Entries already in Redis in the old JSON format are still read and delivered as before, by short type name.

### Added

- `RedisStreamReceiverOptions.PayloadFormat`, with `OrleansCodec` (default) and the legacy `Json`. It applies to publishing; receivers read both formats. An undefined value throws when the adapter is created.

### Changed

- A payload the Orleans serializer cannot read, or an entry in a payload format this version does not know, is treated as an unreadable entry: logged at error level with the exception, acknowledged and skipped, instead of faulting the subscription at delivery.
- `RedisStreamFactory` and `RedisStreamAdapter` take the Orleans `Serializer`. `RedisStreamFactory.Create` resolves it from the service provider, so configuration through `AddPersistentStreams` is unchanged.
- The unreadable-entry error log now says why the entry could not be read, such as which field is missing.

### Removed

- `RedisStreamBatchContainer(StreamEntry)`, and the container's `EventType` and `Data` properties. Batch containers are created only by the receiver; events are read with `GetEvents<T>()` as before.
- The public `RedisStreamReceiver` constructor. Receivers are created by the adapter (`IQueueAdapter.CreateReceiver`).

## 2026.10.26

Production hardening: no silently lost or stuck events, and tests against a real Redis and Orleans cluster.

### Upgrade notes

- **Trimming defaults to the new `TrimStrategy.Auto`.** On Redis 6.2 or later it deletes only acknowledged entries and ignores `MaxStreamLength`, so a stream is no longer capped at that length while consumers are behind; a warning is logged once it holds more than `BacklogWarningLength` entries. On older servers, including Azure Cache for Redis Basic, Standard and Premium (Redis 6.0), it keeps the old `MaxStreamLength` cap and logs a warning at startup. Set `TrimStrategy = RedisStreamTrimStrategy.MaxLength` to keep the old behavior everywhere.
- **Failed publishes throw.** `OnNextAsync` now fails when Redis rejects a write, instead of logging and dropping the event. The events of one call are written in a single transaction, so a failed call normally leaves none of them in the stream; a retry can still duplicate them if the reply is lost after Redis applied the write.
- **Stuck events are delivered.** The first silo on this version drains each queue's whole pending list, so events earlier versions left stuck there arrive late, possibly out of order relative to newer events. Entries trimmed while still pending are logged at error level and skipped.
- The Redis wire format is unchanged: silos on the previous version and on this one can share queues during a rolling deploy.

### Added

- `RedisStreamTrimStrategy` with `Auto` (default), `AcknowledgedOnly` and `MaxLength`, set through `RedisStreamReceiverOptions.TrimStrategy`.
- `RedisStreamReceiverOptions.BacklogWarningLength` (default 1000).
- Delivery guarantees, limitations and upgrade notes in the README.

### Fixed

- On StackExchange.Redis 3 the startup check for `XTRIM MINID` support could not read the server version, because a raw `INFO` needs admin mode there. It now reads `INFO` from a script when the raw command fails.
- A failed publish was swallowed, so the producer believed the event was sent.
- One unreadable entry, or one deleted while pending, failed the whole read and left every entry in it pending forever. Unreadable entries are now logged, acknowledged and skipped.
- After a restart or queue handoff only the first `maxCount` pending entries were redelivered; the rest stayed pending forever. Entries whose read reply was lost to a timeout are now re-read as well.
- If the stream key disappeared (Redis restart without persistence, failover, eviction), every read failed with `NOGROUP` forever. The receiver now recreates its consumer group.
- The consumer group was created at `$`, skipping entries published before a queue's first receiver started. It now starts at `0`.
- Trimming capped the stream at `MaxStreamLength` whether or not entries had been delivered, silently dropping events for consumers that fell behind.
- A trim that kept failing was retried on every poll instead of once per trim interval.
- Acknowledgements that failed were never retried; they are now sent again with the next one.

### Changed

- A delivered batch is acknowledged with one `XACK` instead of one per entry.
- The events of one publish call are written with one `MULTI`/`EXEC` transaction instead of one `XADD` round trip each.
- `RedisStreamBatchContainer(StreamEntry)` throws `ArgumentException` for an entry it cannot read, instead of `ArgumentNullException` for a missing field and `FormatException` or `OverflowException` for a malformed id.
- The build and CI use the .NET 10 SDK.
- Works with StackExchange.Redis 2.x and 3.x. The package still declares 2.11.3 as its minimum, and CI runs the tests against 3.x as well.
