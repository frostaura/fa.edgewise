using System.Collections.Concurrent;
using System.Net;
using Polly;
using Polly.Retry;

namespace Edgewise.Infrastructure.MarketData;

/// <summary>Raised when an upstream provider returns a non-success response.</summary>
public sealed class ProviderHttpException(string provider, HttpStatusCode statusCode, string url)
    : Exception($"Provider '{provider}' returned {(int)statusCode} for {url}.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>
/// Shared HTTP gateway for all provider adapters. Owns one long-lived
/// <see cref="HttpClient"/> per provider (browser-like User-Agent, gzip enabled),
/// applies a conservative per-provider sliding-window rate limit, and retries
/// 429/5xx/transport failures with exponential backoff + jitter (Polly).
/// The Infrastructure project has no Microsoft.Extensions.Http reference, so
/// typed-client wiring is done here instead of via IHttpClientFactory.
/// </summary>
public sealed class ProviderHttp : IDisposable
{
    /// <summary>Conservative requests-per-window defaults per provider.</summary>
    private static readonly Dictionary<string, (int Max, TimeSpan Window)> RateLimits = new(StringComparer.Ordinal)
    {
        [ProviderNames.Yahoo] = (100, TimeSpan.FromHours(1)),
        [ProviderNames.Stooq] = (100, TimeSpan.FromHours(1)),
        [ProviderNames.Binance] = (600, TimeSpan.FromMinutes(1)),
        [ProviderNames.Coinbase] = (200, TimeSpan.FromMinutes(1)),
        [ProviderNames.CoinGecko] = (25, TimeSpan.FromMinutes(1)),
        [ProviderNames.Frankfurter] = (60, TimeSpan.FromMinutes(1)),
        [ProviderNames.ExchangeRateApi] = (20, TimeSpan.FromMinutes(1)),
        [ProviderNames.AlternativeMe] = (30, TimeSpan.FromMinutes(1)),
        [ProviderNames.Finnhub] = (50, TimeSpan.FromMinutes(1)),
        [ProviderNames.Rss] = (60, TimeSpan.FromMinutes(1)),
    };

    private static readonly (int Max, TimeSpan Window) DefaultRateLimit = (30, TimeSpan.FromMinutes(1));

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static readonly ResiliencePipeline<HttpResponseMessage> Retry =
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(1),
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(ex => ex.InnerException is TimeoutException)
                    .HandleResult(r =>
                        r.StatusCode == HttpStatusCode.TooManyRequests || (int)r.StatusCode >= 500),
            })
            .Build();

    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProviderRateLimiter> _limiters = new(StringComparer.Ordinal);

    /// <summary>GETs <paramref name="url"/> under the provider's rate limit and retry policy, returning the body.</summary>
    public async Task<string> GetStringAsync(
        string provider, string url, CancellationToken ct, IReadOnlyDictionary<string, string>? headers = null)
    {
        var limiter = _limiters.GetOrAdd(provider, static name =>
        {
            var (max, window) = RateLimits.TryGetValue(name, out var limit) ? limit : DefaultRateLimit;
            return new ProviderRateLimiter(max, window);
        });
        var client = _clients.GetOrAdd(provider, static _ => CreateClient());

        using var response = await Retry.ExecuteAsync(
            async token =>
            {
                await limiter.WaitAsync(token);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (headers is not null)
                {
                    foreach (var (key, value) in headers)
                    {
                        request.Headers.TryAddWithoutValidation(key, value);
                    }
                }

                return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            },
            ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new ProviderHttpException(provider, response.StatusCode, url);
        }

        return await response.Content.ReadAsStringAsync(ct);
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AllowAutoRedirect = true,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        return client;
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }
    }
}
