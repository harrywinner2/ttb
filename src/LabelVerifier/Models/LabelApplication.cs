namespace LabelVerifier.Models;

/// <summary>
/// The data an agent (or the COLA application) asserts about a label.
/// This is the "source of truth" we verify the artwork against.
/// All fields are optional: an agent only verifies what they have on file.
/// </summary>
public sealed class LabelApplication
{
    public string? BrandName { get; set; }

    /// <summary>e.g. "Kentucky Straight Bourbon Whiskey".</summary>
    public string? ClassType { get; set; }

    /// <summary>As written on the application, e.g. "45% Alc./Vol." or "45".</summary>
    public string? AlcoholContent { get; set; }

    /// <summary>e.g. "750 mL".</summary>
    public string? NetContents { get; set; }

    /// <summary>Name &amp; address of the bottler / producer / importer.</summary>
    public string? BottlerNameAddress { get; set; }

    /// <summary>Required for imports, e.g. "Product of Scotland".</summary>
    public string? CountryOfOrigin { get; set; }

    /// <summary>Used only to label the row in batch results.</summary>
    public string? FileName { get; set; }

    public bool HasAnyExpectedFields =>
        !string.IsNullOrWhiteSpace(BrandName) ||
        !string.IsNullOrWhiteSpace(ClassType) ||
        !string.IsNullOrWhiteSpace(AlcoholContent) ||
        !string.IsNullOrWhiteSpace(NetContents) ||
        !string.IsNullOrWhiteSpace(BottlerNameAddress) ||
        !string.IsNullOrWhiteSpace(CountryOfOrigin);
}
