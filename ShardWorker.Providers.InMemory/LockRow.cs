using System;

namespace ShardWorker.Providers.InMemory;

public sealed record LockRow(string InstanceId, DateTimeOffset ExpiresAt);