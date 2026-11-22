using System.Data;
using Dapper;
using Npgsql;

namespace TruckGate.Data;

/// <summary>
/// Who performs an action. Written into every audit row by the database (tg.actor setting).
/// </summary>
public sealed record Actor(string Value)
{
    public static Actor User(int userId) => new($"user:{userId}");
    public static Actor System(string component) => new($"system:{component}");
    public static Actor Development(string name) => new($"dev:{name}");
}

/// <summary>Opens connections and runs work in READ COMMITTED transactions tagged with the actor.</summary>
public sealed class Db
{
    private readonly NpgsqlDataSource _dataSource;

    static Db()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public Db(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<T> InTransactionAsync<T>(Actor actor,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        // READ COMMITTED is required by the audit chain trigger (see V001 header).
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await conn.ExecuteAsync("SELECT set_config('tg.actor', @a, true)", new { a = actor.Value }, tx);
        var result = await work(conn, tx);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<T> ReadAsync<T>(Func<NpgsqlConnection, Task<T>> work, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await work(conn);
    }
}
