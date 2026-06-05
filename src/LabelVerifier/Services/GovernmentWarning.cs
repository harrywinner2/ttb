using System.Text.RegularExpressions;
using LabelVerifier.Models;

namespace LabelVerifier.Services;

/// <summary>
/// The strict check for the mandatory health warning (27 CFR Part 16).
/// Unlike the other fields, this is intentionally unforgiving: the wording must be
/// word-for-word, the heading must be ALL CAPS, and (when the engine can see it)
/// bold. This is the rule agents most often catch people bending — Jenny's
/// "Government Warning" title-case rejection.
/// </summary>
public static partial class GovernmentWarning
{
    /// <summary>The exact statutory text required on every container ≥ 0.5% ABV.</summary>
    public const string CanonicalText =
        "GOVERNMENT WARNING: (1) According to the Surgeon General, women should not drink " +
        "alcoholic beverages during pregnancy because of the risk of birth defects. " +
        "(2) Consumption of alcoholic beverages impairs your ability to drive a car or " +
        "operate machinery, and may cause health problems.";

    public static WarningCheck Check(LabelReading reading)
    {
        var result = new WarningCheck();

        var found = reading.GovernmentWarningText;
        // The vision engine may have skipped the field but still captured the text in rawText.
        if (string.IsNullOrWhiteSpace(found) && reading.RawText is not null &&
            HeadingRegex().Match(reading.RawText) is { Success: true } hm)
        {
            found = reading.RawText[hm.Index..].Trim();
        }

        result.FoundText = found;

        if (string.IsNullOrWhiteSpace(found))
        {
            result.Present = false;
            result.Status = CheckStatus.Fail;
            result.Detail = "No government warning statement was found on the label. It is mandatory on all alcohol beverages.";
            result.Issues.Add("Government warning statement is missing.");
            return result;
        }

        result.Present = true;
        var failures = new List<string>();   // hard violations  -> Fail
        var soft = new List<string>();        // needs human eyes  -> Review
        var notes = new List<string>();       // advisory only     -> no status change

        // 1) Heading must be ALL CAPS. We trust the *literal transcription* over the engine's
        // boolean judgement: vision models faithfully copy the casing into the text but are
        // unreliable at self-reporting "is this all caps?". So inspect the captured text first
        // (case-sensitively) and only fall back to the engine's flag if the heading isn't in the text.
        bool? allCaps = DetectAllCaps(found) ?? DetectAllCaps(reading.RawText) ?? reading.WarningHeadingAllCaps;
        if (allCaps == false)
            failures.Add("The heading “GOVERNMENT WARNING” must be in all capital letters.");

        // 2) Heading should be bold. Bold is genuinely hard to judge from an image: the vision
        // model self-reports it unreliably (both false positives and negatives) and OCR can't
        // see it at all. So bold is treated as ADVISORY — surfaced for a human, never the sole
        // reason to fail or even hold a label that is otherwise correct. (See README trade-offs.)
        if (reading.WarningHeadingBold == false)
            soft.Add("The heading may not be in bold type — please confirm by eye.");
        else if (reading.WarningHeadingBold is null)
            notes.Add("Bold type could not be confirmed automatically — a quick visual check is advised.");

        // 3) Wording must be word-for-word.
        var sim = WordingSimilarity(found, CanonicalText);
        if (sim >= 0.995)
        {
            // word-for-word match
        }
        else if (sim >= 0.95)
        {
            soft.Add("Wording is very close to the required statement but not identical — verify transcription by eye.");
        }
        else
        {
            failures.Add("The warning wording does not match the required statutory text word-for-word.");
        }

        // Status is driven by enforced checks (caps + wording); advisory notes never downgrade it.
        result.Issues.AddRange(failures);
        result.Issues.AddRange(soft);
        result.Issues.AddRange(notes);

        if (failures.Count > 0)
        {
            result.Status = CheckStatus.Fail;
            result.Detail = "Health warning does not meet TTB requirements.";
        }
        else if (soft.Count > 0)
        {
            result.Status = CheckStatus.Review;
            result.Detail = "Health warning looks correct, but one aspect needs a human glance.";
        }
        else
        {
            result.Status = CheckStatus.Pass;
            result.Detail = notes.Count > 0
                ? "Health warning is present and correctly worded."
                : "Health warning is present, correctly worded, and properly formatted.";
        }

        return result;
    }

    /// <summary>Case-INSENSITIVE comparison of just the wording (punctuation/whitespace normalised).</summary>
    private static double WordingSimilarity(string found, string canonical)
        => TextMatching.Similarity(StripForWording(found), StripForWording(canonical));

    private static string StripForWording(string s)
    {
        s = s.Replace('’', '\'').Replace('‘', '\'');
        // Compare just the statutory body — the heading is judged separately (caps/bold),
        // and the vision model sometimes omits it from the captured paragraph.
        s = HeadingRegex().Replace(s, " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.TrimStart(':', ' ', '-');
    }

    /// <summary>
    /// Judges heading capitalisation from the literal transcription, case-sensitively.
    /// Returns true if "GOVERNMENT WARNING" appears in caps, false if it appears in any
    /// other casing (e.g. "Government Warning"), or null if the heading isn't present.
    /// </summary>
    private static bool? DetectAllCaps(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (CapsHeadingRegex().IsMatch(text)) return true;       // exact ALL-CAPS occurrence
        if (HeadingRegex().IsMatch(text)) return false;          // present, but not all caps
        return null;                                             // heading not found here
    }

    [GeneratedRegex(@"GOVERNMENT\s+WARNING")]
    private static partial Regex CapsHeadingRegex();

    [GeneratedRegex(@"GOVERNMENT\s+WARNING", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingRegex();
}
