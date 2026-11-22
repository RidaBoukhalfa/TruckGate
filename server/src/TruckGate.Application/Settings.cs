using System.Globalization;
using Dapper;
using Npgsql;
using TruckGate.Crypto;

namespace TruckGate.Application;

internal static class Settings
{
    public static async Task<GatePolicyParameters> LoadGatePolicyAsync(NpgsqlConnection conn, NpgsqlTransaction? tx)
    {
        var rows = (await conn.QueryAsync<(string Key, string Value)>(
                "SELECT key, value::text FROM tg.settings WHERE key LIKE 'gate.%'", transaction: tx))
            .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);

        int Get(string key, int fallback) =>
            rows.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : fallback;

        var d = new GatePolicyParameters();
        return new GatePolicyParameters(
            Get("gate.min_travel_s", d.MinTravelSeconds),
            Get("gate.max_validity_s", d.MaxValiditySeconds),
            Get("gate.min_truck_gap_s", d.MinTruckGapSeconds),
            Get("gate.future_tolerance_s", d.FutureToleranceSeconds));
    }
}

/// <summary>All settings as raw JSON values, with typed readers and defaults.</summary>
public sealed class SettingsBag(IReadOnlyDictionary<string, string> raw)
{
    public static async Task<SettingsBag> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx = null) =>
        new((await conn.QueryAsync<(string Key, string Value)>("SELECT key, value::text FROM tg.settings", transaction: tx))
            .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal));

    public IReadOnlyDictionary<string, string> Raw => raw;

    private System.Text.Json.JsonElement? Get(string key)
    {
        if (!raw.TryGetValue(key, out var v)) return null;
        try { return System.Text.Json.JsonDocument.Parse(v).RootElement.Clone(); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public int Int(string key, int fallback) =>
        Get(key) is { ValueKind: System.Text.Json.JsonValueKind.Number } e && e.TryGetInt32(out var n) ? n : fallback;

    public decimal Decimal(string key, decimal fallback) =>
        Get(key) is { ValueKind: System.Text.Json.JsonValueKind.Number } e && e.TryGetDecimal(out var n) ? n : fallback;

    public bool Bool(string key, bool fallback) => Get(key) switch
    {
        { ValueKind: System.Text.Json.JsonValueKind.True } => true,
        { ValueKind: System.Text.Json.JsonValueKind.False } => false,
        _ => fallback,
    };

    public string String(string key, string fallback) =>
        Get(key) is { ValueKind: System.Text.Json.JsonValueKind.String } e ? e.GetString() ?? fallback : fallback;

    public IReadOnlyList<string> Strings(string key) =>
        Get(key) is { ValueKind: System.Text.Json.JsonValueKind.Array } e
            ? e.EnumerateArray().Where(x => x.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(x => x.GetString()!).Where(x => x.Length > 0).ToList()
            : [];
}
