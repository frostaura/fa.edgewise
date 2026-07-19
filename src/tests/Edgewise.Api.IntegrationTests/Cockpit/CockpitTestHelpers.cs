using System.Text.Json;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.IntegrationTests.Cockpit;

public static class CockpitTestHelpers
{
    private sealed class StubCurrentUser(Guid? userId) : ICurrentUser
    {
        public Guid? UserId => userId;
    }

    /// <summary>Direct DbContext for seeding, scoped to a user (or unscoped with null).</summary>
    public static EdgewiseDbContext CreateDbContext(this TestAppFactory factory, Guid? userId)
    {
        var options = new DbContextOptionsBuilder<EdgewiseDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .Options;
        return new EdgewiseDbContext(options, new StubCurrentUser(userId));
    }

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
