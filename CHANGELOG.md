# Changelog

Versions are `year.month.build`, assigned by CI when a change reaches `main`. Releases before this one have no entries.

## Unreleased

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
