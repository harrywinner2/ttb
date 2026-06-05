using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using LabelVerifier.Engines;
using LabelVerifier.Models;

namespace LabelVerifier.Services;

/// <summary>
/// Orchestrates: read the label (with fallback) → compare each field to the
/// application → run the strict warning check → roll up an overall verdict.
/// Field comparisons are deliberately tolerant of formatting; the warning is not.
/// </summary>
public sealed partial class LabelVerificationService
{
    private readonly FallbackLabelReader _reader;
    private readonly ILogger<LabelVerificationService> _log;

    // Tolerances for fuzzy text fields.
    private const double PassThreshold = 0.90;
    private const double ReviewThreshold = 0.72;

    public LabelVerificationService(FallbackLabelReader reader, ILogger<LabelVerificationService> log)
    {
        _reader = reader;
        _log = log;
    }

    public FallbackLabelReader Reader => _reader;

    public async Task<VerificationResult> VerifyAsync(
        byte[] imageBytes, string contentType, LabelApplication app, bool forceFallback = false, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new VerificationResult { FileName = app.FileName };

        LabelReading reading;
        try
        {
            reading = await _reader.ReadAsync(imageBytes, contentType, forceFallback, ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "Failed to read label {File}", app.FileName);
            result.Overall = CheckStatus.Fail;
            result.Error = "The label image could not be read. Try a clearer, well-lit, straight-on photo.";
            result.ProcessingMs = sw.ElapsedMilliseconds;
            return result;
        }

        result.Reading = reading;
        result.EngineUsed = reading.EngineUsed;

        result.Fields.Add(CheckText("Brand name", app.BrandName, reading.BrandName, reading.RawText));
        result.Fields.Add(CheckText("Class / type", app.ClassType, reading.ClassType, reading.RawText, allowPartial: true));
        result.Fields.Add(CheckAbv(app.AlcoholContent, reading.AlcoholContent, reading.RawText));
        result.Fields.Add(CheckNetContents(app.NetContents, reading.NetContents, reading.RawText));
        result.Fields.Add(CheckText("Bottler / producer", app.BottlerNameAddress, reading.BottlerNameAddress, reading.RawText, allowPartial: true));
        result.Fields.Add(CheckText("Country of origin", app.CountryOfOrigin, reading.CountryOfOrigin, reading.RawText, allowPartial: true));

        result.Warning = GovernmentWarning.Check(reading);

        result.Overall = RollUp(result);
        sw.Stop();
        result.ProcessingMs = sw.ElapsedMilliseconds;
        return result;
    }

    private static CheckStatus RollUp(VerificationResult r)
    {
        var statuses = r.Fields.Select(f => f.Status).Append(r.Warning.Status).ToList();
        if (statuses.Contains(CheckStatus.Fail)) return CheckStatus.Fail;
        if (statuses.Contains(CheckStatus.Review)) return CheckStatus.Review;
        return CheckStatus.Pass;
    }

    private static FieldCheck CheckText(string field, string? expected, string? found, string? rawText, bool allowPartial = false)
    {
        var check = new FieldCheck { Field = field, Expected = expected, Found = found };
        if (string.IsNullOrWhiteSpace(expected))
        {
            check.Status = CheckStatus.NotChecked;
            check.Detail = "Nothing on the application to compare.";
            return check;
        }

        // If the engine didn't isolate the field, try to find the expected value in the raw label text.
        if (string.IsNullOrWhiteSpace(found))
        {
            if (TextMatching.NormalizedContains(rawText, expected))
            {
                check.Found = expected;
                check.Status = CheckStatus.Pass;
                check.Detail = "Found in the label text.";
                return check;
            }
            check.Status = CheckStatus.Fail;
            check.Detail = "Not found on the label.";
            return check;
        }

        var exactNorm = TextMatching.Normalize(expected) == TextMatching.Normalize(found);
        if (exactNorm)
        {
            check.Status = CheckStatus.Pass;
            check.Detail = expected.Trim() == found.Trim() ? "Exact match." : "Matches (formatting/case differs).";
            return check;
        }

        // Partial containment is acceptable for descriptive fields (class/type, address).
        if (allowPartial &&
            (TextMatching.NormalizedContains(found, expected) || TextMatching.NormalizedContains(expected, found)))
        {
            check.Status = CheckStatus.Pass;
            check.Detail = "Matches (one value contains the other).";
            return check;
        }

        // For strict fields (brand): if the application value is a whole-word subset of the
        // fuller name printed on the label (e.g. application "Van Winkle" vs label "Van Winkle
        // Special Reserve"), don't hard-fail an obvious match — but don't silently pass either
        // ("Crown" vs "Crown Royal" are different products). Flag for a human to confirm.
        // (Found from real-world testing — see README.)
        if (!allowPartial && TextMatching.IsProperTokenSubset(expected, found))
        {
            check.Status = CheckStatus.Review;
            check.Detail = "The application value is part of the fuller name on the label — please confirm it is the same product.";
            return check;
        }

        var sim = TextMatching.Similarity(expected, found);
        if (sim >= PassThreshold)
        {
            check.Status = CheckStatus.Pass;
            check.Detail = $"Matches (minor differences, {sim:P0} similar).";
        }
        else if (sim >= ReviewThreshold)
        {
            check.Status = CheckStatus.Review;
            check.Detail = $"Close but not certain ({sim:P0} similar) — please verify by eye.";
        }
        else
        {
            check.Status = CheckStatus.Fail;
            check.Detail = "Does not match the application.";
        }
        return check;
    }

