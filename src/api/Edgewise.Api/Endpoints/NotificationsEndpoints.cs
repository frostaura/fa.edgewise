using System.Text.Json;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.Jobs.Alerts;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

/// <summary>
/// Notifications + Web Push subscription management.
///   GET    /api/notifications?unread=true&amp;limit=50
///   POST   /api/notifications/{id}/read
///   POST   /api/notifications/read-all
///   GET    /api/notifications/push/vapid       → { publicKey } (404 when VAPID unset)
///   POST   /api/notifications/push/subscribe   { subscription: {endpoint, keys:{p256dh,auth}} }
///   DELETE /api/notifications/push/subscribe   ?endpoint= (all subscriptions when omitted)
/// </summary>
public sealed class NotificationsEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapPost("/{id:guid}/read", MarkReadAsync);
        group.MapPost("/read-all", MarkAllReadAsync);
        group.MapGet("/push/vapid", GetVapid);
        group.MapPost("/push/subscribe", SubscribeAsync);
        group.MapDelete("/push/subscribe", UnsubscribeAsync);
    }

    private static async Task<IResult> ListAsync(
        bool? unread, int? limit, EdgewiseDbContext db, CancellationToken ct)
    {
        var query = db.Notifications.AsQueryable();
        if (unread == true)
        {
            query = query.Where(n => n.ReadAt == null);
        }

        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .Take(Math.Clamp(limit ?? 50, 1, 200))
            .Select(n => new NotificationDto(
                n.Id, n.AlertId, n.Title, n.Body, n.DeepLink, n.Channels, n.CreatedAt, n.ReadAt))
            .ToListAsync(ct);
        return Results.Ok(items);
    }

    private static async Task<IResult> MarkReadAsync(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var notification = await db.Notifications.SingleOrDefaultAsync(n => n.Id == id, ct)
            ?? throw ApiException.NotFound("notification_not_found", "Notification not found.");

        notification.ReadAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new NotificationDto(
            notification.Id, notification.AlertId, notification.Title, notification.Body,
            notification.DeepLink, notification.Channels, notification.CreatedAt, notification.ReadAt));
    }

    private static async Task<IResult> MarkAllReadAsync(EdgewiseDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var unread = await db.Notifications.Where(n => n.ReadAt == null).ToListAsync(ct);
        foreach (var notification in unread)
        {
            notification.ReadAt = now;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new { marked = unread.Count });
    }

    private static IResult GetVapid(WebPushSender push)
    {
        if (push.PublicKey is not { } publicKey || !push.IsConfigured)
        {
            throw ApiException.NotFound("push_not_configured", "Web push is not configured on this server.");
        }

        return Results.Ok(new { publicKey });
    }

    private static async Task<IResult> SubscribeAsync(
        JsonElement body, EdgewiseDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var userId = currentUser.UserId
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

        // Accept either { subscription: {...} } or the raw subscription object.
        var subscription = body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("subscription", out var wrapped)
            && wrapped.ValueKind == JsonValueKind.Object
                ? wrapped
                : body;

        var endpoint = subscription.ValueKind == JsonValueKind.Object
            && subscription.TryGetProperty("endpoint", out var endpointEl)
            && endpointEl.ValueKind == JsonValueKind.String
                ? endpointEl.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw ApiException.BadRequest("invalid_subscription", "A push subscription with an endpoint is required.");
        }

        var json = subscription.GetRawText();

        // Upsert by endpoint: replace any existing subscription with the same endpoint.
        var existing = await db.PushSubscriptions.ToListAsync(ct);
        var match = existing.FirstOrDefault(s => ExtractEndpoint(s.EndpointJson) == endpoint);
        if (match is not null)
        {
            match.EndpointJson = json;
        }
        else
        {
            db.PushSubscriptions.Add(new Edgewise.Domain.Entities.PushSubscription
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                EndpointJson = json,
            });
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new { subscribed = true });
    }

    private static async Task<IResult> UnsubscribeAsync(
        string? endpoint, EdgewiseDbContext db, CancellationToken ct)
    {
        var subscriptions = await db.PushSubscriptions.ToListAsync(ct);
        var toRemove = string.IsNullOrWhiteSpace(endpoint)
            ? subscriptions
            : subscriptions.Where(s => ExtractEndpoint(s.EndpointJson) == endpoint).ToList();
        db.PushSubscriptions.RemoveRange(toRemove);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { removed = toRemove.Count });
    }

    private static string? ExtractEndpoint(string endpointJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(endpointJson);
            return doc.RootElement.TryGetProperty("endpoint", out var el) ? el.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public sealed record NotificationDto(
        Guid Id,
        Guid? AlertId,
        string Title,
        string Body,
        string? DeepLink,
        string? Channels,
        DateTime CreatedAt,
        DateTime? ReadAt);
}
