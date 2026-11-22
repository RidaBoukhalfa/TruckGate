using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace TruckGate.Data;

/// <summary>
/// Applies the embedded SQL migrations (Migrations/V###__name.sql) in order, once each.
/// A migration that was already applied must never change: its checksum is verified at
/// every start and the server refuses to run if a shipped script was edited.
/// Run with the database owner connection, never with the application role.
/// </summary>
public static class DbMigrator
{
    private const long LockKey = 7340000;

    public sealed record Script(string Version, string Name, string Sql, string Checksum);

    public static IReadOnlyList<Script> LoadScripts()
    {
        var asm = typeof(DbMigrator).Assembly;
        const string prefix = "TruckGate.Data.Migrations.";
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n =>
            {
                var file = n[prefix.Length..];
                var sql = ReadResource(asm, n).Replace("\r\n", "\n", StringComparison.Ordinal);
                var version = file.Split("__", 2)[0];
                var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
                return new Script(version, file, sql, checksum);
            })
            .OrderBy(s => s.Version, StringComparer.Ordinal)
            .ToList();
    }

    /// <returns>The names of the scripts applied by this call.</returns>
    public static async Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource ownerDataSource,
        CancellationToken ct = default)
    {
        var applied = new List<string>();
        await using var conn = await ownerDataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("SELECT pg_advisory_lock(@k)", new { k = LockKey });
        try
        {
            await conn.ExecuteAsync("""
                CREATE SCHEMA IF NOT EXISTS tg_meta;
                CREATE TABLE IF NOT EXISTS tg_meta.migrations (
                    version    text PRIMARY KEY,
                    name       text        NOT NULL,
                    checksum   text        NOT NULL,
                    applied_at timestamptz NOT NULL DEFAULT now()
                );
                """);
            var done = (await conn.QueryAsync<(string Version, string Checksum)>(
                    "SELECT version, checksum FROM tg_meta.migrations"))
                .ToDictionary(r => r.Version, r => r.Checksum, StringComparer.Ordinal);

            foreach (var script in LoadScripts())
            {
                if (done.TryGetValue(script.Version, out var checksum))
                {
                    if (!string.Equals(checksum, script.Checksum, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"Migration {script.Name} was modified after being applied (checksum mismatch). Refusing to start.");
                    continue;
                }

                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var cmd = new NpgsqlCommand(script.Sql, conn, tx))
                {
                    cmd.CommandTimeout = 300;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await conn.ExecuteAsync(
                    "INSERT INTO tg_meta.migrations (version, name, checksum) VALUES (@v, @n, @c)",
                    new { v = script.Version, n = script.Name, c = script.Checksum }, tx);
                await tx.CommitAsync(ct);
                applied.Add(script.Name);
            }
        }
        finally
        {
            await conn.ExecuteAsync("SELECT pg_advisory_unlock(@k)", new { k = LockKey });
        }
        return applied;
    }

    private static string ReadResource(Assembly asm, string name)
    {
        using var stream = asm.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"missing resource {name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