    private static FieldCheck CheckAbv(string? expected, string? found, string? rawText)
    {
        var check = new FieldCheck { Field = "Alcohol content (ABV)", Expected = expected, Found = found };
        if (string.IsNullOrWhiteSpace(expected))
        {
            check.Status = CheckStatus.NotChecked;
            check.Detail = "Nothing on the application to compare.";
            return check;
        }

        var expPct = TextMatching.ParseAbv(expected) ?? (double.TryParse(expected.Trim().TrimEnd('%'), NumberStyles.Any, CultureInfo.InvariantCulture, out var e) ? e : null);
        var foundPct = TextMatching.ParseAbv(found) ?? TextMatching.ParseAbv(rawText);
        if (foundPct is null && !string.IsNullOrWhiteSpace(found))
            check.Found = found;

        if (expPct is null)
        {
            // Couldn't parse a number from the application; fall back to text comparison.
            return CheckText("Alcohol content (ABV)", expected, found, rawText);
        }

        if (foundPct is null)
        {
            check.Status = CheckStatus.Fail;
            check.Detail = "No alcohol content could be read from the label.";
            return check;
        }

        check.Found = $"{foundPct.Value.ToString("0.##", CultureInfo.InvariantCulture)}%";
        var diff = Math.Abs(expPct.Value - foundPct.Value);

        // Proof sanity check (spirits): proof should be 2× ABV.
        var proof = TextMatching.ParseProof(found) ?? TextMatching.ParseProof(rawText);
        string proofNote = proof is not null && Math.Abs(proof.Value - foundPct.Value * 2) > 0.6
            ? $" Note: stated proof ({proof.Value:0.#}) is not 2× the ABV."
            : "";

        if (diff < 0.05)
        {
            check.Status = CheckStatus.Pass;
            check.Detail = $"Matches ({expPct.Value.ToString("0.##", CultureInfo.InvariantCulture)}%).{proofNote}";
        }
        else if (diff <= 0.3)
        {
            check.Status = CheckStatus.Review;
            check.Detail = $"Off by {diff:0.##}% — within label tolerance but worth a glance.{proofNote}";
        }
        else
        {
            check.Status = CheckStatus.Fail;
            check.Detail = $"Application says {expPct.Value.ToString("0.##", CultureInfo.InvariantCulture)}%, label shows {foundPct.Value.ToString("0.##", CultureInfo.InvariantCulture)}%.{proofNote}";
        }
        return check;
    }

    private static FieldCheck CheckNetContents(string? expected, string? found, string? rawText)
    {
        var check = new FieldCheck { Field = "Net contents", Expected = expected, Found = found };
        if (string.IsNullOrWhiteSpace(expected))
        {
            check.Status = CheckStatus.NotChecked;
            check.Detail = "Nothing on the application to compare.";
            return check;
        }

        var expMl = ToMilliliters(expected);
        var foundMl = ToMilliliters(found) ?? ToMilliliters(rawText);

        if (expMl is not null && foundMl is not null)
        {
            var diff = Math.Abs(expMl.Value - foundMl.Value);
            if (diff < 0.5)
            {
                check.Status = CheckStatus.Pass;
                check.Detail = "Matches.";
            }
            else
            {
                check.Status = CheckStatus.Fail;
                check.Detail = $"Application says {expected.Trim()}, label shows a different volume.";
            }
            return check;
        }

        // Fall back to fuzzy text if we couldn't parse a volume.
        return CheckText("Net contents", expected, found, rawText);
    }

    /// <summary>Converts "750 mL", "1 L", "25.4 fl oz" to millilitres for comparison.</summary>
    private static double? ToMilliliters(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = VolumeRegex().Match(s);
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var n)) return null;
        var unit = m.Groups[2].Value.ToLowerInvariant();
        return unit switch
        {
            "l" or "liter" or "litre" or "liters" or "litres" => n * 1000,
            "cl" => n * 10,
            "floz" or "fl oz" or "floz." or "oz" => n * 29.5735,
            _ => n // ml
        };
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(ml|cl|l|liters?|litres?|fl\.?\s*oz|oz)", RegexOptions.IgnoreCase)]
    private static partial Regex VolumeRegex();
}
