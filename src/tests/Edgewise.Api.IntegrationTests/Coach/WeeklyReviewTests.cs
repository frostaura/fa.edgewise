using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Coach;

[Collection(ApiCollection.Name)]
public class WeeklyReviewTests(TestAppFactory factory)
{
    private static DateOnly MondayOfCurrentWeek()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var offset = ((int)today.DayOfWeek + 6) % 7; // Monday=0 … Sunday=6
        return today.AddDays(-offset);
    }

    [Fact]
    public async Task Get_creates_a_draft_pack_and_review_row()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);
        var week = MondayOfCurrentWeek();

        await using (var scoped = CoachTestData.OpenDb(factory, auth.User!.Id))
        {
            var instrument = await CoachTestData.SeedInstrumentAsync(scoped.Db);
            await CoachTestData.SeedClosedTradeAsync(
                scoped.Db, auth.User!.Id, instrument.Id,
                closedAt: week.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));
        }

        var state = await client.GetFromJsonAsync<JsonNode>($"/api/coach/weekly-reviews/{week:yyyy-MM-dd}");
        state.ShouldNotBeNull();
        state["review"]!["weekStartDate"]!.GetValue<string>().ShouldBe(week.ToString("yyyy-MM-dd"));
        state["review"]!["completedAt"].ShouldBeNull();
        state["pack"]!["type"]!.GetValue<string>().ShouldBe("weeklyPack");
        state["pack"]!["modelTag"]!.GetValue<string>().ShouldBe("deterministic");
        state["pack"]!["content"]!["observations"]!.AsArray().Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Completing_consecutive_weeks_builds_a_streak()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);

        var thisWeek = MondayOfCurrentWeek();
        var lastWeek = thisWeek.AddDays(-7);

        var first = await client.PostAsJsonAsync(
            $"/api/coach/weekly-reviews/{lastWeek:yyyy-MM-dd}",
            new { userEditsMd = "## Last week\nSolid process.", focusCommitment = "No entries without a plan", complete = true });
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonNode>();
        firstBody!["completedAt"].ShouldNotBeNull();
        firstBody["streakCount"]!.GetValue<int>().ShouldBe(1);
        firstBody["userEditsMd"]!.GetValue<string>().ShouldContain("Solid process");
        firstBody["focusCommitment"]!.GetValue<string>().ShouldBe("No entries without a plan");

        var second = await client.PostAsJsonAsync(
            $"/api/coach/weekly-reviews/{thisWeek:yyyy-MM-dd}",
            new { complete = true });
        var secondBody = await second.Content.ReadFromJsonAsync<JsonNode>();
        secondBody!["streakCount"]!.GetValue<int>().ShouldBe(2);

        // Completing again does not double-count.
        var again = await client.PostAsJsonAsync(
            $"/api/coach/weekly-reviews/{thisWeek:yyyy-MM-dd}",
            new { complete = true });
        (await again.Content.ReadFromJsonAsync<JsonNode>())!["streakCount"]!.GetValue<int>().ShouldBe(2);

        // Past reviews appear in the current week's state.
        var state = await client.GetFromJsonAsync<JsonNode>($"/api/coach/weekly-reviews/{thisWeek:yyyy-MM-dd}");
        state!["pastReviews"]!.AsArray().Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Non_monday_week_start_is_rejected()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);

        var tuesday = MondayOfCurrentWeek().AddDays(1);
        var response = await client.GetAsync($"/api/coach/weekly-reviews/{tuesday:yyyy-MM-dd}");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
