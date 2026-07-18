using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Edgewise.Api.Services.Journal;
using Edgewise.Api.IntegrationTests.Journal;
using Edgewise.Contracts.Me;
using Edgewise.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Export;

/// <summary>
/// POPIA data-subject endpoints: the export ZIP's shape and validity, and the
/// hard-delete flow (password confirmation, PAT rejection, row erasure with a
/// tombstone audit row, token revocation).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ExportEndpointsTests(TestAppFactory factory)
{
    private static readonly JsonSerializerOptions Json = JournalTestHelpers.Json;

    private static readonly string[] ExpectedZipEntries =
    [
        "trades.json", "plans.json", "fills.json", "holdings.json", "buckets.json", "cashflows.json",
        "dividends.json", "snapshots.json", "watchlists.json", "alerts.json", "zones.json",
        "strategies.json", "backtests.json", "insights.json", "brier.json", "decisions.json",
        "overrides.json", "audit-log.json", "profile.json",
        "trades.csv", "fills.csv", "holdings.csv",
    ];

    /// <summary>Registers a user and journals one confessed trade so the export has data.</summary>
    private async Task<(HttpClient Client, Guid UserId, string Email)> SetUpUserWithTradeAsync()
    {
        var client = factory.CreateClient();
        var email = ApiClientHelpers.UniqueEmail();
        var auth = await client.RegisterAsync(email);
        client.UseBearer(auth.AccessToken!);
        var userId = auth.User!.Id;

        var lookups = await client.GetFromJsonAsync<PlanLookupsDto>("/api/plans/lookups", Json);
        var instrument = lookups!.Instruments.First(i => i.Symbol == "BTC-USD");

        var fillResponse = await client.PostAsJsonAsync("/api/fills", new CreateFillRequest(
            null, instrument.Id, FillSide.Buy, 1m, 100m, 0, null, DateTime.UtcNow, null), Json);
        fillResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var fill = (await fillResponse.Content.ReadFromJsonAsync<FillDto>(Json))!;
        (await client.PostAsJsonAsync($"/api/fills/{fill.Id}/confess", new { }, Json))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        return (client, userId, email);
    }

    private static HttpRequestMessage DeleteMeRequest(string password) =>
        new(HttpMethod.Delete, "/api/me") { Content = JsonContent.Create(new { password }) };

    private async Task<string> CreatePatAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/me/tokens", new CreateApiTokenRequest("popia-test"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreateApiTokenResponse>())!.Token;
    }

    // -------------------------------------------------------------- export

    [Fact]
    public async Task Export_returns_a_zip_with_every_pillar_and_valid_json()
    {
        var (client, userId, email) = await SetUpUserWithTradeAsync();

        var response = await client.GetAsync("/api/me/export");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");

        using var buffer = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        foreach (var expected in ExpectedZipEntries)
        {
            names.ShouldContain(expected);
        }

        // trades.json: valid JSON, exactly the one confessed trade.
        using var trades = JsonDocument.Parse(ReadEntry(zip, "trades.json"));
        trades.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        trades.RootElement.GetArrayLength().ShouldBe(1);
        trades.RootElement[0].GetProperty("userId").GetGuid().ShouldBe(userId);

        // Every JSON entry must parse.
        foreach (var name in names.Where(n => n.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var document = JsonDocument.Parse(ReadEntry(zip, name));
            document.RootElement.ValueKind.ShouldBeOneOf(JsonValueKind.Array, JsonValueKind.Object);
        }

        // profile.json carries the account but never secret material.
        using var profile = JsonDocument.Parse(ReadEntry(zip, "profile.json"));
        profile.RootElement.GetProperty("email").GetString().ShouldBe(email);
        profile.RootElement.TryGetProperty("passwordHash", out _).ShouldBeFalse();
        profile.RootElement.TryGetProperty("totpSecretEnc", out _).ShouldBeFalse();
        profile.RootElement.TryGetProperty("recoveryCodesJson", out _).ShouldBeFalse();

        // CSVs: header plus one row for the trade/fill.
        var tradesCsv = ReadEntry(zip, "trades.csv").Trim().Split('\n');
        tradesCsv.Length.ShouldBe(2);
        tradesCsv[0].ShouldStartWith("Id,Symbol,Direction");
        tradesCsv[1].ShouldContain("BTC-USD");
        var fillsCsv = ReadEntry(zip, "fills.csv").Trim().Split('\n');
        fillsCsv.Length.ShouldBe(2);
    }

    [Fact]
    public async Task Export_rejects_personal_access_tokens()
    {
        var (client, _, _) = await SetUpUserWithTradeAsync();
        var pat = await CreatePatAsync(client);

        var patClient = factory.CreateClient();
        patClient.UseBearer(pat);
        var response = await patClient.GetAsync("/api/me/export");
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    // -------------------------------------------------------------- delete

    [Fact]
    public async Task Delete_me_refuses_wrong_password_and_pats()
    {
        var (client, userId, email) = await SetUpUserWithTradeAsync();
        var pat = await CreatePatAsync(client);

        // Wrong password: refused, nothing deleted.
        var wrong = await client.SendAsync(DeleteMeRequest("definitely-not-it"));
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // PATs cannot delete the account at all.
        var patClient = factory.CreateClient();
        patClient.UseBearer(pat);
        var viaPat = await patClient.SendAsync(DeleteMeRequest(ApiClientHelpers.Password));
        viaPat.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The account is intact: login still works and the rows are still there.
        var fresh = factory.CreateClient();
        (await fresh.LoginAsync(email)).AccessToken.ShouldNotBeNull();
        await using var db = factory.CreateDbContext(userId);
        (await db.Users.CountAsync()).ShouldBe(1);
        (await db.Trades.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Delete_me_erases_all_rows_revokes_tokens_and_leaves_a_hashed_tombstone()
    {
        var (client, userId, email) = await SetUpUserWithTradeAsync();
        var pat = await CreatePatAsync(client);

        var response = await client.SendAsync(DeleteMeRequest(ApiClientHelpers.Password));
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Credentials are gone: a second login attempt fails.
        var fresh = factory.CreateClient();
        var login = await fresh.PostAsJsonAsync("/api/auth/login",
            new Edgewise.Contracts.Auth.LoginRequest(email, ApiClientHelpers.Password));
        login.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The PAT is revoked (hard-deleted).
        var patClient = factory.CreateClient();
        patClient.UseBearer(pat);
        (await patClient.GetAsync("/api/me/tokens")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Every user-owned row is gone (checked without query filters).
        await using var db = factory.CreateDbContext(userId);
        (await db.Users.IgnoreQueryFilters().CountAsync(u => u.Id == userId)).ShouldBe(0);
        (await db.Trades.IgnoreQueryFilters().CountAsync(t => t.UserId == userId)).ShouldBe(0);
        (await db.Fills.IgnoreQueryFilters().CountAsync(f => f.UserId == userId)).ShouldBe(0);
        (await db.TradePlans.IgnoreQueryFilters().CountAsync(p => p.UserId == userId)).ShouldBe(0);
        (await db.Buckets.IgnoreQueryFilters().CountAsync(b => b.UserId == userId)).ShouldBe(0);
        (await db.Accounts.IgnoreQueryFilters().CountAsync(a => a.UserId == userId)).ShouldBe(0);
        (await db.RiskProfiles.IgnoreQueryFilters().CountAsync(r => r.UserId == userId)).ShouldBe(0);
        (await db.Snapshots.IgnoreQueryFilters().CountAsync(s => s.UserId == userId)).ShouldBe(0);
        (await db.ApiTokens.IgnoreQueryFilters().CountAsync(t => t.UserId == userId)).ShouldBe(0);
        (await db.RefreshTokens.IgnoreQueryFilters().CountAsync(t => t.UserId == userId)).ShouldBe(0);

        // Exactly one tombstone remains, keyed by hash — no plaintext identifiers.
        var tombstones = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.UserId == userId)
            .ToListAsync();
        tombstones.Count.ShouldBe(1);
        var tombstone = tombstones[0];
        tombstone.Action.ShouldBe("AccountDeleted");
        tombstone.EntityType.ShouldBe("User");
        tombstone.EntityId.ShouldNotContain("@");
        tombstone.EntityId.Length.ShouldBe(64); // SHA-256 hex
        tombstone.DiffJson!.ShouldNotContain(email);
    }
}
