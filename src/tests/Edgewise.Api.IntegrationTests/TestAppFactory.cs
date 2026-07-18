using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Edgewise.Api.IntegrationTests;

/// <summary>
/// Boots the real API against a per-test-run PostgreSQL database. Uses the
/// server from the EDGEWISE_TEST_DB connection string when set (with a unique
/// database name per run), otherwise starts a Testcontainers PostgreSQL
/// instance. Migrations and seeding run via the app's own startup hosted
/// service; Hangfire is disabled.
/// </summary>
public sealed class TestAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _adminConnectionString;

    public string ConnectionString { get; private set; } = string.Empty;

    async Task IAsyncLifetime.InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("EDGEWISE_TEST_DB");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _adminConnectionString = configured;
            var builder = new NpgsqlConnectionStringBuilder(configured)
            {
                Database = $"edgewise_test_{Guid.NewGuid():N}",
            };
            ConnectionString = builder.ConnectionString;
        }
        else
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase($"edgewise_test_{Guid.NewGuid():N}")
                .Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }

        // Force host creation (and thereby migration + seed) up front.
        _ = Server;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("DisableHangfire", "true");
        builder.UseSetting("EDGEWISE_JWT_SECRET", "integration-test-jwt-secret-0123456789abcdef");
        builder.UseSetting("EDGEWISE_ENCRYPTION_KEY", Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();

        if (_adminConnectionString is not null)
        {
            // Best-effort cleanup of the per-run database on the shared server.
            try
            {
                var dbName = new NpgsqlConnectionStringBuilder(ConnectionString).Database;
                var admin = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = "postgres" };
                await using var connection = new NpgsqlConnection(admin.ConnectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)";
                await command.ExecuteNonQueryAsync();
            }
            catch (NpgsqlException)
            {
                // Leave the database behind rather than failing the run.
            }
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<TestAppFactory>
{
    public const string Name = "api";
}
