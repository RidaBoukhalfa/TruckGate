package dz.truckgate.quarry

import android.app.Application
import android.os.Build
import android.security.keystore.KeyPermanentlyInvalidatedException
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import dz.truckgate.quarry.core.BlockReason
import dz.truckgate.quarry.core.EnrollmentInvite
import dz.truckgate.quarry.core.IssuanceContext
import dz.truckgate.quarry.core.IssuanceDecision
import dz.truckgate.quarry.core.IssuanceRules
import dz.truckgate.quarry.core.PaperWidth
import dz.truckgate.quarry.core.PinHash
import dz.truckgate.quarry.core.PrinterProfile
import dz.truckgate.quarry.core.SignedConfig
import dz.truckgate.quarry.data.DeviceConfig
import dz.truckgate.quarry.data.Driver
import dz.truckgate.quarry.data.Operator
import dz.truckgate.quarry.data.Store
import dz.truckgate.quarry.data.StoredTicket
import dz.truckgate.quarry.data.Truck
import dz.truckgate.quarry.gps.GpsTracker
import dz.truckgate.quarry.net.ServerApi
import dz.truckgate.quarry.net.ServerException
import dz.truckgate.quarry.print.BluetoothPrinter
import dz.truckgate.quarry.print.PairedPrinter
import dz.truckgate.quarry.print.PrintLine
import dz.truckgate.quarry.print.TicketRenderer
import dz.truckgate.quarry.security.Biometric
import dz.truckgate.quarry.security.DeviceKeys
import dz.truckgate.quarry.security.UnlockedTicketSigner
import dz.truckgate.ticket.EcdsaP256
import dz.truckgate.ticket.TicketFields
import dz.truckgate.ticket.TicketIssuer
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.security.MessageDigest
import java.security.Signature
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Base64

enum class Screen { Enroll, Login, Home, NewTicket, History, TicketDetail, Settings }

class QuarryViewModel(app: Application) : AndroidViewModel(app) {
    private val store = Store(app)
    val gps = GpsTracker(app)
    private val printer = BluetoothPrinter(app)

    var screen by mutableStateOf(Screen.Enroll)
        private set
    var config by mutableStateOf<DeviceConfig?>(null)
        private set
    var operator by mutableStateOf<Operator?>(null)
        private set
    var message by mutableStateOf<String?>(null)
    var busy by mutableStateOf(false)
        private set

    // New ticket wizard
    var step by mutableStateOf(0)
    var truck by mutableStateOf<Truck?>(null)
        private set
    var driver by mutableStateOf<Driver?>(null)
        private set
    var driverChangeReason by mutableStateOf<String?>(null)
    var photo by mutableStateOf<File?>(null)
    var blocked by mutableStateOf<List<BlockReason>>(emptyList())
        private set

    var selected by mutableStateOf<StoredTicket?>(null)

        private set
    var tickets by mutableStateOf<List<StoredTicket>>(emptyList())
        private set

    private var wrongPins = 0
    private var lockedUntilMs = 0L

    init {
        config = store.get(K_CONFIG)?.let { runCatching { DeviceConfig.parse(it) }.getOrNull() }
        screen = if (store.get(K_DEVICE_ID) != null && DeviceKeys.exist()) Screen.Login else Screen.Enroll
        refreshTickets()
    }

    private fun str(id: Int, vararg args: Any): String = getApplication<Application>().getString(id, *args)

    val deviceId: Int? get() = store.get(K_DEVICE_ID)?.toIntOrNull()
    val serverUrl: String? get() = store.get(K_SERVER_URL)
    val printerAddress: String? get() = store.get(K_PRINTER)
    val paper: PaperWidth get() = runCatching { PaperWidth.valueOf(store.get(K_PAPER) ?: "MM58") }.getOrDefault(PaperWidth.MM58)
    val todayCount: Int
        get() = store.countSince(LocalDate.now().atStartOfDay(ZoneId.systemDefault()).toEpochSecond())

    fun go(target: Screen) {
        if (target == Screen.History) refreshTickets()
        screen = target
    }

    fun onPermissionsGranted() = gps.start()

    // ------------------------------------------------------------------ enrollment and configuration

