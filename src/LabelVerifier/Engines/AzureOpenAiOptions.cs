namespace LabelVerifier.Engines;

/// <summary>
/// Binds to the "AzureOpenAI" config section. In production these come from
/// App Service settings / Key Vault, never from source control.
/// </summary>
public sealed class AzureOpenAiOptions
{
    public const string SectionName = "AzureOpenAI";

    /// <summary>e.g. https://my-resource.openai.azure.com</summary>
    public string? Endpoint { get; set; }

    /// <summary>The model deployment name, e.g. "gpt-4o".</summary>
    public string? Deployment { get; set; }

    public string? ApiKey { get; set; }

    public string ApiVersion { get; set; } = "2024-08-01-preview";

    /// <summary>Hard cap so a single slow read can't blow the ~5s budget for the whole queue.</summary>
    public int TimeoutSeconds { get; set; } = 12;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) &&
        !string.IsNullOrWhiteSpace(Deployment) &&
        !string.IsNullOrWhiteSpace(ApiKey);
}
