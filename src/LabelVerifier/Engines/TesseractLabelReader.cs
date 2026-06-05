using System.Diagnostics;
using System.Text.RegularExpressions;
using LabelVerifier.Models;

namespace LabelVerifier.Engines;

/// <summary>
/// Offline fallback engine. Shells out to the Tesseract OCR binary so it needs
/// no outbound network — the path TTB would use behind its firewall. OCR is
/// weaker on stylised fonts and bad photos, so it mainly recovers the warning
/// text and obvious fields; the verifier also scans the raw OCR text for
/// expected values, which keeps this useful even when field parsing is rough.
/// </summary>
public sealed partial class TesseractLabelReader : ILabelReader
{
    private readonly ILogger<TesseractLabelReader> _log;
    private readonly string _binary;

    public TesseractLabelReader(ILogger<TesseractLabelReader> log, IConfiguration config)
    {
        _log = log;
        _binary = config["Tesseract:Binary"] ?? "tesseract";
    }

    public string Name => "Tesseract OCR (offline)";

    public bool IsAvailable
    {
        get
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(_binary, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                });
                if (p is null) return false;
                p.WaitForExit(3000);
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task<LabelReading> ReadAsync(byte[] imageBytes, string contentType, CancellationToken ct = default)
    {
        var ext = contentType.Contains("png") ? ".png" : contentType.Contains("webp") ? ".webp" : ".jpg";
        var tmp = Path.Combine(Path.GetTempPath(), $"label_{Guid.NewGuid():N}{ext}");
        await File.WriteAllBytesAsync(tmp, imageBytes, ct);
        try
        {
            var rawText = await RunOcrAsync(tmp, ct);
            var reading = new LabelReading
            {
                RawText = rawText,
                EngineUsed = Name,
                Notes = "Read with offline OCR; field detection is best-effort and typography (bold) cannot be assessed."
            };

            ExtractWarning(rawText, reading);
            reading.AlcoholContent = AbvRegex().Match(rawText) is { Success: true } m ? m.Value.Trim() : null;
            reading.NetContents = NetContentsRegex().Match(rawText) is { Success: true } n ? n.Value.Trim() : null;

            return reading;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    private async Task<string> RunOcrAsync(string imagePath, CancellationToken ct)
    {
        // --psm 3: fully automatic page segmentation. stdout: write text to stdout.
        var psi = new ProcessStartInfo(_binary, $"\"{imagePath}\" stdout --psm 3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start tesseract.");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var text = await outTask;
        if (p.ExitCode != 0)
            _log.LogWarning("tesseract exited {Code}: {Err}", p.ExitCode, await errTask);
        return text;
    }

    private static void ExtractWarning(string rawText, LabelReading reading)
    {
        // Locate the warning heading case-insensitively, capture the rest of the paragraph.
        var m = Regex.Match(rawText, @"GOVERNMENT\s+WARNING.*", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return;

        reading.GovernmentWarningText = Regex.Replace(m.Value, @"\s+", " ").Trim();
        // Heading caps can be judged from the OCR text; bold cannot.
        reading.WarningHeadingAllCaps = Regex.IsMatch(rawText, @"GOVERNMENT\s+WARNING");
        reading.WarningHeadingBold = null;
    }

    [GeneratedRegex(@"\d{1,2}(\.\d+)?\s*%\s*(alc|abv)?", RegexOptions.IgnoreCase)]
    private static partial Regex AbvRegex();

    [GeneratedRegex(@"\d+(\.\d+)?\s*(ml|millilit(er|re)s?|l|lit(er|re)s?|fl\.?\s*oz)", RegexOptions.IgnoreCase)]
    private static partial Regex NetContentsRegex();
}