    fun enroll(qrText: String) {
        val invite = runCatching { EnrollmentInvite.parse(qrText) }.getOrElse { message = str(R.string.error, it.message ?: ""); return }
        busy = true
        viewModelScope.launch {
            try {
                val cfg = withContext(Dispatchers.IO) {
                    DeviceKeys.create(invite.token.toByteArray(Charsets.UTF_8))
                    val enrolled = ServerApi(invite.serverUrl).enroll(invite.token, DeviceKeys.ticketPublicKey,
                        DeviceKeys.authPublicKey, DeviceKeys.attestationChain(), "${Build.MANUFACTURER} ${Build.MODEL}")
                    store.putAll(mapOf(
                        K_SERVER_URL to invite.serverUrl,
                        K_SERVER_KEY to Base64.getEncoder().encodeToString(invite.serverKeySpki),
                        K_DEVICE_ID to enrolled.deviceId.toString(),
                        K_LAST_SYNC to enrolled.serverTime.toString(),
                    ))
                    fetchConfig()
                }
                config = cfg
                screen = Screen.Login
            } catch (e: Exception) {
                message = str(R.string.error, e.message ?: e.javaClass.simpleName)
            } finally {
                busy = false
            }
        }
    }

    fun refreshConfig() {
        busy = true
        viewModelScope.launch {
            try {
                val cfg = withContext(Dispatchers.IO) { fetchConfig() }
                config = cfg
                message = str(R.string.config_updated, cfg.version)
            } catch (e: ServerException) {
                if (e.code == "DEVICE_NOT_ALLOWED") store.put(K_REVOKED, "1")
                message = str(R.string.error, e.message ?: e.code)
            } catch (e: Exception) {
                message = str(R.string.error, e.message ?: e.javaClass.simpleName)
            } finally {
                busy = false
            }
        }
    }

    /** Downloads, verifies (pinned server key, device id, no rollback) and stores the configuration. */
    private fun fetchConfig(): DeviceConfig {
        val id = deviceId ?: error("not enrolled")
        val serverKey = Base64.getDecoder().decode(store.get(K_SERVER_KEY) ?: error("no server key"))
        val signed = ServerApi(serverUrl ?: error("no server")).fetchConfig(id, gps.bestUnixSeconds()) {
            DeviceKeys.signWithAuthKey(it)
        }
        check(SignedConfig.verify(signed.payload, signed.signature, serverKey)) { "configuration signature is invalid" }
        val cfg = DeviceConfig.parse(signed.payload)
        check(cfg.deviceId == id) { "configuration is for another device" }
        val previous = store.get(K_CONFIG)?.let { runCatching { DeviceConfig.parse(it) }.getOrNull() }
        check(previous == null || cfg.generatedAt >= previous.generatedAt) { "older configuration refused" }
        store.putAll(mapOf(K_CONFIG to signed.payload, K_CONFIG_SIG to signed.signature,
            K_LAST_SYNC to cfg.generatedAt.toString()))
        return cfg
    }

    // ------------------------------------------------------------------ login

    fun login(op: Operator, pin: String) {
        val now = System.currentTimeMillis()
        if (now < lockedUntilMs) {
            message = str(R.string.login_locked, ((lockedUntilMs - now) / 1000).toInt())
            return
        }
        viewModelScope.launch {
            val ok = withContext(Dispatchers.Default) { PinHash.verify(pin, op.pinHash) }
            if (ok) {
                wrongPins = 0
                operator = op
                screen = Screen.Home
            } else {
                wrongPins++
                if (wrongPins >= 5) {
                    wrongPins = 0
                    lockedUntilMs = System.currentTimeMillis() + 60_000
                }
                message = str(R.string.login_wrong)
            }
        }
    }

    fun logout() {
        operator = null
        screen = Screen.Login
    }

    // ------------------------------------------------------------------ new ticket

    fun startTicket() {
        step = 0; truck = null; driver = null; driverChangeReason = null; photo = null; blocked = emptyList()
        screen = Screen.NewTicket
    }

    /** Minutes left before this truck can get a new ticket (20-minute rule), 0 if available. */
    fun waitMinutes(t: Truck): Int {
        val last = store.lastIssuedAtForTruck(t.id) ?: return 0
        val cooldown = config?.params?.truckCooldownS ?: 1200
        val left = cooldown - (gps.bestUnixSeconds() - last)
        return if (left <= 0) 0 else ((left + 59) / 60).toInt()
    }

