using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace LabelVerifier.Models;

/// <summary>An in-memory image payload (bytes + content type).</summary>
public readonly record struct ImageBlob(byte[] Bytes, string ContentType);

/// <summary>One product to verify, possibly represented by several images.</summary>
public sealed class ProductInput
{
    public LabelApplication App { get; set; } = new();
    public List<ImageBlob> Images { get; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobStatus { Pending, Running, Completed }

/// <summary>
/// A batch verification job. Results are filled in by background workers as each product
/// completes, so the client can poll for incremental progress instead of holding one long
/// HTTP request open. In-memory for the prototype; production would back this with a durable
/// queue + store (see docs/ENGINEERING-NOTES.md).
/// </summary>
public sealed class BatchJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public int Total { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonIgnore] public ConcurrentBag<VerificationResult> Bag { get; } = new();

    public int Completed => Bag.Count;
}
