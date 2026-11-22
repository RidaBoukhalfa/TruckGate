using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Npgsql;
using TruckGate.Application.Cameras;
using TruckGate.Crypto;
using TruckGate.Data;

namespace TruckGate.Application;

public sealed record SyncVoid(long At, string Reason, string Signature);

public sealed record SyncTicket(string Raw, int PrintCount, string? DriverChangeReason, SyncVoid? Void);

public sealed record SyncRequest(IReadOnlyList<SyncTicket> Tickets);

public sealed record SyncTicketResult(string? TicketId, long? Seq, string Status, string? Error, bool PhotoWanted);

public sealed record SyncResponse(long ServerTime, IReadOnlyList<SyncTicketResult> Results);

/// <summary>
/// Quarry phone upload (whenever the phone has a signal): tickets, print counts, voids and the photos
/// of loaded trucks. Everything is verified with the phone's own ticket key before it is stored.
/// </summary>
public sealed class SyncService(Db db, StorageOptions storage)
{
    public const int MaxTicketsPerRequest = 200;
    public const int MaxPhotoBytes = 8 * 1024 * 1024;
    private static readonly Actor SyncActor = Actor.System("sync");

    public Task<SyncResponse> SyncAsync(int deviceId, SyncRequest request, CancellationToken ct = default)
    {
        var tickets = request.Tickets ?? [];
        if (tickets.Count > MaxTicketsPerRequest)
            throw TruckGateException.Validation("SYNC_TOO_LARGE", $"At most {MaxTicketsPerRequest} tickets per request.");

        return db.InTransactionAsync(SyncActor, async (conn, tx) =>
        {
            var ticketKey = await conn.QuerySingleAsync<byte[]>(
                "SELECT public_key_spki FROM tg.devices WHERE id = @deviceId", new { deviceId }, tx);
            var results = new List<SyncTicketResult>();
            foreach (var t in tickets)
                results.Add(await SyncOneAsync(conn, tx, deviceId, ticketKey, t));
            await conn.ExecuteAsync("UPDATE tg.devices SET last_sync_at = now() WHERE id = @deviceId", new { deviceId }, tx);
            var now = await conn.ExecuteScalarAsync<long>("SELECT extract(epoch FROM now())::bigint", transaction: tx);
            return new SyncResponse(now, results);
        }, ct);
    }

