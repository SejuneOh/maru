using Dapper;
using Npgsql;

namespace WorkBoard.Api.Tests.Fakes;

// A throwaway database created next to the real one, so a test can own every row of a global table.
internal sealed class ScratchDb : IAsyncDisposable
{
    private static readonly string AdminConnection =
        Environment.GetEnvironmentVariable("ConnectionStrings__Migrate")
        ?? "Host=localhost;Port=5434;Database=workboard;Username=postgres;Password=postgres";

    private readonly string _name = "wb_mig_" + Guid.NewGuid().ToString("N")[..10];
    public string ConnectionString { get; private set; } = "";

    public static async Task<ScratchDb> CreateAsync()
    {
        var db = new ScratchDb();
        await using var admin = new NpgsqlConnection(AdminConnection);
        await admin.ExecuteAsync($"create database {db._name}");
        db.ConnectionString = new NpgsqlConnectionStringBuilder(AdminConnection) { Database = db._name, Pooling = false }.ConnectionString;
        return db;
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        return await conn.ExecuteScalarAsync<T>(sql) ?? default!;
    }

    public async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.ExecuteAsync(sql);
    }

    public async ValueTask DisposeAsync()
    {
        await using var admin = new NpgsqlConnection(AdminConnection);
        await admin.ExecuteAsync($"drop database if exists {_name} with (force)");
    }
}
