using System.Text.Json;
using Dapper;
using TruckGate.Data;

namespace TruckGate.Application;

public sealed record SettingItem(string Key, string Value, string Type, string? Min, string? Max);

/// <summary>Administrator-editable settings. Only known keys, each with its type and bounds; every change is audited.</summary>
public sealed class SettingsService(Db db)
{
    private sealed record Rule(string Type, decimal Min = 0, decimal Max = 0, string[]? Options = null);

    private static readonly Dictionary<string, Rule> Rules = new(StringComparer.Ordinal)
    {
        ["gate.min_travel_s"] = new("int", 0, 7200),
        ["gate.max_validity_s"] = new("int", 600, 86400),
        ["gate.min_truck_gap_s"] = new("int", 0, 7200),
        ["gate.future_tolerance_s"] = new("int", 0, 3600),
        ["gate.min_unload_s"] = new("int", 0, 3600),
        ["gate.entry_decision_alert_s"] = new("int", 60, 86400),
        ["trip.exit_deadline_s"] = new("int", 600, 172800),
        ["trip.price_per_trip"] = new("decimal", 0, 100_000_000),
        ["trip.partial_pay_percent"] = new("decimal", 0, 100),
        ["trip.currency"] = new("string", 1, 8),
        ["trip.partial_alert_threshold"] = new("int", 0, 100),
        ["trip.partial_alert_window_days"] = new("int", 1, 90),
        ["review.sample_percent"] = new("decimal", 0, 100),
        ["site.timezone"] = new("string", 1, 64),
        ["quarry.truck_cooldown_s"] = new("int", 0, 7200),
        ["quarry.clock_mismatch_s"] = new("int", 30, 3600),
        ["quarry.sync_max_age_s"] = new("int", 3600, 604800),
        ["quarry.gps_max_age_s"] = new("int", 10, 3600),
        ["quarry.gps_max_accuracy_m"] = new("int", 5, 5000),
        ["quarry.max_unsynced_tickets"] = new("int", 10, 5000),
        ["auth.max_failed_logins"] = new("int", 3, 20),
        ["auth.lockout_s"] = new("int", 30, 86400),
        ["alerts.language"] = new("enum", Options: ["ar", "fr"]),
        ["alerts.sms_enabled"] = new("bool"),
        ["alerts.sms_port"] = new("string", 0, 64),
        ["alerts.sms_recipients"] = new("phones", 0, 10),
        ["alerts.sms_min_severity"] = new("enum", Options: ["CRITICAL", "MEDIUM", "INFO"]),
        ["alerts.sms_max_per_hour"] = new("int", 0, 200),
        ["alerts.telegram_enabled"] = new("bool"),
        ["alerts.telegram_chat_ids"] = new("chats", 0, 10),
        ["alerts.telegram_min_severity"] = new("enum", Options: ["CRITICAL", "MEDIUM", "INFO"]),
        ["alerts.telegram_photos"] = new("bool"),
        ["alerts.sms_transport"] = new("enum", Options: ["MODEM", "ZTE_ROUTER"]),
        ["alerts.sms_router_url"] = new("string", 0, 200),
        ["alerts.sms_router_user"] = new("string", 0, 64),
        ["alerts.whatsapp_enabled"] = new("bool"),
        ["alerts.whatsapp_phone_number_id"] = new("string", 0, 32),
        ["alerts.whatsapp_recipients"] = new("phones", 0, 10),
        ["alerts.whatsapp_template"] = new("string", 1, 512),
        ["alerts.whatsapp_template_language"] = new("string", 2, 10),
        ["alerts.whatsapp_min_severity"] = new("enum", Options: ["CRITICAL", "MEDIUM", "INFO"]),
        ["alerts.whatsapp_photos"] = new("bool"),
        ["alerts.whatsapp_api_version"] = new("string", 2, 8),
    };

