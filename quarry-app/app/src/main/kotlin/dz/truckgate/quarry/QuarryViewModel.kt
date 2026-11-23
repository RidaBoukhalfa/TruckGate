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
import dz.truckgate.quarry.core.ServerAddress
import dz.truckgate.quarry.data.DeviceConfig
import dz.truckgate.quarry.data.Driver
import dz.truckgate.quarry.data.Operator
import dz.truckgate.quarry.data.Keys.K_CONFIG
import dz.truckgate.quarry.data.Keys.K_DEVICE_ID
import dz.truckgate.quarry.data.Keys.K_LAST_SYNC
import dz.truckgate.quarry.data.Keys.K_LAST_SYNC_ERROR
import dz.truckgate.quarry.data.Keys.K_ADDRESS_ISSUED_AT
import dz.truckgate.quarry.data.Keys.K_PAPER
import dz.truckgate.quarry.data.Keys.K_PRINTER
import dz.truckgate.quarry.data.Keys.K_REVOKED
import dz.truckgate.quarry.data.Keys.K_SERVER_KEY
import dz.truckgate.quarry.data.Keys.K_SERVER_URL
import dz.truckgate.quarry.data.Store
import dz.truckgate.quarry.data.StoredTicket
import dz.truckgate.quarry.data.Truck
import dz.truckgate.quarry.gps.GpsTracker
import dz.truckgate.quarry.net.NetErrors
import dz.truckgate.quarry.net.ServerApi
import dz.truckgate.quarry.print.BluetoothPrinter
import dz.truckgate.quarry.print.PairedPrinter
import dz.truckgate.quarry.print.PrintLine
import dz.truckgate.quarry.print.TicketRenderer
import dz.truckgate.quarry.security.Biometric
import dz.truckgate.quarry.security.DeviceKeys
import dz.truckgate.quarry.security.UnlockedTicketSigner
import dz.truckgate.quarry.sync.SyncEngine
import dz.truckgate.quarry.sync.SyncWorker
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
    private val store = Store.get(app)
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
    var unsynced by mutableStateOf(0)
        private set
    var lastSyncAt by mutableStateOf<Long?>(null)
        private set
    var syncError by mutableStateOf<String?>(null)
        private set

    private var wrongPins = 0
    private var lockedUntilMs = 0L

    init {
        config = store.get(K_CONFIG)?.let { runCatching { DeviceConfig.parse(it) }.getOrNull() }
        screen = if (store.get(K_DEVICE_ID) != null && DeviceKeys.exist()) Screen.Login else Screen.Enroll
        if (screen == Screen.Login) SyncWorker.schedule(app)
        refreshTickets()
    }

    private fun str(id: Int, vararg args: Any): String = getApplication<Application>().getString(id, *args)

    val deviceId: Int? get() = store.get(K_DEVICE_ID)?.toIntOrNull()
    var serverUrl by mutableStateOf(store.get(K_SERVER_URL))
        private set
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
        val invite = runCatching { EnrollmentInvite.parse(qrText) }.getOrElse {
            val text = qrText.trim()
            message = when {
                text.startsWith("TGA1|") -> str(R.string.enroll_scanned_address_code)
                text.startsWith("TGE1|") -> str(R.string.enroll_code_damaged)
                else -> str(R.string.enroll_not_a_code, text.take(24))
            }
            return
        }
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
                    SyncEngine(getApplication<Application>()).fetchConfig(ServerApi(invite.serverUrl), enrolled.deviceId, enrolled.serverTime)
                }
                SyncWorker.schedule(getApplication<Application>())
                serverUrl = invite.serverUrl
                config = cfg
                screen = Screen.Login
            } catch (e: Exception) {
                message = NetErrors.describe(getApplication<Application>(), invite.serverUrl, e)
            } finally {
                busy = false
            }
        }
    }

    /** Configuration down, tickets and voids up, photos too (the operator asked for it). */
    fun syncNow() {
        busy = true
        viewModelScope.launch {
            try {
                val r = withContext(Dispatchers.IO) { SyncEngine(getApplication<Application>()).run(includePhotos = true) }
                config = r.config
                message = str(R.string.sync_done, r.sent, r.photos, r.config?.version ?: 0) +
                    (if (r.rejected > 0) "\n" + str(R.string.sync_rejected, r.rejected) else "") +
                    (if (r.photosRefused > 0) "\n" + str(R.string.sync_photos_refused, r.photosRefused) else "")
            } catch (e: Exception) {
                message = NetErrors.describe(getApplication<Application>(), serverUrl.orEmpty(), e)
            } finally {
                busy = false
                refreshTickets()
            }
        }
    }

    /** Settings → "test connection": reaches the server and compares the clocks. */
    fun testConnection() {
        val url = serverUrl ?: return
        busy = true
        viewModelScope.launch {
            try {
                val serverTime = withContext(Dispatchers.IO) { ServerApi(url).serverTime() }
                val skew = kotlin.math.abs(System.currentTimeMillis() / 1000 - serverTime)
                message = str(R.string.connection_ok, url) +
                    if (skew > 300) "\n" + str(R.string.clock_skew, skew / 60) else ""
            } catch (e: Exception) {
                message = NetErrors.describe(getApplication<Application>(), url, e)
            } finally {
                busy = false
            }
        }
    }

    /**
     * Settings → "change server address": accepts only a code signed by this factory's server
     * (key received at enrollment) and newer than the last one accepted, and only if the new
     * address answers. Unsynced tickets stay on the phone and go to the new address.
     */
    fun changeServerAddress(qrText: String) {
        val key = store.get(K_SERVER_KEY)?.let { Base64.getDecoder().decode(it) } ?: return
        val address = try {
            ServerAddress.parseAndVerify(qrText, key)
        } catch (e: SecurityException) {
            message = str(R.string.address_wrong_server); return
        } catch (e: IllegalArgumentException) {
            message = str(R.string.address_not_a_code); return
        }
        if (address.issuedAt <= (store.get(K_ADDRESS_ISSUED_AT)?.toLongOrNull() ?: 0L)) {
            message = str(R.string.address_old_code); return
        }
        busy = true
        viewModelScope.launch {
            try {
                withContext(Dispatchers.IO) { ServerApi(address.serverUrl).serverTime() }
                store.putAll(mapOf(K_SERVER_URL to address.serverUrl, K_ADDRESS_ISSUED_AT to address.issuedAt.toString()))
                serverUrl = address.serverUrl
                SyncWorker.schedule(getApplication<Application>())
                message = str(R.string.address_changed, address.serverUrl)
            } catch (e: Exception) {
                message = NetErrors.describe(getApplication<Application>(), address.serverUrl, e) + "\n" + str(R.string.address_not_changed)
            } finally {
                busy = false
            }
        }
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
                unsyncedCount = store.unsyncedCount(),
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
                SyncWorker.kick(getApplication<Application>())
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
                SyncWorker.kick(getApplication<Application>())
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
        unsynced = store.unsyncedCount()
        lastSyncAt = store.get(K_LAST_SYNC)?.toLongOrNull()
        syncError = store.get(K_LAST_SYNC_ERROR)?.takeIf { it.isNotBlank() }
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
                SyncWorker.kick(getApplication<Application>())
                selected = store.ticket(t.seq)
                refreshTickets()
            },
            onError = { message = it })
    }

    override fun onCleared() {
        gps.stop()
    }
}
