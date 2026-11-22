using System.Text.Json;

namespace TruckGate.Application.Alerts;

/// <summary>Short alert texts in Arabic and French for SMS and Telegram.</summary>
public static class AlertText
{
    private static readonly Dictionary<string, (string Ar, string Fr)> Titles = new(StringComparer.Ordinal)
    {
        ["GATE_BAD_SIGNATURE"] = ("وصل مزور (توقيع غير صالح)", "Ticket falsifié (signature invalide)"),
        ["GATE_UNKNOWN_DEVICE"] = ("وصل من هاتف غير مسجل", "Ticket d'un téléphone inconnu"),
        ["GATE_WRONG_DEVICE_KIND"] = ("وصل بمفتاح غير مسموح", "Ticket signé par une clé non autorisée"),
        ["GATE_ALREADY_USED"] = ("وصل مستعمل من قبل", "Ticket déjà utilisé"),
        ["GATE_VOIDED"] = ("وصل ملغى", "Ticket annulé"),
        ["GATE_DEVICE_REVOKED"] = ("وصل من هاتف ملغى", "Ticket d'un téléphone révoqué"),
        ["GATE_TRUCK_GAP"] = ("الشاحنة عادت بسرعة غير ممكنة", "Camion revenu trop vite"),
        ["GATE_ISSUED_IN_FUTURE"] = ("وقت الوصل في المستقبل", "Heure du ticket dans le futur"),
        ["GATE_TOO_EARLY"] = ("وصل أحدث من مدة الطريق", "Ticket trop récent pour le trajet"),
        ["GATE_CLOCK_MISMATCH"] = ("ساعة الهاتف غير مضبوطة", "Horloge du téléphone décalée"),
        ["GATE_ISSUED_BEFORE_REVOCATION"] = ("وصل من هاتف أُلغي لاحقاً", "Ticket émis avant révocation"),
        ["GATE_OUTSIDE_GEOFENCE"] = ("وصل خارج حدود المقلع", "Ticket hors de la carrière"),
        ["GATE_WRONG_KIND"] = ("نوع وصل غير مقبول", "Type de ticket refusé"),
        ["GATE_DIFFERENT_TRUCK"] = ("شاحنة مختلفة عن الوصل", "Camion différent du ticket"),
        ["GATE_EMPTY_ON_ENTRY"] = ("شاحنة فارغة عند الدخول", "Camion vide à l'entrée"),
        ["GATE_BAD_SHORT_CODE"] = ("رمز يدوي خاطئ", "Code manuel erroné"),
        ["GATE_FORGED_RECEIPT"] = ("وصل خروج مزور", "Reçu de sortie falsifié"),
        ["GATE_RECEIPT_MISMATCH"] = ("وصل خروج لا يطابق الرحلة", "Reçu de sortie incohérent"),
        ["GATE_ALREADY_EXITED"] = ("وصل خروج مستعمل من قبل", "Reçu de sortie déjà utilisé"),
        ["GATE_EXIT_LOADED"] = ("شاحنة خرجت محمّلة", "Camion sorti chargé"),
        ["ENTRY_NOT_DECIDED"] = ("دخول بدون قرار البواب", "Entrée sans décision du gardien"),
        ["NO_EXIT_SCAN"] = ("شاحنة لم يُسجَّل خروجها", "Camion sans sortie enregistrée"),
        ["SYNC_OVERDUE"] = ("هاتف المقلع لم يزامن", "Téléphone de carrière non synchronisé"),
        ["PARTIAL_LOADS_DRIVER"] = ("حمولات ناقصة متكررة لسائق", "Chargements partiels répétés (chauffeur)"),
        ["PARTIAL_LOADS_OPERATOR"] = ("حمولات ناقصة متكررة لعامل المقلع", "Chargements partiels répétés (opérateur)"),
        ["CAMERA_FAILED"] = ("كاميرا لا تعمل", "Caméra en panne"),
        ["SNAPSHOT_TAMPERED"] = ("صورة دليل معدَّلة", "Photo de preuve modifiée"),
        ["SNAPSHOT_MISSING"] = ("صورة دليل مفقودة", "Photo de preuve manquante"),
        ["SYNC_BAD_SIGNATURE"] = ("هاتف أرسل وصلاً بتوقيع غير صالح", "Téléphone : ticket à signature invalide"),
        ["SYNC_TICKET_CONFLICT"] = ("وصلان مختلفان بنفس الرقم", "Deux tickets différents, même numéro"),
        ["SYNC_BAD_VOID"] = ("إلغاء وصل بتوقيع غير صالح", "Annulation à signature invalide"),
        ["SYNC_VOID_AFTER_USE"] = ("إلغاء وصل بعد استعماله عند البوابة", "Ticket annulé après passage au portail"),
        ["SYNC_PHOTO_MISMATCH"] = ("صورة المقلع لا تطابق الوصل", "Photo de carrière non conforme au ticket"),
        ["TEST"] = ("رسالة تجريبية من TruckGate", "Message de test TruckGate"),
    };

    public static string Title(string type, string lang) =>
        Titles.TryGetValue(type, out var t) ? (lang == "fr" ? t.Fr : t.Ar) : type;

    public static string Severity(string severity, string lang) => (severity, lang) switch
    {
        ("CRITICAL", "fr") => "CRITIQUE",
        ("CRITICAL", _) => "حرج",
        ("MEDIUM", "fr") => "MOYEN",
        ("MEDIUM", _) => "متوسط",
        (_, "fr") => "INFO",
        _ => "معلومة",
    };

    /// <summary>One or two lines: severity, title, the main identifier, local time.</summary>
    public static string Format(string type, string severity, string paramsJson, DateTime createdAtUtc, string lang,
        TimeZoneInfo zone)
    {
        string? detail = null;
        try
        {
            using var doc = JsonDocument.Parse(paramsJson);
            var p = doc.RootElement;
            foreach (var key in new[] { "ticket_id", "trip_number", "label", "error", "driver_id", "operator_id" })
                if (p.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                {
                    detail = $"{key switch { "driver_id" => lang == "fr" ? "chauffeur " : "سائق ", "operator_id" => lang == "fr" ? "opérateur " : "عامل ", _ => "" }}{v}";
                    break;
                }
            if (p.TryGetProperty("count", out var c)) detail += $" ×{c}";
        }
        catch (JsonException) { }
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc), zone);
        var when = local.ToString("HH:mm dd/MM", System.Globalization.CultureInfo.InvariantCulture);
        return $"[{Severity(severity, lang)}] {Title(type, lang)}{(detail is null ? "" : " — " + detail)} {when}";
    }

    public static int Rank(string severity) => severity switch { "CRITICAL" => 3, "MEDIUM" => 2, _ => 1 };
}
