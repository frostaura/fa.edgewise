using System.Text.Json;
using System.Text.Json.Serialization;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.IntegrationTests.Journal;

/// <summary>Shared plumbing for the journal vertical's integration tests.</summary>
public static class JournalTestHelpers
{
    /// <summary>Mirrors the API's JSON contract (camelCase, enums as camelCase strings).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed class StubCurrentUser(Guid? userId) : ICurrentUser
    {
        public Guid? UserId => userId;
    }

    /// <summary>Direct DbContext for seeding test prerequisites (snapshots, accounts).</summary>
    public static EdgewiseDbContext CreateDbContext(this TestAppFactory factory, Guid? userId)
    {
        var options = new DbContextOptionsBuilder<EdgewiseDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .Options;
        return new EdgewiseDbContext(options, new StubCurrentUser(userId));
    }

    /// <summary>Seeds a bucket equity snapshot so sizing math has something to bite on.</summary>
    public static async Task SeedSnapshotAsync(
        this TestAppFactory factory, Guid userId, Guid bucketId, long equityMinor)
    {
        await using var db = factory.CreateDbContext(userId);
        db.Snapshots.Add(new Snapshot
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BucketId = bucketId,
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            EquityMinor = equityMinor,
            Currency = "ZAR",
            NetFlowMinor = 0,
        });
        await db.SaveChangesAsync();
    }

    public static async Task<Guid> SeedAccountAsync(this TestAppFactory factory, Guid userId, Guid bucketId)
    {
        await using var db = factory.CreateDbContext(userId);
        var account = new Account
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BucketId = bucketId,
            Venue = Venue.Csv,
            Name = "Test CSV account",
            Status = AccountStatus.Connected,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }
}
