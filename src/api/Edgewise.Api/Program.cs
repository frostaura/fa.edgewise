using System.Text.Json;
using System.Text.Json.Serialization;
using Edgewise.Api;
using Edgewise.Api.Auth;
using Edgewise.Api.Endpoints;
using Edgewise.Contracts.Common;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.Security;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ------------------------------------------------------------------- JSON
// Contract: camelCase properties, enums as camelCase strings, nulls omitted.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

// -------------------------------------------------- reverse proxy (nginx)
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// --------------------------------------------------------------- database
var connectionString = config.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "Connection string 'Default' is required (ConnectionStrings__Default).");

builder.Services.AddSingleton<AuditSaveChangesInterceptor>();
builder.Services.AddDbContext<EdgewiseDbContext>((sp, options) => options
    .UseNpgsql(connectionString)
    .AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>()));

// --------------------------------------------------------------- services
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddSingleton(EnvelopeCrypto.FromBase64(
    config[EnvelopeCrypto.EnvVarName]
    ?? throw new InvalidOperationException($"{EnvelopeCrypto.EnvVarName} is required (base64, 32 bytes).")));
builder.Services.AddSingleton<TotpService>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<AuthService>();

// ------------------------------------------------------------------- auth
// "Smart" selects the PAT handler for ew_* bearer tokens, JWT otherwise.
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = "Smart";
        options.DefaultChallengeScheme = "Smart";
    })
    .AddPolicyScheme("Smart", "JWT or PAT", options =>
    {
        options.ForwardDefaultSelector = context =>
            context.Request.Headers.Authorization.ToString()
                .StartsWith($"Bearer {AuthService.PatPrefix}", StringComparison.Ordinal)
                ? ApiTokenAuthenticationHandler.SchemeName
                : JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer()
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(
        ApiTokenAuthenticationHandler.SchemeName, null);

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<TokenService>((options, tokenService) =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = tokenService.BuildValidationParameters();
        options.Events = new JwtBearerEvents
        {
            // Reject non-access tokens (e.g. the 5-minute TOTP step-up token).
            OnTokenValidated = context =>
            {
                if (context.Principal?.FindFirst(TokenService.PurposeClaim)?.Value != TokenService.AccessPurpose)
                {
                    context.Fail("Token is not an access token.");
                }

                return Task.CompletedTask;
            },
            OnChallenge = async context =>
            {
                context.HandleResponse();
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(
                        new ErrorResponse(new ErrorDetail("unauthorized", "Authentication required or token invalid.")));
                }
            },
            OnForbidden = async context =>
            {
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(
                        new ErrorResponse(new ErrorDetail("forbidden", "You do not have access to this resource.")));
                }
            },
        };
    });

builder.Services.AddAuthorization();

// --------------------------------------------------------------- hangfire
var hangfireEnabled = !config.GetValue<bool>("DisableHangfire");
if (hangfireEnabled)
{
    builder.Services.AddHangfire(cfg => cfg
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(connectionString)));
    builder.Services.AddHangfireServer();
}

// -------------------------------------------------- startup migrate + seed
if (config["EDGEWISE_SKIP_MIGRATE"] != "1")
{
    builder.Services.AddHostedService<MigrateAndSeedHostedService>();
}

// ---------------------------------------------------------- service modules
// Feature service registrations are discovered, never added here directly.
var moduleAssemblies = new[] { typeof(Program).Assembly, typeof(EdgewiseDbContext).Assembly };
foreach (var moduleType in moduleAssemblies
    .SelectMany(assembly => assembly.GetTypes())
    .Where(type => type is { IsAbstract: false, IsInterface: false }
        && typeof(Edgewise.Infrastructure.IServiceModule).IsAssignableFrom(type)))
{
    var module = (Edgewise.Infrastructure.IServiceModule)Activator.CreateInstance(moduleType)!;
    module.Configure(builder.Services, config);
}

var app = builder.Build();

app.UseForwardedHeaders();
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

// ----------------------------------------------------------------- health
app.MapGet("/health", async (EdgewiseDbContext db, CancellationToken ct) =>
{
    var databaseUp = await db.Database.CanConnectAsync(ct);
    return databaseUp
        ? Results.Ok(new { status = "ok", database = "up" })
        : Results.Json(new { status = "degraded", database = "down" }, statusCode: StatusCodes.Status503ServiceUnavailable);
});

// -------------------------------------------------------------- endpoints
// Convention: each Endpoints/XxxEndpoints.cs exposes
//   public static class XxxEndpoints { public static void Map(IEndpointRouteBuilder app) }
// and is called here. Add new endpoint groups below.
AuthEndpoints.Map(app);
MeEndpoints.Map(app);

// Feature endpoint groups implement IEndpointModule and are discovered here.
foreach (var endpointModuleType in typeof(Program).Assembly.GetTypes()
    .Where(type => type is { IsAbstract: false, IsInterface: false }
        && typeof(IEndpointModule).IsAssignableFrom(type)))
{
    var endpointModule = (IEndpointModule)Activator.CreateInstance(endpointModuleType)!;
    endpointModule.Map(app);
}

if (hangfireEnabled)
{
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = [new HangfireDashboardAdminFilter()],
    });

    // Recurring jobs are registered by discovered IRecurringJobRegistrar
    // implementations (constructor injection supported).
    using var jobScope = app.Services.CreateScope();
    var jobManager = jobScope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    foreach (var registrarType in moduleAssemblies
        .SelectMany(assembly => assembly.GetTypes())
        .Where(type => type is { IsAbstract: false, IsInterface: false }
            && typeof(Edgewise.Infrastructure.IRecurringJobRegistrar).IsAssignableFrom(type)))
    {
        var registrar = (Edgewise.Infrastructure.IRecurringJobRegistrar)
            ActivatorUtilities.CreateInstance(jobScope.ServiceProvider, registrarType);
        registrar.Register(jobManager);
    }
}

app.Run();

/// <summary>Exposes the entry point to WebApplicationFactory-based integration tests.</summary>
public partial class Program
{
}
