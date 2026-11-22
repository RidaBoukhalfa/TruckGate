using Dapper;
using TruckGate.Data;

namespace TruckGate.Application;

public sealed record AuditVerification(bool Ok, long Checked, long? FirstBadId, string? Problem);

public sealed record AuditAnchor(long AuditId, string Hash, DateTime CreatedAt);

public sealed record SequenceGap(int DeviceId, long GapFrom, long GapTo);

/// <summary>Integrity checks and integrity-related reports.</summary>
public sealed class AuditService(Db db)
{
    /// <summary>Recomputes the whole hash chain and checks every anchor.</summary>
    public Task<AuditVerification> VerifyAsync(CancellationToken ct = default) =>
        db.ReadAsync(async conn =>
        {
            var r = await conn.QuerySingleAsync<(long Checked, long? FirstBadId, string? Problem)>(
                "SELECT checked, first_bad_id, problem FROM tg.audit_verify()");
            return new AuditVerification(r.Problem is null, r.Checked, r.FirstBadId, r.Problem);
        }, ct);

    /// <summary>
    /// Records the latest hash as an anchor. The returned hash is meant to leave the factory
    /// (SMS/Telegram, step 5) so that even a full rewrite of the database is detectable.
    /// </summary>
    public Task<AuditAnchor?> AnchorAsync(Actor actor, CancellationToken ct = default) =>
        db.InTransactionAsync(actor, async (conn, tx) =>
        {
            var r = await conn.QuerySingleOrDefaultAsync<AnchorRow>(
                "SELECT audit_id, hash, created_at FROM tg.audit_anchor()", transaction: tx);
            return r is null ? null : new AuditAnchor(r.AuditId, Convert.ToHexStringLower(r.Hash), r.CreatedAt);
        }, ct);

    private sealed class AnchorRow
    {
        public long AuditId { get; set; }
        public byte[] Hash { get; set; } = [];
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>Ticket numbers never seen for each quarry phone (deleted or hidden tickets show up here).</summary>
    public Task<IReadOnlyList<SequenceGap>> SequenceGapsAsync(CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<SequenceGap>>(async conn =>
            (await conn.QueryAsync<(int DeviceId, long GapFrom, long GapTo)>(
                "SELECT device_id, gap_from, gap_to FROM tg.ticket_sequence_gaps ORDER BY device_id, gap_from"))
            .Select(g => new SequenceGap(g.DeviceId, g.GapFrom, g.GapTo))
            .ToList(), ct);
}
