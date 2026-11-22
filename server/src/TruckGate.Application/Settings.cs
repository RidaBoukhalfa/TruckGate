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
