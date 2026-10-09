using System.Net;
using System.Text;

namespace StevanPlayer.Tests.Providers;

internal sealed class ProviderTestHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> responses = new();
    internal List<Uri> Requests { get; } = [];

    internal HttpClient CreateClient() => new(this) { Timeout = TimeSpan.FromSeconds(10) };

    internal void Respond(Func<HttpRequestMessage, HttpResponseMessage> response) => responses.Enqueue(response);

    internal void Json(string content, HttpStatusCode status = HttpStatusCode.OK) =>
        Respond(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request.RequestUri!);
        Assert.NotEmpty(responses);
        return Task.FromResult(responses.Dequeue()(request));
    }
}