    private static async Task<SyncTicketResult> SyncOneAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int deviceId,
        byte[] ticketKey, SyncTicket t)
    {
        DecodedTicket decoded;
        try
        {
            decoded = TicketCodec.Decode(Convert.FromBase64String(t.Raw ?? ""));
        }
        catch (Exception e) when (e is FormatException)
        {
            return new SyncTicketResult(null, null, "REJECTED", "MALFORMED", false);
        }
        var f = decoded.Fields;
        string id = f.TicketId;
        long seq = f.Seq;
        if (f.DeviceId != deviceId) return new SyncTicketResult(id, seq, "REJECTED", "WRONG_DEVICE", false);
        if (!EcdsaP256.Verify(ticketKey, decoded.SignedPart, decoded.Signature))
        {
            await Watchdog.AlertOnceAsync(conn, tx, "SYNC_BAD_SIGNATURE", "CRITICAL", "devices", deviceId.ToString(),
                DateTime.UtcNow.ToString("yyyy-MM-dd"), new { device_id = deviceId, ticket_id = id });
            return new SyncTicketResult(id, seq, "REJECTED", "BAD_SIGNATURE", false);
        }
        if (f.Kind != TicketFields.KindTransport) return new SyncTicketResult(id, seq, "REJECTED", "WRONG_KIND", false);

        // same lock as the gate, so a scan and a sync of one ticket never interleave
        await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", new { k = "ticket:" + id }, tx);
        var existing = await conn.QuerySingleOrDefaultAsync<byte[]>(
            "SELECT raw FROM tg.tickets WHERE device_id = @deviceId AND seq = @seq", new { deviceId, seq }, tx);
        if (existing is null)
        {
            await GateService.InsertTicketIfNewAsync(conn, tx, decoded, "SYNC");
        }
        else if (!existing.AsSpan(0, TicketFields.SignedLength).SequenceEqual(decoded.SignedPart))
        {
            // Same number, different content: a second phone with a cloned identity or a tampered database.
            await Watchdog.AlertOnceAsync(conn, tx, "SYNC_TICKET_CONFLICT", "CRITICAL", "tickets", id, "once",
                new { ticket_id = id, device_id = deviceId });
            return new SyncTicketResult(id, seq, "REJECTED", "TICKET_CONFLICT", false);
        }

        var events = (await conn.QueryAsync<(string Type, string? Count)>("""
            SELECT type, payload ->> 'count' FROM tg.ticket_events WHERE device_id = @deviceId AND seq = @seq
            """, new { deviceId, seq }, tx)).ToList();
        if (!events.Any(e => e.Type == "SYNCED"))
            await InsertEventAsync(conn, tx, deviceId, seq, "SYNCED", null, new
            {
                driver_change_reason = string.IsNullOrWhiteSpace(t.DriverChangeReason) ? null : t.DriverChangeReason.Trim(),
                print_count = t.PrintCount,
            });
        int recorded = events.Where(e => e.Type is "PRINTED" or "REPRINTED")
            .Select(e => int.TryParse(e.Count, out var n) ? n : 1).DefaultIfEmpty(0).Max();
        if (t.PrintCount > recorded)
            await InsertEventAsync(conn, tx, deviceId, seq, recorded == 0 ? "PRINTED" : "REPRINTED", null, new { count = t.PrintCount });

        if (t.Void is not null)
        {
            byte[] signature;
            try { signature = Convert.FromBase64String(t.Void.Signature ?? ""); }
            catch (FormatException) { signature = []; }
            if (!VoidRecord.Verify(ticketKey, id, t.Void.At, t.Void.Reason ?? "", signature))
            {
                await Watchdog.AlertOnceAsync(conn, tx, "SYNC_BAD_VOID", "CRITICAL", "tickets", id, "once",
                    new { ticket_id = id, device_id = deviceId });
                return new SyncTicketResult(id, seq, "REJECTED", "BAD_VOID", false);
            }
            if (!events.Any(e => e.Type == "VOIDED"))
            {
                await InsertEventAsync(conn, tx, deviceId, seq, "VOIDED", t.Void.Reason!.Trim(),
                    new { at = t.Void.At, signature = Convert.ToHexStringLower(signature) });
                if (await conn.ExecuteScalarAsync<bool>(
                        "SELECT EXISTS (SELECT 1 FROM tg.trips WHERE ticket_device_id = @deviceId AND ticket_seq = @seq)",
                        new { deviceId, seq }, tx))
                    await Watchdog.AlertOnceAsync(conn, tx, "SYNC_VOID_AFTER_USE", "MEDIUM", "tickets", id, "once",
                        new { ticket_id = id });
            }
        }

        var photoWanted = !await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM tg.ticket_photos WHERE device_id = @deviceId AND seq = @seq)", new { deviceId, seq }, tx);
        return new SyncTicketResult(id, seq, "OK", null, photoWanted);
    }

    /// <summary>Stores the loaded-truck photo if its SHA-256 is the one signed inside the ticket.</summary>
    public async Task StorePhotoAsync(int deviceId, long seq, byte[] jpeg, CancellationToken ct = default)
    {
        if (jpeg.Length == 0 || jpeg.Length > MaxPhotoBytes || !SnapshotFetcher.IsJpeg(jpeg))
            throw TruckGateException.Validation("PHOTO_INVALID", "The photo must be a JPEG of at most 8 MB.");
        var expected = await db.ReadAsync(conn => conn.QuerySingleOrDefaultAsync<byte[]>(
            "SELECT photo_sha256 FROM tg.tickets WHERE device_id = @deviceId AND seq = @seq", new { deviceId, seq }), ct)
            ?? throw TruckGateException.NotFound("TICKET_NOT_SYNCED", "Sync the ticket before its photo.");
        var sha = SHA256.HashData(jpeg);
        var id = $"Q{deviceId}-{seq:D6}";
        if (!sha.AsSpan().SequenceEqual(expected))
        {
            await db.InTransactionAsync(SyncActor, async (conn, tx) =>
                await Watchdog.AlertOnceAsync(conn, tx, "SYNC_PHOTO_MISMATCH", "MEDIUM", "tickets", id, "once", new { ticket_id = id }), ct);
            throw TruckGateException.Validation("PHOTO_MISMATCH", "This photo is not the one signed in the ticket.");
        }

        var now = DateTime.UtcNow;
        var rel = Path.Combine("photos", now.ToString("yyyy"), now.ToString("MM"), $"{id}.jpg");
        var full = Path.Combine(storage.Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (!File.Exists(full))
        {
            await using var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
            await fs.WriteAsync(jpeg, ct);
        }
        await db.InTransactionAsync(SyncActor, async (conn, tx) =>
        {
            await conn.ExecuteAsync("""
                INSERT INTO tg.ticket_photos (device_id, seq, path, sha256, size_bytes) VALUES (@deviceId, @seq, @path, @sha, @size)
                ON CONFLICT (device_id, seq) DO NOTHING
                """, new { deviceId, seq, path = rel.Replace('\\', '/'), sha, size = jpeg.Length }, tx);
            return 0;
        }, ct);
    }

    /// <summary>The loaded-truck photo, checked against the hash signed in the ticket.</summary>
    public async Task<byte[]> ReadPhotoAsync(int deviceId, long seq, CancellationToken ct = default)
    {
        var row = await db.ReadAsync(conn => conn.QuerySingleOrDefaultAsync<(string Path, byte[] Sha)>(
            "SELECT path, sha256 FROM tg.ticket_photos WHERE device_id = @deviceId AND seq = @seq", new { deviceId, seq }), ct);
        if (row.Path is null) throw TruckGateException.NotFound("PHOTO_NOT_FOUND", "No photo received for this ticket yet.");
        var root = Path.GetFullPath(storage.Root);
        var full = Path.GetFullPath(Path.Combine(root, row.Path));
        byte[]? bytes = full.StartsWith(root, StringComparison.Ordinal) && File.Exists(full) ? await File.ReadAllBytesAsync(full, ct) : null;
        if (bytes is null || !SHA256.HashData(bytes).AsSpan().SequenceEqual(row.Sha))
            throw TruckGateException.Conflict("PHOTO_TAMPERED", "The stored photo does not match the ticket.");
        return bytes;
    }

    private static Task InsertEventAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int deviceId, long seq, string type,
        string? reason, object payload) =>
        conn.ExecuteAsync("""
            INSERT INTO tg.ticket_events (device_id, seq, type, reason, payload)
            VALUES (@deviceId, @seq, @type, @reason, CAST(@payload AS jsonb))
            """, new { deviceId, seq, type, reason, payload = JsonSerializer.Serialize(payload) }, tx);
}
