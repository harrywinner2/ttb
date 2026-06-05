using LabelVerifier.Services;

namespace LabelVerifier.Tests;

public class TextMatchingTests
{
    [Theory]
    [InlineData("STONE'S THROW", "Stone's Throw")]      // Dave's case-mismatch example
    [InlineData("OLD TOM DISTILLERY", "Old Tom Distillery")]
    [InlineData("Café Noir", "Cafe Noir")]               // diacritics
    [InlineData("Red Oak  Cellars", "Red Oak Cellars")]  // whitespace
    public void Trivial_formatting_differences_normalize_equal(string a, string b)
    {
        Assert.Equal(TextMatching.Normalize(a), TextMatching.Normalize(b));
        Assert.Equal(1.0, TextMatching.Similarity(a, b), 3);
    }

    [Fact]
    public void Genuinely_different_brands_are_not_similar()
    {
        Assert.True(TextMatching.Similarity("Old Tom Distillery", "Blue Heron Spirits") < 0.6);
    }

    [Theory]
    [InlineData("45% Alc./Vol. (90 Proof)", 45)]
    [InlineData("13.5% ABV", 13.5)]
    [InlineData("40 % Alc/Vol", 40)]
    public void Parses_abv_percentage(string input, double expected)
    {
        Assert.Equal(expected, TextMatching.ParseAbv(input));
    }

    [Fact]
    public void Parses_proof()
    {
        Assert.Equal(90, TextMatching.ParseProof("45% Alc./Vol. (90 Proof)"));
    }

    [Fact]
    public void NormalizedContains_finds_value_in_raw_text()
    {
        var raw = "OLD TOM DISTILLERY\nKentucky Straight Bourbon Whiskey\n45% Alc./Vol.";
        Assert.True(TextMatching.NormalizedContains(raw, "old tom distillery"));
        Assert.False(TextMatching.NormalizedContains(raw, "vodka"));
    }
}
