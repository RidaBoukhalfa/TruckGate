using Dapper;
using Npgsql;
using TruckGate.Application;
using TruckGate.Data;

namespace TruckGate.Server.Tests;

[Collection("db")]
public class IntegrityTests(DatabaseFixture fx)
{
    [Fact]
    public async Task Audit_chain_is_valid_after_normal_activity()
    {
        var fleet = await fx.NewFleetAsync();
        var now = await fx.DbNowAsync();
        var scan = await fx.Gate.ScanEntryAsync(
            new ScanEntryRequest(fx.GateId, fleet.Phone.Ticket(1, fleet.TruckId, fleet.DriverId, now - 900), null),
            DatabaseFixture.Tester);
        await fx.Gate.RecordEntryDecisionAsync(scan.ScanId, EntryDecision.Full, null, null, DatabaseFixture.Tester);

        var result = await fx.Audit.VerifyAsync();

        Assert.True(result.Ok, $"{result.Problem} at {result.FirstBadId}");
        Assert.True(result.Checked > 10);
        var actor = await fx.Db.ReadAsync(c => c.ExecuteScalarAsync<string>(
            "SELECT actor FROM tg.audit_log WHERE entity = 'gate_scans' AND entity_id = @id",
            new { id = scan.ScanId.ToString() }));
        Assert.Equal("dev:tests", actor);
    }

    [Theory]
    [InlineData("DELETE FROM tg.trips")]
    [InlineData("DELETE FROM tg.audit_log")]
    [InlineData("UPDATE tg.gate_scans SET outcome = 'ACCEPT'")]
    [InlineData("UPDATE tg.tickets SET truck_id = 1")]
    [InlineData("UPDATE tg.audit_log SET actor = 'nobody'")]
    [InlineData("TRUNCATE tg.alerts")]
    [InlineData("UPDATE tg.trips SET current_state = 'PAID'")]
    [InlineData("UPDATE tg.devices SET public_key_spki = '\\x00'")]
    [InlineData("INSERT INTO tg.trip_transitions VALUES ('ENTERED', 'PAID')")]
    public async Task Application_role_cannot_rewrite_history(string sql)
    {
        // Make sure there is at least one row of each kind to touch.
        var fleet = await fx.NewFleetAsync();
        var now = await fx.DbNowAsync();
        await fx.Gate.ScanEntryAsync(
            new ScanEntryRequest(fx.GateId, fleet.Phone.Ticket(1, fleet.TruckId, fleet.DriverId, now - 900), null),
            DatabaseFixture.Tester);

        await using var conn = await fx.App.OpenConnectionAsync();
        await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql));
    }

    [Fact]
    public async Task Superuser_editing_the_audit_log_is_detected()
    {
        var (admin, db) = await fx.CreateIsolatedDatabaseAsync();
        var adminSvc = new AdminService(db);
        var audit = new AuditService(db);
        var driver = await adminSvc.CreateDriverAsync("Ahmed", null, "EMPLOYEE", DatabaseFixture.Tester);
        await adminSvc.CreateTruckAsync(5, "05", null, driver, "01234 216 16", DatabaseFixture.Tester);
        Assert.True((await audit.VerifyAsync()).Ok);

        await using (var c = await admin.OpenConnectionAsync())
            await c.ExecuteAsync("""
                ALTER TABLE tg.audit_log DISABLE TRIGGER USER;
                UPDATE tg.audit_log SET payload = jsonb_set(payload, '{new,fleet_number}', '"99"') WHERE entity = 'trucks';
                ALTER TABLE tg.audit_log ENABLE TRIGGER USER;
                """);

        var result = await audit.VerifyAsync();
        Assert.False(result.Ok);
        Assert.Equal("HASH_MISMATCH", result.Problem);
    }

    [Fact]
    public async Task Superuser_rewriting_the_whole_chain_is_caught_by_the_anchor()
    {
        var (admin, db) = await fx.CreateIsolatedDatabaseAsync();
        var adminSvc = new AdminService(db);
        var audit = new AuditService(db);
        await adminSvc.CreateDriverAsync("Ahmed", null, "EMPLOYEE", DatabaseFixture.Tester);
        var anchor = await audit.AnchorAsync(DatabaseFixture.Tester);
        Assert.NotNull(anchor);
        Assert.Equal(64, anchor!.Hash.Length);

        await using (var c = await admin.OpenConnectionAsync())
            await c.ExecuteAsync("""
                ALTER TABLE tg.audit_log DISABLE TRIGGER USER;
                UPDATE tg.audit_log SET payload = jsonb_set(payload, '{new,full_name}', '"Someone else"') WHERE entity = 'drivers';
                DO $$
                DECLARE r record; v_prev bytea := decode(repeat('00', 32), 'hex');
                BEGIN
                    FOR r IN SELECT * FROM tg.audit_log ORDER BY id LOOP
                        UPDATE tg.audit_log SET prev_hash = v_prev,
                            hash = tg.audit_digest(r.id, r.at, r.actor, r.action, r.entity, r.entity_id, r.payload, v_prev)
                        WHERE id = r.id RETURNING hash INTO v_prev;
                    END LOOP;
                END $$;
                ALTER TABLE tg.audit_log ENABLE TRIGGER USER;
                """);

        var result = await audit.VerifyAsync();
        Assert.False(result.Ok);
        Assert.Equal("ANCHOR_MISMATCH", result.Problem);
    }

    [Fact]
    public async Task Migrations_run_once_and_an_edited_migration_stops_the_server()
    {
        var (admin, _) = await fx.CreateIsolatedDatabaseAsync();

        Assert.Empty(await DbMigrator.MigrateAsync(admin));

        await using (var c = await admin.OpenConnectionAsync())
            await c.ExecuteAsync("UPDATE tg_meta.migrations SET checksum = 'tampered' WHERE version = 'V001'");

        await Assert.ThrowsAsync<InvalidOperationException>(() => DbMigrator.MigrateAsync(admin));
    }
}
