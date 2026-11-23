package dz.truckgate.quarry.core

import dz.truckgate.ticket.EcdsaP256
import dz.truckgate.ticket.TicketFields
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.Base64

/** Checks for quarry-core, runnable with or without a test framework. */
class CoreChecks {
    val failures = mutableListOf<String>()
    var passed = 0
        private set

    private fun check(name: String, ok: Boolean, detail: () -> String = { "" }) {
        if (ok) passed++ else failures += "$name ${detail()}"
    }

    fun runAll(): CoreChecks {
        geofence(); issuance(); escPos(); enrollment(); pin(); signedConfig(); serverAddress(); trucks()
        return this
    }

    // ------------------------------------------------------------------ geofence

    private val square = Geofence(
        listOf(GeoPoint(35_000_000, -1_000_000), GeoPoint(35_000_000, -990_000),
            GeoPoint(35_010_000, -990_000), GeoPoint(35_010_000, -1_000_000))
    )

    private fun geofence() {
        check("geo/inside", square.contains(GeoPoint(35_005_000, -995_000)))
        check("geo/outside-north", !square.contains(GeoPoint(35_020_000, -995_000)))
        check("geo/outside-east", !square.contains(GeoPoint(35_005_000, -980_000)))
        val concave = Geofence(listOf(GeoPoint(0, 0), GeoPoint(0, 10), GeoPoint(10, 10),
            GeoPoint(10, 6), GeoPoint(4, 6), GeoPoint(4, 4), GeoPoint(10, 4), GeoPoint(10, 0)))
        check("geo/concave-notch-outside", !concave.contains(GeoPoint(7, 5)))
        check("geo/concave-inside", concave.contains(GeoPoint(2, 5)))
    }

    // ------------------------------------------------------------------ issuance rules

    private val p = IssuanceParams()
    private val t0 = 1_795_000_000_000L // ms

    private fun ctx(
        phoneNowMs: Long = t0,
        fix: GpsFix? = GpsFix(t0 - 5_000, 35_005_000, -995_000, 8f, 5_000),
        truckLast: Long? = null,
        last: Long? = null,
        driverId: Int = 12,
        lastSync: Long = t0 / 1000 - 3600,
        unsynced: Int = 0,
        revoked: Boolean = false,
        active: Boolean = true,
    ) = IssuanceContext(phoneNowMs, fix, square, revoked, active, truckLast, last, 12, driverId, lastSync, unsynced)