    fun chooseTruck(t: Truck) {
        truck = t
        driver = t.defaultDriverId?.let { config?.driver(it) }
        driverChangeReason = null
        step = 1
    }

    fun chooseDriver(d: Driver) {
        driver = d
        if (d.id == truck?.defaultDriverId) driverChangeReason = null
    }

    val driverChanged: Boolean get() = truck?.defaultDriverId != null && driver?.id != truck?.defaultDriverId

    private fun decide(): IssuanceDecision {
        val cfg = config!!
        val t = truck!!
        return IssuanceRules.decide(
            IssuanceContext(
                phoneNowMs = System.currentTimeMillis(),
                fix = gps.latestFix(),
                geofence = cfg.geofence,
                deviceRevoked = store.get(K_REVOKED) == "1",
                truckActive = t.active,
                truckLastIssuedAt = store.lastIssuedAtForTruck(t.id),
                lastIssuedAt = store.lastIssuedAt(),
                defaultDriverId = t.defaultDriverId,
                driverId = driver!!.id,
                lastSyncAt = store.get(K_LAST_SYNC)?.toLongOrNull() ?: 0,
                // Ticket upload arrives with sync (step 6); until then only the configuration refresh counts.
                unsyncedCount = 0,
            ),
            cfg.params,
        )
    }

    fun signAndPrint(activity: FragmentActivity) {
        val cfg = config ?: return
        val t = truck ?: return
        val d = driver ?: return
        val op = operator ?: return
        val file = photo ?: return
        val decision = decide()
        if (decision is IssuanceDecision.Blocked) {
            blocked = decision.reasons
            return
        }
        val allowed = decision as IssuanceDecision.Allowed
        val signature = try {
            DeviceKeys.ticketSignature()
        } catch (e: KeyPermanentlyInvalidatedException) {
            message = str(R.string.key_invalidated)
            return
        }
        Biometric.unlock(activity, signature, str(R.string.biometric_title), str(R.string.biometric_subtitle),
            str(R.string.cancel),
            onUnlocked = { unlocked -> issue(unlocked, allowed, cfg, t, d, op, file) },
            onError = { message = it })
    }

    private fun issue(
        unlocked: Signature, a: IssuanceDecision.Allowed, cfg: DeviceConfig, t: Truck, d: Driver, op: Operator, file: File,
    ) {
        busy = true
        viewModelScope.launch {
            try {
                val seq = withContext(Dispatchers.IO) {
                    val photoHash = MessageDigest.getInstance("SHA-256").digest(file.readBytes())
                    val seq = store.nextSeq()
                    val fields = TicketFields(TicketFields.KIND_TRANSPORT, cfg.deviceId, seq, a.issuedAt, a.clockDelta,
                        t.id, d.id, op.id, a.latE6, a.lonE6, a.flags, cfg.version, photoHash)
                    val issued = TicketIssuer.issue(fields, UnlockedTicketSigner(unlocked, DeviceKeys.ticketPublicKey))
                    // Saved before printing: a number is never reused, even if the printer fails.
                    store.insertTicket(seq, issued.ticketId, t.id, d.id, op.id, a.issuedAt, a.flags, issued.bytes,
                        issued.qrText, issued.shortCode, file.absolutePath, photoHash.joinToString("") { "%02x".format(it) },
                        driverChangeReason)
                    seq
                }
                screen = Screen.Home
                printStored(seq)
            } catch (e: Exception) {
                message = str(R.string.error, e.message ?: e.javaClass.simpleName)
            } finally {
                busy = false
            }
        }
    }

    // ------------------------------------------------------------------ printing

    fun printStored(seq: Long) {
        val t = store.ticket(seq) ?: return
        val address = printerAddress
        if (address == null) {
            message = str(R.string.issued_not_printed) + " — " + str(R.string.no_printer)
            return
        }
        busy = true
        viewModelScope.launch {
            try {
                withContext(Dispatchers.IO) { printer.print(address, render(t)) }
                store.markPrinted(seq)
                message = str(R.string.printed)
            } catch (e: Exception) {
                message = str(R.string.print_failed, e.message ?: e.javaClass.simpleName)
            } finally {
                busy = false
                refreshTickets()
                if (selected?.seq == seq) selected = store.ticket(seq)
            }
        }
    }

