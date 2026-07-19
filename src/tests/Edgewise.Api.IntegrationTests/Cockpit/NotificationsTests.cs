using System.Net;
using System.Net.Http.Json;
using Edgewise.Domain.Entities;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Cockpit;

[Collection(ApiCollection.Name)]
public sealed class NotificationsTests(TestAppFactory factory)
{
    [Fact]
    public async Task Notifications_read_flow_and_unread_filter()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);
        var userId = auth.User!.Id;

        await using (var db = factory.CreateDbContext(userId))
        {
            for (var i = 0; i < 3; i++)
            {
                db.Notifications.Add(new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Title = $"Test {i}",
                    Body = "body",
                    DeepLink = "/radar",
                    Channels = "inapp",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-i),
                });
            }

            await db.SaveChangesAsync();
        }

        var unread = await (await client.GetAsync("/api/notifications?unread=true")).ReadJsonAsync();
        unread.GetArrayLength().ShouldBe(3);
        var firstId = unread[0].GetProperty("id").GetGuid();

        // Mark one read
        var marked = await client.PostAsync($"/api/notifications/{firstId}/read", null);
        marked.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await marked.ReadJsonAsync()).TryGetProperty("readAt", out _).ShouldBeTrue();

        (await (await client.GetAsync("/api/notifications?unread=true")).ReadJsonAsync())
            .GetArrayLength().ShouldBe(2);

        // All notifications still listed without the filter
        (await (await client.GetAsync("/api/notifications")).ReadJsonAsync())
            .GetArrayLength().ShouldBe(3);

        // Read-all clears the rest
        var readAll = await client.PostAsync("/api/notifications/read-all", null);
        readAll.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await readAll.ReadJsonAsync()).GetProperty("marked").GetInt32().ShouldBe(2);
        (await (await client.GetAsync("/api/notifications?unread=true")).ReadJsonAsync())
            .GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Notifications_are_user_scoped()
    {
        var clientA = factory.CreateClient();
        var authA = await clientA.RegisterAsync(ApiClientHelpers.UniqueEmail());
        clientA.UseBearer(authA.AccessToken!);

        var clientB = factory.CreateClient();
        var authB = await clientB.RegisterAsync(ApiClientHelpers.UniqueEmail());
        clientB.UseBearer(authB.AccessToken!);

        Guid notificationId;
        await using (var db = factory.CreateDbContext(authA.User!.Id))
        {
            var notification = new Notification
            {
                Id = Guid.NewGuid(),
                UserId = authA.User!.Id,
                Title = "Private",
                Body = "for A only",
                CreatedAt = DateTime.UtcNow,
            };
            db.Notifications.Add(notification);
            await db.SaveChangesAsync();
            notificationId = notification.Id;
        }

        var forB = await (await clientB.GetAsync("/api/notifications")).ReadJsonAsync();
        forB.EnumerateArray()
            .Any(n => n.GetProperty("id").GetGuid() == notificationId)
            .ShouldBeFalse();
        (await clientB.PostAsync($"/api/notifications/{notificationId}/read", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Vapid_endpoint_returns_404_when_unconfigured()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);

        (await client.GetAsync("/api/notifications/push/vapid"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Push_subscribe_upserts_and_unsubscribe_removes()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);
        var userId = auth.User!.Id;

        var subscription = new
        {
            endpoint = $"https://push.example/{userId}",
            keys = new { p256dh = "key", auth = "secret" },
        };

        (await client.PostAsJsonAsync("/api/notifications/push/subscribe", new { subscription }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        // Same endpoint again: upsert, not duplicate.
        (await client.PostAsJsonAsync("/api/notifications/push/subscribe", new { subscription }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await using (var db = factory.CreateDbContext(userId))
        {
            db.PushSubscriptions.Count().ShouldBe(1);
        }

        // Missing endpoint rejected
        (await client.PostAsJsonAsync("/api/notifications/push/subscribe", new { subscription = new { } }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var removed = await client.DeleteAsync("/api/notifications/push/subscribe");
        removed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await removed.ReadJsonAsync()).GetProperty("removed").GetInt32().ShouldBe(1);
    }
}
