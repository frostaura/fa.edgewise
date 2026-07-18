using Edgewise.Api.Auth;
using Edgewise.Contracts.Auth;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;

namespace Edgewise.Api.Endpoints;

public static class AuthEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/register", Register);
        group.MapPost("/login", Login);
        group.MapPost("/login/totp", LoginTotp);
        group.MapPost("/refresh", Refresh);
        group.MapPost("/logout", Logout);

        var totp = group.MapGroup("/totp").RequireAuthorization();
        totp.MapPost("/setup", TotpSetup);
        totp.MapPost("/enable", TotpEnable);
        totp.MapPost("/disable", TotpDisable);
    }

    private static async Task<IResult> Register(
        RegisterRequest request, AuthService auth, TokenService tokens, CancellationToken ct)
    {
        var user = await auth.RegisterAsync(request.Email, request.Password, ct);
        var response = await BuildAuthResponseAsync(user, auth, tokens, ct);
        return Results.Created("/api/me", response);
    }

    private static async Task<IResult> Login(
        LoginRequest request, AuthService auth, TokenService tokens, CancellationToken ct)
    {
        var user = await auth.ValidateCredentialsAsync(request.Email, request.Password, ct)
            ?? throw ApiException.Unauthorized("invalid_credentials", "Email or password is incorrect.");

        if (user.TotpEnabled)
        {
            return Results.Ok(new AuthResponse(RequiresTotp: true, TotpToken: tokens.CreateTotpToken(user)));
        }

        return Results.Ok(await BuildAuthResponseAsync(user, auth, tokens, ct));
    }

    private static async Task<IResult> LoginTotp(
        TotpLoginRequest request, AuthService auth, TokenService tokens, CancellationToken ct)
    {
        var userId = await tokens.ValidateTotpTokenAsync(request.TotpToken)
            ?? throw ApiException.Unauthorized("invalid_totp_token", "The TOTP session token is invalid or expired.");

        var user = await auth.CompleteTotpLoginAsync(userId, request.Code, ct);
        return Results.Ok(await BuildAuthResponseAsync(user, auth, tokens, ct));
    }

    private static async Task<IResult> Refresh(
        RefreshRequest request, AuthService auth, TokenService tokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            throw ApiException.BadRequest("validation_error", "refreshToken is required.");
        }

        var (user, newRefreshToken) = await auth.RefreshAsync(request.RefreshToken, ct);
        return Results.Ok(new AuthResponse(
            RequiresTotp: false,
            AccessToken: tokens.CreateAccessToken(user),
            RefreshToken: newRefreshToken,
            User: ToDto(user)));
    }

    private static async Task<IResult> Logout(LogoutRequest request, AuthService auth, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            throw ApiException.BadRequest("validation_error", "refreshToken is required.");
        }

        await auth.LogoutAsync(request.RefreshToken, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> TotpSetup(ICurrentUser currentUser, AuthService auth, CancellationToken ct)
    {
        var userId = RequireUserId(currentUser);
        var (secret, uri) = await auth.SetupTotpAsync(userId, ct);
        return Results.Ok(new TotpSetupResponse(secret, uri));
    }

    private static async Task<IResult> TotpEnable(
        TotpCodeRequest request, ICurrentUser currentUser, AuthService auth, CancellationToken ct)
    {
        var userId = RequireUserId(currentUser);
        var recoveryCodes = await auth.EnableTotpAsync(userId, request.Code, ct);
        return Results.Ok(new TotpEnableResponse(recoveryCodes));
    }

    private static async Task<IResult> TotpDisable(
        TotpCodeRequest request, ICurrentUser currentUser, AuthService auth, CancellationToken ct)
    {
        var userId = RequireUserId(currentUser);
        await auth.DisableTotpAsync(userId, request.Code, ct);
        return Results.NoContent();
    }

    private static Guid RequireUserId(ICurrentUser currentUser) =>
        currentUser.UserId
        ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

    private static async Task<AuthResponse> BuildAuthResponseAsync(
        User user, AuthService auth, TokenService tokens, CancellationToken ct) =>
        new(
            RequiresTotp: false,
            AccessToken: tokens.CreateAccessToken(user),
            RefreshToken: await auth.IssueRefreshTokenAsync(user.Id, ct),
            User: ToDto(user));

    internal static UserDto ToDto(User user) => new(
        user.Id,
        user.Email,
        user.BaseCurrency,
        user.Timezone,
        user.TotpEnabled,
        user.IsAdmin,
        user.LlmOptOut,
        user.SettingsJson,
        user.CreatedAt);
}
