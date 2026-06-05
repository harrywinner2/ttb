using System.Collections.Concurrent;
using LabelVerifier.Models;

namespace LabelVerifier.Services;

/// <summary>In-memory store of batch jobs, with simple age-based eviction.</summary>
public sealed class BatchJobStore
{
    private readonly ConcurrentDictionary<string, BatchJob> _jobs = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    public BatchJob Create(int total)
    {
        Evict();
        var job = new BatchJob { Total = total };
        _jobs[job.Id] = job;
        return job;
    }

    public BatchJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    private void Evict()
    {
        var cutoff = DateTimeOffset.UtcNow - Ttl;
        foreach (var kv in _jobs)
            if (kv.Value.CreatedAt < cutoff)
                _jobs.TryRemove(kv.Key, out _);
    }
}