    private fun issuance() {
        val ok = IssuanceRules.decide(ctx(), p)
        check("rules/normal-allowed", ok is IssuanceDecision.Allowed && ok.flags == 0 && ok.issuedAt == t0 / 1000) { "$ok" }

        val skew = IssuanceRules.decide(ctx(phoneNowMs = t0 + 900_000), p)
        check("rules/clock-mismatch-uses-gps-time",
            skew is IssuanceDecision.Allowed && skew.issuedAt == t0 / 1000 && skew.clockDelta == 900 &&
                skew.flags and TicketFields.FLAG_CLOCK_MISMATCH != 0) { "$skew" }

        val noGps = IssuanceRules.decide(ctx(fix = null), p)
        check("rules/no-gps-allowed-flagged",
            noGps is IssuanceDecision.Allowed && noGps.flags == TicketFields.FLAG_NO_GPS && noGps.latE6 == 0) { "$noGps" }

        val staleFix = IssuanceRules.decide(ctx(fix = GpsFix(t0 - 600_000, 35_005_000, -995_000, 8f, 600_000)), p)
        check("rules/stale-fix-is-no-gps", staleFix is IssuanceDecision.Allowed && staleFix.flags == TicketFields.FLAG_NO_GPS)

        val inaccurate = IssuanceRules.decide(ctx(fix = GpsFix(t0, 35_005_000, -995_000, 900f, 0)), p)
        check("rules/inaccurate-fix-is-no-gps",
            inaccurate is IssuanceDecision.Allowed && inaccurate.flags == TicketFields.FLAG_NO_GPS)

        val outside = IssuanceRules.decide(ctx(fix = GpsFix(t0, 35_200_000, -995_000, 8f, 0)), p)
        check("rules/outside-geofence-blocked",
            outside is IssuanceDecision.Blocked && BlockReason.OutsideGeofence in outside.reasons) { "$outside" }

        val cooldown = IssuanceRules.decide(ctx(truckLast = t0 / 1000 - 300), p)
        check("rules/truck-cooldown",
            cooldown is IssuanceDecision.Blocked && cooldown.reasons.contains(BlockReason.TruckCooldown(900))) { "$cooldown" }

        val afterCooldown = IssuanceRules.decide(ctx(truckLast = t0 / 1000 - 1200), p)
        check("rules/cooldown-exact-boundary-allowed", afterCooldown is IssuanceDecision.Allowed)

        val backwards = IssuanceRules.decide(ctx(fix = null, truckLast = t0 / 1000 + 3600, last = t0 / 1000 + 3600), p)
        check("rules/clock-moved-back-blocks-and-flags",
            backwards is IssuanceDecision.Blocked && backwards.reasons.contains(BlockReason.TruckCooldown(1200))) { "$backwards" }
        val backwardsOtherTruck = IssuanceRules.decide(ctx(fix = null, last = t0 / 1000 + 3600), p)
        check("rules/clock-moved-back-flag",
            backwardsOtherTruck is IssuanceDecision.Allowed &&
                backwardsOtherTruck.flags and TicketFields.FLAG_CLOCK_MISMATCH != 0) { "$backwardsOtherTruck" }

        val overdue = IssuanceRules.decide(ctx(lastSync = t0 / 1000 - 90_000), p)
        check("rules/sync-overdue", overdue is IssuanceDecision.Blocked && overdue.reasons.contains(BlockReason.SyncOverdue(25)))

        val tooMany = IssuanceRules.decide(ctx(unsynced = 500), p)
        check("rules/too-many-unsynced", tooMany is IssuanceDecision.Blocked)

        val driver = IssuanceRules.decide(ctx(driverId = 13), p)
        check("rules/driver-changed-flag",
            driver is IssuanceDecision.Allowed && driver.flags == TicketFields.FLAG_DRIVER_CHANGED)

        val all = IssuanceRules.decide(ctx(revoked = true, active = false), p)
        check("rules/revoked-and-inactive",
            all is IssuanceDecision.Blocked && all.reasons.containsAll(listOf(BlockReason.DeviceRevoked, BlockReason.TruckInactive)))
    }

    // ------------------------------------------------------------------ ESC/POS

    private fun escPos() {
        val img = MonoBitmap(10, 30)
        img[0, 0] = true; img[9, 0] = true; img[8, 29] = true
        val chunks = EscPosEncoder.encode(img, PrinterProfile(PaperWidth.MM58, rowsPerChunk = 24, feedLinesAfter = 3))
        check("escpos/init", chunks[0].contentEquals(byteArrayOf(0x1B, 0x40)))
        check("escpos/two-raster-chunks", chunks.size == 4) { "${chunks.size}" }
        val c1 = chunks[1]
        check("escpos/gs-v-0-header", c1[0] == 0x1D.toByte() && c1[1] == 0x76.toByte() && c1[2] == 0x30.toByte() && c1[3] == 0.toByte())
        check("escpos/width-bytes", c1[4] == 2.toByte() && c1[5] == 0.toByte())
        check("escpos/rows", c1[6] == 24.toByte() && chunks[2][6] == 6.toByte())
        check("escpos/first-pixel-msb", c1[8] == 0x80.toByte())
        check("escpos/tenth-pixel", c1[9] == 0x40.toByte())
        val last = chunks[2]
        check("escpos/last-row-pixel-8", last[8 + 5 * 2 + 1] == 0x80.toByte())
        check("escpos/feed", chunks[3].contentEquals(byteArrayOf(0x1B, 0x64, 3)))
        val cut = EscPosEncoder.encode(img, PrinterProfile(cut = true))
        check("escpos/cut", cut.last().contentEquals(byteArrayOf(0x1D, 0x56, 0x42, 0x00)))
        val tooWide = runCatching { EscPosEncoder.encode(MonoBitmap(400, 1), PrinterProfile(PaperWidth.MM58)) }
        check("escpos/too-wide-refused", tooWide.isFailure)
        check("escpos/80mm-allows-576", runCatching { EscPosEncoder.encode(MonoBitmap(576, 1), PrinterProfile(PaperWidth.MM80)) }.isSuccess)
        check("escpos/qr-module", EscPosEncoder.qrModuleSize(57, 384) == 6)

        val grey = IntArray(100) { if (it % 10 < 5) 30 else 220 }
        val th = Dither.threshold(grey, 10, 10)
        check("dither/threshold", th.blackCount() == 50)
        val mid = IntArray(10_000) { 128 }
        val fs = Dither.floydSteinberg(mid, 100, 100).blackCount()
        check("dither/floyd-50-percent", fs in 4_500..5_500) { "$fs" }
    }

