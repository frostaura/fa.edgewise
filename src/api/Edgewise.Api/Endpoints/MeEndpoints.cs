using System.Text.Json;
using Edgewise.Contracts.Me;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

public static class MeEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me").RequireAuthorization();

        group.MapGet("/", GetMe);
        group.MapPatch("/", PatchMe);
        group.MapGet("/tokens", ListTokens);
        group.MapPost("/tokens", CreateToken);
        group.MapDelete("/tokens/{id:guid}", DeleteToken);
    }

    private static async Task<IResult> GetMe(EdgewiseDbContext db, CancellationToken ct)
    {
        var user = await RequireCurrentUserAsync(db, ct);
        return Results.Ok(AuthEndpoints.ToDto(user));
    }

    private static async Task<IResult> PatchMe(UpdateMeRequest request, EdgewiseDbContext db, CancellationToken ct)
    {
        var user = await RequireCurrentUserAsync(db, ct);

        if (request.BaseCurrency is not null)
        {
            var currency = request.BaseCurrency.Trim().ToUpperInvariant();
            if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
            {
                throw ApiException.BadRequest("invalid_base_currency", "baseCurrency must be a 3-letter ISO code.");
            }

            user.BaseCurrency = currency;
        }

        if (request.Timezone is not null)
        {
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(request.Timezone.Trim(), out _))
            {
                throw ApiException.BadRequest("invalid_timezone", "timezone must be a valid IANA timezone id.");
            }

            user.Timezone = request.Timezone.Trim();
        }

        if (request.LlmOptOut is not null)
        {
            user.LlmOptOut = request.LlmOptOut.Value;
        }

        if (request.SettingsJson is not null)
        {
            try
            {
                using var _ = JsonDocument.Parse(request.SettingsJson);
            }
            catch (JsonException)
            {
                throw ApiException.BadRequest("invalid_settings_json", "settingsJson must be valid JSON.");
            }

            user.SettingsJson = request.SettingsJson;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(AuthEndpoints.ToDto(user));
    }

    private static async Task<IResult> ListTokens(ICurrentUser currentUser, AuthService auth, CancellationToken ct)
    {
        var tokens = await auth.ListApiTokensAsync(RequireUserId(currentUser), ct);
        return Results.Ok(tokens.Select(t => new ApiTokenDto(t.Id, t.Name, t.CreatedAt, t.LastUsedAt)).ToList());
    }

    private static async Task<IResult> CreateToken(
        CreateApiTokenRequest request, ICurrentUser currentUser, AuthService auth, CancellationToken ct)
    {
        var (token, plaintext) = await auth.CreateApiTokenAsync(RequireUserId(currentUser), request.Name, ct);
        return Results.Created(
            $"/api/me/tokens/{token.Id}",
            new CreateApiTokenResponse(token.Id, token.Name, plaintext, token.CreatedAt));
    }

    private static async Task<IResult> DeleteToken(
        Guid id, ICurrentUser currentUser, AuthService auth, CancellationToken ct)
    {
        await auth.DeleteApiTokenAsync(RequireUserId(currentUser), id, ct);
        return Results.NoContent();
    }

    private static Guid RequireUserId(ICurrentUser currentUser) =>
        currentUser.UserId
        ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

    private static async Task<User> RequireCurrentUserAsync(EdgewiseDbContext db, CancellationToken ct) =>
        await db.Users.SingleOrDefaultAsync(ct) // global filter narrows to the current user
        ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");
}
