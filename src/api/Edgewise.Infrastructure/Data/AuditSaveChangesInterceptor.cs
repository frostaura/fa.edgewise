using System.Text.Json;
using Edgewise.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Edgewise.Infrastructure.Data;

/// <summary>
/// Writes an AuditLog row for every insert/update/delete of a user-owned entity
/// in the same SaveChanges unit of work. AuditLog itself, LlmRequestLog and the
/// global cache tables are never audited. Secret-bearing columns are redacted.
/// </summary>
public sealed class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private static readonly HashSet<Type> SkippedTypes =
    [
        typeof(AuditLog),
        typeof(LlmRequestLog),
        typeof(QuoteCache),
        typeof(PriceBar),
        typeof(FxRate),
        typeof(ProviderHealth),
        typeof(SentimentReading),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        AddAuditEntries(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        AddAuditEntries(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void AddAuditEntries(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        List<AuditLog>? logs = null;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is not IUserOwned owned
                || SkippedTypes.Contains(entry.Metadata.ClrType)
                || entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            logs ??= [];
            logs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                UserId = owned.UserId,
                EntityType = entry.Metadata.ClrType.Name,
                EntityId = DescribePrimaryKey(entry),
                Action = entry.State.ToString(),
                DiffJson = JsonSerializer.Serialize(BuildDiff(entry), JsonOptions),
                At = DateTime.UtcNow,
            });
        }

        if (logs is not null)
        {
            context.Set<AuditLog>().AddRange(logs);
        }
    }

    private static string DescribePrimaryKey(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null)
        {
            return string.Empty;
        }

        var values = key.Properties.Select(p => entry.Property(p.Name).CurrentValue?.ToString() ?? "null");
        return string.Join("|", values);
    }

    private static Dictionary<string, object?> BuildDiff(EntityEntry entry)
    {
        var diff = new Dictionary<string, object?>();
        foreach (var property in entry.Properties)
        {
            var name = property.Metadata.Name;
            switch (entry.State)
            {
                case EntityState.Added:
                    diff[name] = new { @new = Redact(name, property.CurrentValue) };
                    break;
                case EntityState.Deleted:
                    diff[name] = new { old = Redact(name, property.OriginalValue) };
                    break;
                case EntityState.Modified when property.IsModified:
                    diff[name] = new
                    {
                        old = Redact(name, property.OriginalValue),
                        @new = Redact(name, property.CurrentValue),
                    };
                    break;
            }
        }

        return diff;
    }

    private static object? Redact(string propertyName, object? value)
    {
        if (value is null)
        {
            return null;
        }

        return propertyName.EndsWith("Hash", StringComparison.Ordinal)
            || propertyName.EndsWith("Enc", StringComparison.Ordinal)
            || propertyName.Contains("Secret", StringComparison.Ordinal)
            || propertyName.Contains("RecoveryCodes", StringComparison.Ordinal)
            ? "[redacted]"
            : value;
    }
}
