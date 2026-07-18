using System.Net;
using System.Text;
using Edgewise.Infrastructure.Ingestion.Connectors;

namespace Edgewise.Api.IntegrationTests.Integrations;

/// <summary>
/// Fake HTTP layer for the exchange connectors: routes every outbound request to
/// registered responders (matched on URL substring) and records request URLs so
/// tests can assert pagination/signing behaviour. NO real network is touched —
/// unmatched requests fail loudly with 599.
/// </summary>
public sealed class FakeIntegrationHttpFactory : IIntegrationHttpClientFactory
{
    private readonly List<(string UrlContains, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    /// <summary>Recorded "METHOD url" strings, in call order.</summary>
    public List<string> Requests { get; } = [];

    public void When(string urlContains, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        _routes.Add((urlContains, respond));

    public void WhenJson(string urlContains, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        When(urlContains, _ => JsonResponse(json, status));

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Reads a query parameter from a request URL ("offset", "fromId", …).</summary>
    public static string? Query(HttpRequestMessage request, string name)
    {
        var query = request.RequestUri?.Query.TrimStart('?') ?? string.Empty;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == name)
            {
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return null;
    }

    public HttpClient CreateClient(string name) => new(new RoutingHandler(this));

    private sealed class RoutingHandler(FakeIntegrationHttpFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (owner.Requests)
            {
                owner.Requests.Add($"{request.Method} {url}");
            }

            foreach (var (contains, respond) in owner._routes)
            {
                if (url.Contains(contains, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(respond(request));
                }
            }

            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)599)
            {
                Content = new StringContent($"FakeIntegrationHttpFactory: no route for {url}"),
            });
        }
    }
}