    private fun render(t: StoredTicket): List<ByteArray> {
        val cfg = config
        val tr = cfg?.truck(t.truckId)
        val time = DateTimeFormatter.ofPattern("yyyy-MM-dd  HH:mm").withZone(ZoneId.systemDefault())
            .format(Instant.ofEpochSecond(t.issuedAt))
        val header = buildList {
            add(PrintLine(str(R.string.ticket_title), 26f, bold = true))
            add(PrintLine(t.ticketId, 40f, bold = true))
            add(PrintLine(str(R.string.ticket_truck, tr?.fleetNumber ?: t.truckId.toString(), tr?.plate ?: ""), 26f, bold = true))
            add(PrintLine(str(R.string.ticket_driver, cfg?.driver(t.driverId)?.name ?: t.driverId.toString()), 24f))
            add(PrintLine(time, 24f))
            add(PrintLine(str(R.string.ticket_operator, cfg?.operator(t.operatorId)?.name ?: t.operatorId.toString()), 22f))
            if (t.flags and TicketFields.FLAG_NO_GPS != 0) add(PrintLine(str(R.string.ticket_no_gps), 20f))
        }
        val footer = buildList {
            add(PrintLine(str(R.string.ticket_code, t.shortCode), 26f, bold = true))
            if (t.printCount > 0) add(PrintLine(str(R.string.copy_label, t.printCount), 30f, bold = true))
        }
        return TicketRenderer.render(header, t.qrText, footer, PrinterProfile(paper))
    }

    fun pairedPrinters(): List<PairedPrinter> = runCatching { printer.paired() }.getOrDefault(emptyList())

    fun choosePrinter(p: PairedPrinter) = store.put(K_PRINTER, p.address)

    fun choosePaper(p: PaperWidth) = store.put(K_PAPER, p.name)

    fun testPrint() {
        val address = printerAddress ?: run { message = str(R.string.no_printer); return }
        busy = true
        viewModelScope.launch {
            try {
                withContext(Dispatchers.IO) {
                    val lines = listOf(PrintLine(str(R.string.test_ticket), 26f, bold = true),
                        PrintLine("TruckGate — ${paper.mm} mm", 24f), PrintLine("ابجد هوز — ABCDEF 0123456789", 24f))
                    printer.print(address, TicketRenderer.render(lines, "TEST-${System.currentTimeMillis()}", emptyList(),
                        PrinterProfile(paper)))
                }
                message = str(R.string.printed)
            } catch (e: Exception) {
                message = str(R.string.print_failed, e.message ?: e.javaClass.simpleName)
            } finally {
                busy = false
            }
        }
    }

    // ------------------------------------------------------------------ history, reprint, void

    fun refreshTickets() {
        tickets = store.recent()
    }

    fun open(t: StoredTicket) {
        selected = t
        screen = Screen.TicketDetail
    }

    /**
     * A void is signed with the ticket key (fingerprint) over `TGVOID1|<ticket id>|<unix s>|<reason>`
     * and uploaded with sync (step 6). The server then refuses the ticket at the gate.
     */
    fun voidTicket(activity: FragmentActivity, t: StoredTicket, reason: String) {
        if (reason.isBlank()) return
        val signature = try {
            DeviceKeys.ticketSignature()
        } catch (e: KeyPermanentlyInvalidatedException) {
            message = str(R.string.key_invalidated)
            return
        }
        Biometric.unlock(activity, signature, str(R.string.void_biometric_title), t.ticketId, str(R.string.cancel),
            onUnlocked = { unlocked ->
                val at = gps.bestUnixSeconds()
                unlocked.update("TGVOID1|${t.ticketId}|$at|${reason.trim()}".toByteArray(Charsets.UTF_8))
                store.markVoided(t.seq, at, reason.trim(), EcdsaP256.derToRaw(unlocked.sign()))
                selected = store.ticket(t.seq)
                refreshTickets()
            },
            onError = { message = it })
    }

    override fun onCleared() {
        gps.stop()
        store.close()
    }

    private companion object {
        const val K_DEVICE_ID = "device_id"
        const val K_SERVER_URL = "server_url"
        const val K_SERVER_KEY = "server_key"
        const val K_CONFIG = "config_payload"
        const val K_CONFIG_SIG = "config_signature"
        const val K_LAST_SYNC = "last_sync_at"
        const val K_REVOKED = "revoked"
        const val K_PRINTER = "printer_address"
        const val K_PAPER = "paper_width"
    }
}
