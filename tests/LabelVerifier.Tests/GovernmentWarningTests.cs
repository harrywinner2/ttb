using LabelVerifier.Models;
using LabelVerifier.Services;

namespace LabelVerifier.Tests;

public class GovernmentWarningTests
{
    private static LabelReading Reading(string? warning, bool? caps = true, bool? bold = true, string? raw = null)
        => new()
        {
            GovernmentWarningText = warning,
            WarningHeadingAllCaps = caps,
            WarningHeadingBold = bold,
            RawText = raw ?? warning,
            EngineUsed = "test"
        };

    [Fact]
    public void Correct_warning_caps_and_bold_passes()
    {
        var r = GovernmentWarning.Check(Reading(GovernmentWarning.CanonicalText, caps: true, bold: true));
        Assert.Equal(CheckStatus.Pass, r.Status);
        Assert.True(r.Present);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Title_case_heading_fails_on_caps()
    {
        // Jenny's real example: "Government Warning" in title case must be rejected.
        var titled = GovernmentWarning.CanonicalText.Replace("GOVERNMENT WARNING", "Government Warning");
        var r = GovernmentWarning.Check(Reading(titled, caps: false, bold: true, raw: titled));
        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Contains(r.Issues, i => i.Contains("capital", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Literal_titlecase_text_overrides_models_wrong_caps_flag()
    {
        // gpt-4o sometimes transcribes "Government Warning" faithfully but reports
        // warningHeadingAllCaps=true anyway. The literal text must win → Fail.
        var titled = GovernmentWarning.CanonicalText.Replace("GOVERNMENT WARNING", "Government Warning");
        var r = GovernmentWarning.Check(Reading(titled, caps: true, bold: true, raw: titled));
        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Contains(r.Issues, i => i.Contains("capital", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_warning_fails_as_not_present()
    {
        var r = GovernmentWarning.Check(Reading(null, caps: null, bold: null, raw: "BRAND X · 40% Alc./Vol. · 750 mL"));
        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.False(r.Present);
    }

    [Fact]
    public void Creative_wording_fails()
    {
        var bogus = "GOVERNMENT WARNING: Drinking may be bad for you. Please drink responsibly.";
        var r = GovernmentWarning.Check(Reading(bogus, caps: true, bold: true));
        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Contains(r.Issues, i => i.Contains("word-for-word", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Non_bold_heading_is_flagged_for_review_not_failed()
    {
        // Bold is advisory (unreliably detected); a reported non-bold heading asks for human
        // eyes rather than hard-failing an otherwise-correct warning.
        var r = GovernmentWarning.Check(Reading(GovernmentWarning.CanonicalText, caps: true, bold: false));
        Assert.Equal(CheckStatus.Review, r.Status);
        Assert.Contains(r.Issues, i => i.Contains("bold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unknown_bold_passes_with_an_advisory_note()
    {
        // OCR / uncertain bold should not block a correctly-worded, all-caps warning.
        var r = GovernmentWarning.Check(Reading(GovernmentWarning.CanonicalText, caps: true, bold: null));
        Assert.Equal(CheckStatus.Pass, r.Status);
        Assert.Contains(r.Issues, i => i.Contains("bold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Warning_recovered_from_raw_text_when_field_missing()
    {
        var raw = "OLD TOM\n" + GovernmentWarning.CanonicalText;
        var r = GovernmentWarning.Check(Reading(null, caps: null, bold: true, raw: raw));
        Assert.True(r.Present);
        Assert.Equal(CheckStatus.Pass, r.Status);
    }
}
