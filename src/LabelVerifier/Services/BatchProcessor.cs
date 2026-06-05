using LabelVerifier.Models;

namespace LabelVerifier.Services;

/// <summary>
/// Runs a batch job's products through the verifier in parallel, with a bounded degree of
/// parallelism, writing each result into the job as it finishes. This decouples throughput
/// from any single HTTP request (which is what timed out on big synchronous batches) and is
/// the prototype stand-in for a queue + autoscaling worker pool.
/// </summary>
public sealed class BatchProcessor
{
    private readonly LabelVerificationService _svc;
    private readonly ILogger<BatchProcessor> _log;
    private readonly int _maxParallel;

    public BatchProcessor(LabelVerificationService svc, ILogger<BatchProcessor> log, IConfiguration config)
    {
        _svc = svc;
        _log = log;
        _maxParallel = int.TryParse(config["Batch:MaxParallelism"], out var n) && n > 0 ? n : 8;
    }

    public int MaxParallelism => _maxParallel;

    /// <summary>Starts processing in the background and returns immediately.</summary>
    public void Start(BatchJob job, IReadOnlyList<ProductInput> products, bool forceFallback)
    {
        job.Status = JobStatus.Running;
        _ = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    products,
                    new ParallelOptions { MaxDegreeOfParallelism = _maxParallel },
                    async (product, ct) =>
                    {
                        try
                        {
                            job.Bag.Add(await _svc.VerifyProductAsync(product.Images, product.App, forceFallback, ct));
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "Batch product {Name} failed", product.App.FileName);
                            job.Bag.Add(new VerificationResult
                            {
                                FileName = product.App.FileName,
                                Overall = CheckStatus.Fail,
                                Error = "Could not process this product: " + ex.Message
                            });
                        }
                    });
            }
            finally
            {
                job.Status = JobStatus.Completed;
            }
        });
    }
}
