namespace StevanPlayer.Core.Providers;

/// <summary>Creates the application transport without automatic redirects of credential-bearing requests.</summary>
public static class ProviderHttpClientFactory
{
    public static HttpClient Create(TimeSpan? timeout = null) =>
        new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30)
        };
}