    public Task<IReadOnlyList<SettingItem>> ListAsync(CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<SettingItem>>(async conn =>
        {
            var values = (await conn.QueryAsync<(string Key, string Value)>("SELECT key, value::text FROM tg.settings"))
                .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
            return Rules.Select(r => new SettingItem(r.Key, values.GetValueOrDefault(r.Key, "null"), r.Value.Type,
                    r.Value.Options is null ? r.Value.Min.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Join(',', r.Value.Options),
                    r.Value.Options is null ? r.Value.Max.ToString(System.Globalization.CultureInfo.InvariantCulture) : null))
                .OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
        }, ct);

    public Task SetAsync(string key, JsonElement value, Actor actor, CancellationToken ct = default)
    {
        if (!Rules.TryGetValue(key, out var rule)) throw TruckGateException.Validation("SETTING_UNKNOWN", $"Unknown setting {key}.");
        var json = Validate(key, rule, value);
        return db.InTransactionAsync(actor, async (conn, tx) =>
        {
            await conn.ExecuteAsync("""
                INSERT INTO tg.settings (key, value) VALUES (@key, CAST(@json AS jsonb))
                ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
                """, new { key, json }, tx);
            return 0;
        }, ct);
    }

    private static string Validate(string key, Rule r, JsonElement v)
    {
        TruckGateException Bad() => TruckGateException.Validation("SETTING_INVALID", $"Invalid value for {key}.");
        switch (r.Type)
        {
            case "int":
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var i) || i < r.Min || i > r.Max) throw Bad();
                return i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case "decimal":
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetDecimal(out var d) || d < r.Min || d > r.Max) throw Bad();
                return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case "bool":
                if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Bad();
                return v.GetBoolean() ? "true" : "false";
            case "string":
                if (v.ValueKind != JsonValueKind.String) throw Bad();
                var s = v.GetString()!.Trim();
                if (s.Length < r.Min || s.Length > r.Max) throw Bad();
                if (key == "site.timezone")
                    try { TimeZoneInfo.FindSystemTimeZoneById(s); }
                    catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw Bad(); }
                var valid = key switch
                {
                    "alerts.sms_router_url" => s.Length == 0 || (Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"),
                    "alerts.whatsapp_phone_number_id" => s.All(char.IsAsciiDigit),
                    "alerts.whatsapp_template" => s.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'),
                    "alerts.whatsapp_template_language" => System.Text.RegularExpressions.Regex.IsMatch(s, "^[a-z]{2,3}(_[A-Z]{2})?$"),
                    "alerts.whatsapp_api_version" => System.Text.RegularExpressions.Regex.IsMatch(s, @"^v\d{2}\.\d$"),
                    _ => true,
                };
                if (!valid) throw Bad();
                return JsonSerializer.Serialize(s);
            case "enum":
                if (v.ValueKind != JsonValueKind.String || !r.Options!.Contains(v.GetString())) throw Bad();
                return JsonSerializer.Serialize(v.GetString());
            case "phones":
            case "chats":
                if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() > r.Max) throw Bad();
                // phone numbers may be typed with spaces or dashes: "+213 550 00 00 01"
                var items = v.EnumerateArray().Select(x => x.ValueKind != JsonValueKind.String ? null
                    : r.Type == "phones" ? new string(x.GetString()!.Where(c => c == '+' || char.IsAsciiDigit(c) || char.IsLetter(c)).ToArray())
                    : x.GetString()!.Trim()).ToList();
                if (items.Any(x => x is null || x.Length is 0 or > 32)) throw Bad();
                if (r.Type == "phones" && items.Any(x => !x!.TrimStart('+').All(char.IsAsciiDigit) || x.Length < 6)) throw Bad();
                if (r.Type == "chats" && items.Any(x => !x!.TrimStart('-').All(char.IsAsciiDigit) && !x.StartsWith('@'))) throw Bad();
                return JsonSerializer.Serialize(items);
            default:
                throw Bad();
        }
    }
}
