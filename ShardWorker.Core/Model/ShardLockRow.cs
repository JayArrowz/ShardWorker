using System;

namespace ShardWorker.Core.Model;

/// <summary>A single lock row as returned by <see cref="Interface.IShardLockQueryProvider"/>.</summary>
public sealed record ShardLockRow(int ShardIndex, string InstanceId, DateTimeOffset ExpiresAt);
