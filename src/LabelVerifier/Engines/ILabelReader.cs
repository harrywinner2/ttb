using LabelVerifier.Models;

namespace LabelVerifier.Engines;

/// <summary>
/// Reads the fields off a label image. Implementations are interchangeable so the
/// verification pipeline doesn't care whether the text came from a cloud vision
/// model or fully-offline OCR — the key design point for TTB's firewalled network.
/// </summary>
public interface ILabelReader
{
    /// <summary>A short, stable identifier shown to agents, e.g. "Azure OpenAI (gpt-4o)".</summary>
    string Name { get; }

    /// <summary>Whether this engine is configured/available right now.</summary>
    bool IsAvailable { get; }

    Task<LabelReading> ReadAsync(byte[] imageBytes, string contentType, CancellationToken ct = default);
}
