using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Edgewise.Api.IntegrationTests;

/// <summary>
/// Verifies the per-user global query filters using the seeded RiskProfiles as
/// the probe: each user sees exactly their own rows; an anonymous scope sees none.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class UserIsolationTests(TestAppFactory factory)
{
    private sealed class StubCurrentUser(Guid? userId) : ICurrentUser
    {
        public Guid? UserId => userId;
    }

    private EdgewiseDbContext CreateDbContext(Guid? userId)
    {
        var options = new DbContextOptionsBuilder<EdgewiseDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .Options;
        return new EdgewiseDbContext(options, new StubCurrentUser(userId));
    }

    [Fact]
    public async Task Query_filters_isolate_risk_profiles_between_users()
    {
        var clientA = factory.CreateClient();
        var userA = (await clientA.RegisterAsync(ApiClientHelpers.UniqueEmail())).User!;
        var clientB = factory.CreateClient();
        var userB = (await clientB.RegisterAsync(ApiClientHelpers.UniqueEmail())).User!;

        // User A sees exactly their three seeded presets.
        await using (var dbAsA = CreateDbContext(userA.Id))
        {
            var profiles = await dbAsA.RiskProfiles.ToListAsync();
            profiles.Count.ShouldBe(3);
            profiles.ShouldAllBe(p => p.UserId == userA.Id);
            profiles.Select(p => p.Name).ShouldBe(
                ["Conservative", "Standard", "Aggressive"], ignoreOrder: true);

            // A's buckets are similarly scoped.
            (await dbAsA.Buckets.ToListAsync()).ShouldAllBe(b => b.UserId == userA.Id);
        }

        // User B never sees A's rows, even when asking for them by user id.
        await using (var dbAsB = CreateDbContext(userB.Id))
        {
            (await dbAsB.RiskProfiles.ToListAsync()).ShouldAllBe(p => p.UserId == userB.Id);
            (await dbAsB.RiskProfiles.Where(p => p.UserId == userA.Id).ToListAsync()).ShouldBeEmpty();
            (await dbAsB.Users.Where(u => u.Id == userA.Id).ToListAsync()).ShouldBeEmpty();
        }

        // No authenticated user: user-owned tables appear empty.
        await using (var dbAnonymous = CreateDbContext(null))
        {
            (await dbAnonymous.RiskProfiles.ToListAsync()).ShouldBeEmpty();
            (await dbAnonymous.Buckets.ToListAsync()).ShouldBeEmpty();
            (await dbAnonymous.Users.ToListAsync()).ShouldBeEmpty();

            // Global tables stay visible.
            (await dbAnonymous.Instruments.CountAsync()).ShouldBeGreaterThan(0);
            (await dbAnonymous.PlaybookTemplates.CountAsync(t => t.UserId == null)).ShouldBe(4);
        }
    }

    [Fact]
    public async Task Audit_log_records_seeded_mutations()
    {
        var client = factory.CreateClient();
        var user = (await client.RegisterAsync(ApiClientHelpers.UniqueEmail())).User!;

        await using var db = CreateDbContext(user.Id);
        var audits = await db.AuditLogs.ToListAsync();
        audits.ShouldAllBe(a => a.UserId == user.Id);
        audits.ShouldContain(a => a.EntityType == "RiskProfile" && a.Action == "Added");
        audits.ShouldContain(a => a.EntityType == "Bucket" && a.Action == "Added");
    }
}