    // ------------------------------------------------------------------ enrollment / config / pin

    private val serverKeys = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
    private val serverSpkiB64 = Base64.getEncoder().encodeToString(serverKeys.public.encoded)

    private fun enrollment() {
        val ok = EnrollmentInvite.parse("TGE1|http://192.168.1.10:5080/|AbCdEfGhIjKlMnOpQrStUv|$serverSpkiB64")
        check("enroll/parse", ok.serverUrl == "http://192.168.1.10:5080" && ok.token == "AbCdEfGhIjKlMnOpQrStUv")
        val bad = listOf(
            "TGE2|http://x|AbCdEfGhIjKlMnOpQrStUv|$serverSpkiB64",
            "TGE1|ftp://x|AbCdEfGhIjKlMnOpQrStUv|$serverSpkiB64",
            "TGE1|http://x|short|$serverSpkiB64",
            "TGE1|http://x|AbCdEfGhIjKlMnOpQrStUv|bm90LWEta2V5",
            "TGE1|http://x|AbCdEfGhIjKlMnOpQrStUv",
        )
        bad.forEachIndexed { i, s -> check("enroll/reject-$i", runCatching { EnrollmentInvite.parse(s) }.isFailure) }
        val msg = String(DeviceAuth.message(3, 1_795_000_000, "get", "/api/devices/3/config", ByteArray(0)))
        check("auth/message", msg == "TGAUTH1|3|1795000000|GET|/api/devices/3/config|" +
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855") { msg }
    }

    private fun signedConfig() {
        val payload = """{"version":7,"trucks":[{"id":5,"fleetNumber":"05"}],"operators":[{"id":2,"name":"عمر"}]}"""
        val raw = EcdsaDer.toRaw(Signature.getInstance("SHA256withECDSA").run {
            initSign(serverKeys.private); update(payload.toByteArray(Charsets.UTF_8)); sign()
        })
        val sig = Base64.getEncoder().encodeToString(raw)
        check("config/valid", SignedConfig.verify(payload, sig, serverKeys.public.encoded))
        check("config/tampered", !SignedConfig.verify(payload.replace("05", "06"), sig, serverKeys.public.encoded))
        check("config/garbage-signature", !SignedConfig.verify(payload, "not base64!", serverKeys.public.encoded))
    }

    private fun serverAddress() {
        fun sign(text: String) = Base64.getEncoder().encodeToString(EcdsaDer.toRaw(Signature.getInstance("SHA256withECDSA").run {
            initSign(serverKeys.private); update(text.toByteArray(Charsets.UTF_8)); sign()
        }))
        val body = "TGA1|http://192.168.1.20:5080|1795000000"
        val ok = runCatching { ServerAddress.parseAndVerify("$body|${sign(body)}", serverKeys.public.encoded) }
        check("addr/valid", ok.getOrNull() == ServerAddress("http://192.168.1.20:5080", 1_795_000_000)) { "$ok" }
        val other = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
        check("addr/other-server-refused", runCatching {
            ServerAddress.parseAndVerify("$body|${sign(body)}", other.public.encoded) }.exceptionOrNull() is SecurityException)
        check("addr/tampered-url-refused", runCatching {
            ServerAddress.parseAndVerify("TGA1|http://192.168.1.66:5080|1795000000|${sign(body)}", serverKeys.public.encoded)
        }.isFailure)
        check("addr/not-an-address-code", runCatching {
            ServerAddress.parseAndVerify("TGE1|http://192.168.1.20:5080|x|y", serverKeys.public.encoded) }.isFailure)
        check("addr/bad-url", runCatching {
            val b = "TGA1|ftp://192.168.1.20|1795000000"; ServerAddress.parseAndVerify("$b|${sign(b)}", serverKeys.public.encoded)
        }.isFailure)
    }

    private fun pin() {
        val h = PinHash.hash("4821", "0123456789abcdef".toByteArray(), 20_000)
        check("pin/ok", PinHash.verify("4821", h))
        // Reference vector (Python hashlib.pbkdf2_hmac); the C# server must produce verifiable hashes too.
        val reference = "pbkdf2-sha256\$100000\$MDEyMzQ1Njc4OWFiY2RlZg==\$1XoFd9w4yN5wybLJRH2X7gyB/cRvKe8JHMBKQ3mu1nM="
        check("pin/reference-vector", PinHash.verify("4821", reference))
        check("pin/wrong", !PinHash.verify("4822", h))
        check("pin/bad-format", !PinHash.verify("4821", "plain$4821"))
        check("pin/too-few-iterations-refused", !PinHash.verify("4821", PinHash.hash("4821", ByteArray(16), 10_000).replace("$10000$", "$10$")))
    }

    // ------------------------------------------------------------------ trucks registered on the phone

    private fun trucks() {
        check("plate/arabic-digits", PlateRules.normalize("٠٩٨٧٦-١١٢-٣١") == "09876 112 31") { "${PlateRules.normalize("٠٩٨٧٦-١١٢-٣١")}" }
        check("plate/11-digits", PlateRules.normalize("12345611616") == "123456 116 16")
        check("plate/short-kept", PlateRules.normalize("1234567") == "1234567")
        check("plate/letters-refused", PlateRules.normalize("12AB34") == null)
        check("plate/too-short", PlateRules.normalize("12345") == null)
        check("plate/key-ignores-format", PlateRules.key("09876 112 31") == PlateRules.key("٠٩٨٧٦-١١٢-٣١"))
        check("ids/first-free", ProvisionalIds.next(listOf(40000..40009), setOf(40000, 40001)) == 40002)
        check("ids/next-block", ProvisionalIds.next(listOf(40010..40011, 40000..40001), setOf(40000, 40001)) == 40010)
        check("ids/exhausted", ProvisionalIds.next(listOf(40000..40001), setOf(40000, 40001)) == null)
        val ctx = IssuanceContext(phoneNowMs = 200_000_000L, fix = null, geofence = null, deviceRevoked = false, truckActive = true,
            truckLastIssuedAt = null, lastIssuedAt = null, defaultDriverId = null, driverId = 1, lastSyncAt = 0, unsyncedCount = 0)
        check("sync/overdue-blocks", IssuanceRules.decide(ctx, IssuanceParams()) is IssuanceDecision.Blocked)
        check("sync/overdue-warns-only", IssuanceRules.decide(ctx, IssuanceParams(syncOverdueBlocks = false)) is IssuanceDecision.Allowed)
        check("sync/overdue-hours", IssuanceRules.syncOverdueHours(200_000, 0, IssuanceParams()) == 55L)
    }

    private object EcdsaDer {
        fun toRaw(der: ByteArray): ByteArray = EcdsaP256.derToRaw(der)
    }
}

object CoreLocalRunner {
    @JvmStatic
    fun main(args: Array<String>) {
        val c = CoreChecks().runAll()
        c.failures.forEach { println("FAIL $it") }
        println("passed=${c.passed} failed=${c.failures.size}")
        if (c.failures.isNotEmpty()) kotlin.system.exitProcess(1)
    }
}
