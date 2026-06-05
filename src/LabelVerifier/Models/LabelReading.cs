namespace LabelVerifier.Models;

/// <summary>
/// What a reading engine extracted from the label artwork.
/// Mirrors <see cref="LabelApplication"/> plus warning-specific fields the
/// engine reports about the artwork itself (caps / bold cannot be inferred
/// from the application data — only seen on the image).
/// </summary>
public sealed class LabelReading
{
    public string? BrandName { get; set; }
    public string? ClassType { get; set; }
    public string? AlcoholContent { get; set; }
    public string? NetContents { get; set; }
    public string? BottlerNameAddress { get; set; }
    public string? CountryOfOrigin { get; set; }

    /// <summary>The full health-warning paragraph as read from the label, if any.</summary>
    public string? GovernmentWarningText { get; set; }

    /// <summary>True if the "GOVERNMENT WARNING" heading appears in all capitals on the label.</summary>
    public bool? WarningHeadingAllCaps { get; set; }

    /// <summary>
    /// True if the "GOVERNMENT WARNING" heading appears in bold type.
    /// Null when the engine cannot judge typography (e.g. plain OCR).
    /// </summary>
    public bool? WarningHeadingBold { get; set; }

    /// <summary>Everything the engine could read, for transparency / debugging.</summary>
    public string? RawText { get; set; }

    /// <summary>Free-form notes from the engine (e.g. "image is rotated", "glare on lower third").</summary>
    public string? Notes { get; set; }

    /// <summary>Which engine produced this reading ("Azure OpenAI (gpt-4o)" / "Tesseract OCR").</summary>
    public string EngineUsed { get; set; } = "unknown";
}
