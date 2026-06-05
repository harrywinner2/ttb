using System.Text.Json.Serialization;

namespace LabelVerifier.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CheckStatus
{
    /// <summary>Label matches the application (allowing for trivial formatting differences).</summary>
    Pass,

    /// <summary>A real discrepancy a human should reject or correct.</summary>
    Fail,

    /// <summary>Close but not certain — surfaced for human judgement (Dave's "nuance" cases).</summary>
    Review,

    /// <summary>Nothing on file to compare against, so the check was skipped.</summary>
    NotChecked
}

/// <summary>One field-level comparison (brand, ABV, net contents, …).</summary>
public sealed class FieldCheck
{
    public string Field { get; set; } = "";
    public string? Expected { get; set; }
    public string? Found { get; set; }
    public CheckStatus Status { get; set; }

    /// <summary>Human-readable explanation, e.g. "Case differs but clearly the same brand".</summary>
    public string? Detail { get; set; }
}

/// <summary>The dedicated, stricter check for the mandatory health warning.</summary>
public sealed class WarningCheck
{
    public CheckStatus Status { get; set; }
    public bool Present { get; set; }
    public string? FoundText { get; set; }

    /// <summary>Specific problems found, e.g. "Heading is not all capitals", "Wording deviates from required text".</summary>
    public List<string> Issues { get; set; } = new();

    public string? Detail { get; set; }
}

/// <summary>The full verification outcome for a single label.</summary>
public sealed class VerificationResult
{
    public string? FileName { get; set; }

    /// <summary>Roll-up: Fail if anything failed, else Review if anything needs eyes, else Pass.</summary>
    public CheckStatus Overall { get; set; }

    public List<FieldCheck> Fields { get; set; } = new();
    public WarningCheck Warning { get; set; } = new();

    public string EngineUsed { get; set; } = "unknown";
    public long ProcessingMs { get; set; }

    /// <summary>Set when the whole reading failed (e.g. unreadable image).</summary>
    public string? Error { get; set; }

    /// <summary>The raw reading, exposed so agents can see exactly what the machine saw.</summary>
    public LabelReading? Reading { get; set; }
}
