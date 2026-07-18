using System.Security.Claims;
using System.Text.Encodings.Web;
using Edgewise.Contracts.Common;
using Edgewise.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Edgewise.Api.Auth;

/// <summary>
/// Authenticates personal access tokens ("Authorization: Bearer ew_...").
/// Intended for machine access (e.g. the future /mcp surface).
/// </summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiToken";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header["Bearer ".Length..].Trim();
        if (!token.StartsWith(AuthService.PatPrefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var authService = Context.RequestServices.GetRequiredService<AuthService>();
        var user = await authService.ValidateApiTokenAsync(token, Context.RequestAborted);
        if (user is null)
        {
            return AuthenticateResult.Fail("Invalid API token.");
        }

        var principal = new ClaimsPrincipal(TokenService.BuildIdentity(user, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Response.WriteAsJsonAsync(
            new ErrorResponse(new ErrorDetail("unauthorized", "A valid API token is required.")));
    }
}
