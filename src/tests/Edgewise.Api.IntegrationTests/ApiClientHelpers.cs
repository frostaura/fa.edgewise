using System.Net.Http.Headers;
using System.Net.Http.Json;
using Edgewise.Contracts.Auth;
using Shouldly;

namespace Edgewise.Api.IntegrationTests;

public static class ApiClientHelpers
{
    public static string UniqueEmail() => $"user-{Guid.NewGuid():N}@test.local";

    public const string Password = "correct-horse-battery";

    public static async Task<AuthResponse> RegisterAsync(this HttpClient client, string email, string password = Password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, password));
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        body.ShouldNotBeNull();
        return body;
    }

    public static async Task<AuthResponse> LoginAsync(this HttpClient client, string email, string password = Password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        body.ShouldNotBeNull();
        return body;
    }

    public static void UseBearer(this HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}
