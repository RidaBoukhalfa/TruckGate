using Dapper;
using TruckGate.Data;

namespace TruckGate.Application;

public sealed record UserInfo(int Id, string Username, string FullName, string Role, string Language);

public sealed record UserListItem(int Id, string Username, string FullName, string Role, string Language, bool Active,
    bool HasSecret, DateTime? LastLoginAt, DateTime? LockedUntil);

/// <summary>Rules for the secret a user logs in with.</summary>
public static class SecretPolicy
{
    /// <summary>Gatekeepers and quarry operators use a PIN; everyone else a password of 10+ characters.</summary>
    public static bool UsesPin(string role) => role is "GATEKEEPER" or "QUARRY_OPERATOR";

    public static void Validate(string role, string secret)
    {
        if (UsesPin(role))
        {
            PinHasher.Validate(secret);
            return;
        }
        if (secret.Length < 10 || secret.All(char.IsAsciiDigit))
            throw TruckGateException.Validation("PASSWORD_WEAK", "The password needs at least 10 characters, not only digits.");
    }
}

/// <summary>Web login with lockout. Every attempt that changes the account is in the audit log.</summary>
public sealed class AuthService(Db db)
{
    private static readonly Actor AuthActor = Actor.System("auth");

    // Verified when the user does not exist, so that the answer takes the same time.
    private static readonly string DummyHash = PinHasher.HashSecret("not-a-real-secret");

    private enum Outcome { Ok, Failed, Locked }

    public async Task<UserInfo> LoginAsync(string username, string secret, CancellationToken ct = default)
    {
        var (outcome, user, lockedSeconds) = await db.InTransactionAsync<(Outcome, UserInfo?, int)>(AuthActor, async (conn, tx) =>
        {
            var row = await conn.QuerySingleOrDefaultAsync<LoginRow>("""
                SELECT id, username, full_name, role, language, pin_hash, active, failed_logins,
                       greatest(0, extract(epoch FROM locked_until - now()))::int AS locked_seconds
                FROM tg.users WHERE username = @username FOR UPDATE
                """, new { username = username.Trim() }, tx);

            if (row is null || !row.Active || row.PinHash is null || row.Role == "QUARRY_OPERATOR")
            {
                PinHasher.Verify(secret, DummyHash);
                return (Outcome.Failed, (UserInfo?)null, 0);
            }
            if (row.LockedSeconds > 0) return (Outcome.Locked, null, row.LockedSeconds);

            if (!PinHasher.Verify(secret, row.PinHash))
            {
                var max = await conn.ExecuteScalarAsync<int>(
                    "SELECT coalesce((SELECT value::text::int FROM tg.settings WHERE key = 'auth.max_failed_logins'), 5)",
                    transaction: tx);
                var lockout = await conn.ExecuteScalarAsync<int>(
                    "SELECT coalesce((SELECT value::text::int FROM tg.settings WHERE key = 'auth.lockout_s'), 300)",
                    transaction: tx);
                if (row.FailedLogins + 1 >= max)
                {
                    await conn.ExecuteAsync("""
                        UPDATE tg.users SET failed_logins = 0, locked_until = now() + make_interval(secs => @lockout)
                        WHERE id = @id
                        """, new { id = row.Id, lockout = (double)lockout }, tx);
                    await GateService.InsertAlertAsync(conn, tx, "ACCOUNT_LOCKED", "MEDIUM", "users", row.Id.ToString(),
                        new { username = row.Username });
                    return (Outcome.Locked, null, lockout);
                }
                await conn.ExecuteAsync("UPDATE tg.users SET failed_logins = failed_logins + 1 WHERE id = @id",
                    new { id = row.Id }, tx);
                return (Outcome.Failed, null, 0);
            }

            await conn.ExecuteAsync(
                "UPDATE tg.users SET failed_logins = 0, locked_until = NULL, last_login_at = now() WHERE id = @id",
                new { id = row.Id }, tx);
            return (Outcome.Ok, new UserInfo(row.Id, row.Username, row.FullName, row.Role, row.Language), 0);
        }, ct);

        // Thrown after commit so that the failure counter is saved.
        return outcome switch
        {
            Outcome.Ok => user!,
            Outcome.Locked => throw TruckGateException.Unauthorized("ACCOUNT_LOCKED", $"Account locked for {lockedSeconds} s."),
            _ => throw TruckGateException.Unauthorized("LOGIN_FAILED", "Wrong username or secret."),
        };
    }

    public Task<UserInfo?> GetUserAsync(int id, CancellationToken ct = default) =>
        db.ReadAsync(async conn =>
        {
            var row = await conn.QuerySingleOrDefaultAsync<LoginRow>(
                "SELECT id, username, full_name, role, language, active FROM tg.users WHERE id = @id", new { id });
            return row is { Active: true } ? new UserInfo(row.Id, row.Username, row.FullName, row.Role, row.Language) : null;
        }, ct);

    /// <summary>Creates the first administrator (command line, on the server). Refuses if one exists.</summary>
    public Task<int> CreateFirstAdminAsync(string username, string fullName, string password, CancellationToken ct = default)
    {
        SecretPolicy.Validate("ADMIN", password);
        var hash = PinHasher.HashSecret(password);
        return db.InTransactionAsync(Actor.System("setup"), async (conn, tx) =>
        {
            await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('first-admin', 0))", transaction: tx);
            if (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM tg.users WHERE role = 'ADMIN')", transaction: tx))
                throw TruckGateException.Conflict("ADMIN_EXISTS", "An administrator already exists.");
            return await conn.ExecuteScalarAsync<int>("""
                INSERT INTO tg.users (username, full_name, role, pin_hash, pin_set_at)
                VALUES (@username, @fullName, 'ADMIN', @hash, now()) RETURNING id
                """, new { username, fullName, hash }, tx);
        }, ct);
    }

    private sealed class LoginRow
    {
        public int Id { get; set; }
        public string Username { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Role { get; set; } = "";
        public string Language { get; set; } = "ar";
        public string? PinHash { get; set; }
        public bool Active { get; set; }
        public int FailedLogins { get; set; }
        public int LockedSeconds { get; set; }
    }
}
