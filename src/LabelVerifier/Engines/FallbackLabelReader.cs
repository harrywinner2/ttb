using LabelVerifier.Models;

namespace LabelVerifier.Engines;

/// <summary>
/// Tries the primary (cloud vision) engine, and automatically falls back to the
/// offline OCR engine if the primary is unconfigured, errors, or times out.
/// This is the concrete answer to Marcus's firewall warning: the same app keeps
/// working when the cloud ML endpoint is unreachable.
/// </summary>
public sealed class FallbackLabelReader
{
    private readonly AzureOpenAiLabelReader _primary;
    private readonly TesseractLabelReader _fallback;
    private readonly ILogger<FallbackLabelReader> _log;

    public FallbackLabelReader(AzureOpenAiLabelReader primary, TesseractLabelReader fallback, ILogger<FallbackLabelReader> log)
    {
        _primary = primary;
        _fallback = fallback;
        _log = log;
    }

    public string PrimaryName => _primary.Name;
    public bool PrimaryAvailable => _primary.IsAvailable;
    public string FallbackName => _fallback.Name;
    public bool FallbackAvailable => _fallback.IsAvailable;

    /// <param name="forceFallback">Lets the UI demo the offline path on demand.</param>
    public async Task<LabelReading> ReadAsync(byte[] imageBytes, string contentType, bool forceFallback = false, CancellationToken ct = default)
    {
        if (!forceFallback && _primary.IsAvailable)
        {
            try
            {
                return await _primary.ReadAsync(imageBytes, contentType, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "Primary engine failed; falling back to offline OCR.");
            }
        }

        if (_fallback.IsAvailable)
            return await _fallback.ReadAsync(imageBytes, contentType, ct);

        throw new InvalidOperationException(
            "No label-reading engine is available. Configure Azure OpenAI or install the Tesseract OCR binary.");
    }
}
