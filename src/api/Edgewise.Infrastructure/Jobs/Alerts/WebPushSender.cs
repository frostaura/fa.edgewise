using System.Text.Json;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WebPush;

namespace Edgewise.Infrastructure.Jobs.Alerts;

/// <summary>
/// Sends Web Push notifications to a user's stored subscriptions. Silently a
/// no-op when the VAPID keys (EDGEWISE_VAPID_PUBLIC_KEY / EDGEWISE_VAPID_PRIVATE_KEY /
/// EDGEWISE_VAPID_SUBJECT) are not configured. Dead subscriptions (404/410)
/// are pruned from the DbContext (caller saves).
/// </summary>
public class WebPushSender(IConfiguration config, ILogger<WebPushSender> logger)
{
    public string? PublicKey => Trimmed(config["EDGEWISE_VAPID_PUBLIC_KEY"]);

    private string? PrivateKey => Trimmed(config["EDGEWISE_VAPID_PRIVATE_KEY"]);

    private string Subject => Trimmed(config["EDGEWISE_VAPID_SUBJECT"]) ?? "mailto:alerts@edgewise.local";

    public bool IsConfigured => PublicKey is not null && PrivateKey is not null;

    /// <summary>Returns true when at least one push was delivered.</summary>
    public virtual async Task<bool> SendAsync(
        EdgewiseDbContext db, Guid userId, string title, string body, string? deepLink, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return false;
        }

        var subscriptions = await db.PushSubscriptions.IgnoreQueryFilters()
            .Where(s => s.UserId == userId)
            .ToListAsync(ct);
        if (subscriptions.Count == 0)
        {
            return false;
        }

        var vapid = new VapidDetails(Subject, PublicKey, PrivateKey);
        var payload = JsonSerializer.Serialize(
            new { title, body, deepLink },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        using var client = new WebPushClient();
        var sent = false;
        foreach (var stored in subscriptions)
        {
            var subscription = ParseSubscription(stored.EndpointJson);
            if (subscription is null)
            {
                db.PushSubscriptions.Remove(stored);
                continue;
            }

            try
            {
                await client.SendNotificationAsync(subscription, payload, vapid, ct);
                sent = true;
            }
            catch (WebPushException ex) when (
                ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
            {
                // Dead endpoint — prune it.
                db.PushSubscriptions.Remove(stored);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Web push delivery failed for user {UserId}", userId);
            }
        }

        return sent;
    }

    private static WebPush.PushSubscription? ParseSubscription(string endpointJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(endpointJson);
            var root = doc.RootElement;
            var endpoint = root.TryGetProperty("endpoint", out var e) ? e.GetString() : null;
            string? p256dh = null;
            string? auth = null;
            if (root.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Object)
            {
                p256dh = keys.TryGetProperty("p256dh", out var p) ? p.GetString() : null;
                auth = keys.TryGetProperty("auth", out var a) ? a.GetString() : null;
            }

            return endpoint is null || p256dh is null || auth is null
                ? null
                : new WebPush.PushSubscription(endpoint, p256dh, auth);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
