namespace StevanPlayer.Core.Providers;

public enum ProviderFailure
{
    Configuration,
    Authentication,
    Quota,
    Unavailable,
    InvalidResponse
}

/// <summary>A user-safe failure: neither credential-bearing URLs nor server bodies are included.</summary>
public sealed class ProviderException(string providerId, ProviderFailure failure, string message)
    : Exception(message)
{
    public string ProviderId { get; } = providerId;
    public ProviderFailure Failure { get; } = failure;
}
