using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Edgewise.Api.Services.Integrations;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.Ingestion.Connectors;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Integrations;

/// <summary>
/// Boots the shared test app with the fake connector HTTP layer swapped in.
/// One harness per test; dispose to tear the derived host down.
/// </summary>
public sealed class IntegrationsHarness : IDisposable
{
    public FakeIntegrationHttpFactory Http { get; } = new();

    public WebApplicationFactory<Program> App { get; }

    public IntegrationsHarness(TestAppFactory factory)
    {
        App = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IIntegrationHttpClientFactory>(Http)));
    }

    /// <summary>camelCase + camelCase string enums — the API's wire contract.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Registers a fresh user and returns an authenticated client plus their default Trading bucket.</summary>
    public async Task<(HttpClient Client, Guid UserId, Guid BucketId)> NewUserAsync()
    {
        var client = App.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);
        var userId = auth.User!.Id;

        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgewiseDbContext>();
        var bucketId = await db.Buckets.IgnoreQueryFilters()
            .Where(b => b.UserId == userId)
            .OrderBy(b => b.Name)
            .Select(b => b.Id)
            .FirstAsync();
        return (client, userId, bucketId);
    }

    public async Task<T> WithDbAsync<T>(Func<EdgewiseDbContext, Task<T>> action)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgewiseDbContext>();
        return await action(db);
    }

    public async Task<ConnectAccountResponse> ConnectAsync(HttpClient client, object request)
    {
        var response = await client.PostAsJsonAsync("/api/accounts/connect", request, Json);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ConnectAccountResponse>(Json))!;
    }

    public async Task<SyncAccountResponse> SyncAsync(HttpClient client, Guid accountId)
    {
        var response = await client.PostAsync($"/api/accounts/{accountId}/sync", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SyncAccountResponse>(Json))!;
    }

    public void Dispose() => App.Dispose();
}
