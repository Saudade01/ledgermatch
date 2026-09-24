using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace LedgerMatch.Api.Tests;

public sealed class PostgresApiFixture : IAsyncLifetime
{
    private readonly string _databaseName = "ledgermatch_test_" + Guid.NewGuid().ToString("N");
    private string? _adminConnection;
    private bool _created;
    public TestApiFactory Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        string? configured = Environment.GetEnvironmentVariable("RECON_TEST_DB");
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("Integration tests require RECON_TEST_DB with PostgreSQL CREATEDB permission. No caller database is modified or deleted.");

        NpgsqlConnectionStringBuilder admin = new(configured) { Pooling = false };
        _adminConnection = admin.ConnectionString;
        await using NpgsqlConnection connection = new(_adminConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand create = new($"CREATE DATABASE \"{_databaseName}\"", connection);
        await create.ExecuteNonQueryAsync();
        _created = true;
        try
        {
            NpgsqlConnectionStringBuilder isolated = new(configured) { Database = _databaseName, Pooling = false };
            Factory = new TestApiFactory(isolated.ConnectionString);
            Client = Factory.CreateClient();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Factory is not null) await Factory.DisposeAsync();
        if (!_created) return;
        await using NpgsqlConnection connection = new(_adminConnection);
        await connection.OpenAsync();
        // This identifier is generated internally, never taken from the caller's connection string.
        await using NpgsqlCommand drop = new($"DROP DATABASE \"{_databaseName}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
        _created = false;
    }
}

public sealed class TestApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Database"] = connectionString }));
    }
}
