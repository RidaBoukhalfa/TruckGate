@file:OptIn(ExperimentalMaterial3Api::class)

package dz.truckgate.quarry.ui

import androidx.activity.compose.BackHandler
import android.graphics.Bitmap
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.FilterQuality
import androidx.compose.ui.graphics.asImageBitmap
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.qrcode.QRCodeWriter
import com.google.zxing.qrcode.decoder.ErrorCorrectionLevel
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.fragment.app.FragmentActivity
import dz.truckgate.quarry.QuarryViewModel
import dz.truckgate.quarry.R
import dz.truckgate.quarry.Screen
import dz.truckgate.quarry.camera.PhotoCapture
import dz.truckgate.quarry.camera.PhotoPreview
import dz.truckgate.quarry.camera.QrScanner
import dz.truckgate.quarry.core.BlockReason
import dz.truckgate.quarry.core.PaperWidth
import dz.truckgate.quarry.core.PlateRules
import dz.truckgate.quarry.data.Operator
import dz.truckgate.quarry.data.StoredTicket
import dz.truckgate.quarry.data.SyncState
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

private val Big = 22.sp
private const val ADD_TRUCK_STEP = 10
private val time = DateTimeFormatter.ofPattern("HH:mm").withZone(ZoneId.systemDefault())

@Composable
fun QuarryApp(vm: QuarryViewModel, activity: FragmentActivity, permissionsGranted: Boolean, requestPermissions: () -> Unit) {
    MaterialTheme {
        Surface(Modifier.fillMaxSize()) {
            Box(Modifier.fillMaxSize()) {
                if (!permissionsGranted) {
                    Centered {
                        Text(stringResource(R.string.permissions_needed), fontSize = Big, textAlign = TextAlign.Center)
                        Spacer(Modifier.height(16.dp))
                        BigButton(stringResource(R.string.grant), requestPermissions)
                    }
                } else when (vm.screen) {
                    Screen.Enroll -> EnrollScreen(vm)
                    Screen.Login -> LoginScreen(vm)
                    Screen.Home -> HomeScreen(vm)
                    Screen.NewTicket -> NewTicketScreen(vm, activity)
                    Screen.History -> HistoryScreen(vm)
                    Screen.TicketDetail -> TicketDetailScreen(vm, activity)
                    Screen.Settings -> SettingsScreen(vm)
                }
                if (vm.busy) {
                    Box(Modifier.fillMaxSize().background(Color(0x66000000)), contentAlignment = Alignment.Center) {
                        CircularProgressIndicator()
                    }
                }
                vm.message?.let { text ->
                    AlertDialog(
                        onDismissRequest = { vm.message = null },
                        confirmButton = { TextButton(onClick = { vm.message = null }) { Text(stringResource(R.string.ok)) } },
                        text = { Text(text, fontSize = 18.sp) },
                    )
                }
            }
        }
    }
}

// ------------------------------------------------------------------ building blocks

@Composable
private fun Centered(content: @Composable () -> Unit) {
    Column(Modifier.fillMaxSize().padding(24.dp), verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally) { content() }
}

@Composable
private fun BigButton(text: String, onClick: () -> Unit, enabled: Boolean = true) {
    Button(onClick = onClick, enabled = enabled, modifier = Modifier.fillMaxWidth().height(72.dp)) {
        Text(text, fontSize = Big, fontWeight = FontWeight.Bold)
    }
}

@Composable
private fun SecondaryButton(text: String, onClick: () -> Unit, enabled: Boolean = true) {
    OutlinedButton(onClick = onClick, enabled = enabled, modifier = Modifier.fillMaxWidth().height(56.dp)) {
        Text(text, fontSize = 18.sp)
    }
}

@Composable
private fun Title(text: String) {
    Text(text, fontSize = 26.sp, fontWeight = FontWeight.Bold, modifier = Modifier.padding(bottom = 12.dp))
}

@Composable
private fun Page(content: @Composable ColumnScope.() -> Unit) {
    Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) { content() }
}

// ------------------------------------------------------------------ enrollment

@Composable
private fun EnrollScreen(vm: QuarryViewModel) {
    var scanning by remember { mutableStateOf(false) }
    if (scanning) {
        BackHandler { scanning = false }
        QrScanner(Modifier.fillMaxSize()) { text ->
            scanning = false
            vm.enroll(text)
        }
    } else {
        Centered {
            Title(stringResource(R.string.enroll_title))
            Text(stringResource(R.string.enroll_explain), fontSize = 18.sp, textAlign = TextAlign.Center)
            Spacer(Modifier.height(24.dp))
            BigButton(stringResource(R.string.enroll_scan), { scanning = true })
        }
    }
}

// ------------------------------------------------------------------ login

@Composable
private fun LoginScreen(vm: QuarryViewModel) {
    val cfg = vm.config
    var chosen by remember { mutableStateOf<Operator?>(null) }
    var pin by remember { mutableStateOf("") }
    Page {
        Title(stringResource(R.string.login_title))
        if (cfg == null || cfg.operators.isEmpty()) {
            Text(stringResource(R.string.config_missing), fontSize = 18.sp)
            SecondaryButton(stringResource(R.string.sync_now), vm::syncNow)
            return@Page
        }
        val op = chosen
        if (op == null) {
            LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                items(cfg.operators) { o -> SecondaryButton(o.name, { chosen = o; pin = "" }) }
            }
        } else {
            Text(op.name, fontSize = Big, fontWeight = FontWeight.Bold)
            OutlinedTextField(
                value = pin,
                onValueChange = { v -> if (v.length <= 8 && v.all(Char::isDigit)) pin = v },
                label = { Text(stringResource(R.string.login_pin)) },
                visualTransformation = PasswordVisualTransformation(),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            BigButton(stringResource(R.string.ok), { vm.login(op, pin); pin = "" }, enabled = pin.length >= 4)
            SecondaryButton(stringResource(R.string.back), { chosen = null })
        }
    }
}

// ------------------------------------------------------------------ home

@Composable
private fun HomeScreen(vm: QuarryViewModel) {
    val cfg = vm.config
    val fix = vm.gps.latestFix()
    Page {
        Text(vm.operator?.name.orEmpty(), fontSize = Big, fontWeight = FontWeight.Bold)
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            StatusDot(vm.printerAddress != null, stringResource(R.string.status_printer))
            StatusDot(fix != null && fix.ageMs < 120_000, stringResource(R.string.gps_ok))
            StatusDot(cfg != null, stringResource(R.string.status_config, cfg?.version ?: 0))
            StatusDot(vm.unsynced == 0 && vm.syncError == null, stringResource(R.string.status_sync, vm.unsynced))
        }
        Text(stringResource(R.string.last_sync, vm.lastSyncAt?.let {
            java.time.format.DateTimeFormatter.ofPattern("dd/MM HH:mm").withZone(java.time.ZoneId.systemDefault())
                .format(java.time.Instant.ofEpochSecond(it))
        } ?: "—"), fontSize = 14.sp)
        vm.syncOverdueHours?.let { hours ->
            Text(stringResource(R.string.sync_overdue_warning, hours.toInt()), color = Color(0xFFB45309), fontSize = 16.sp,
                fontWeight = FontWeight.Bold)
        }
        Spacer(Modifier.height(12.dp))
        BigButton(stringResource(R.string.home_new_ticket), vm::startTicket, enabled = cfg != null)
        Text(stringResource(R.string.home_today, vm.todayCount), fontSize = 18.sp)
        vm.tickets.firstOrNull()?.let { Text(stringResource(R.string.home_last, it.ticketId), fontSize = 16.sp) }
        Spacer(Modifier.weight(1f))
        SecondaryButton(stringResource(R.string.home_history), { vm.go(Screen.History) })
        SecondaryButton(stringResource(R.string.sync_now), vm::syncNow)
        SecondaryButton(stringResource(R.string.home_settings), { vm.go(Screen.Settings) })
        SecondaryButton(stringResource(R.string.home_logout), vm::logout)
    }
}

@Composable
private fun StatusDot(ok: Boolean, label: String) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(14.dp).background(if (ok) Color(0xFF2E7D32) else Color(0xFFC62828)))
        Text(" $label", fontSize = 14.sp)
    }
}

// ------------------------------------------------------------------ new ticket (4 steps)

@Composable
private fun NewTicketScreen(vm: QuarryViewModel, activity: FragmentActivity) {
    val cfg = vm.config ?: return
    BackHandler {
        when (vm.step) {
            0 -> vm.go(Screen.Home)
            ADD_TRUCK_STEP -> vm.step = 0
            else -> vm.step -= 1
        }
    }
    when (vm.step) {
        0 -> Page {
            Title(stringResource(R.string.choose_truck))
            var search by remember { mutableStateOf("") }
            OutlinedTextField(value = search, onValueChange = { search = it.take(20) }, singleLine = true,
                label = { Text(stringResource(R.string.search_truck)) }, modifier = Modifier.fillMaxWidth())
            val key = PlateRules.key(search)
            val trucks = vm.availableTrucks().filter { t ->
                search.isBlank() || t.fleetNumber.contains(search.trim(), ignoreCase = true) ||
                    (key.length >= 2 && PlateRules.key(t.plate).contains(key))
            }
            LazyVerticalGrid(columns = GridCells.Adaptive(110.dp), verticalArrangement = Arrangement.spacedBy(8.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.weight(1f)) {
                items(trucks) { t ->
                    val wait = vm.waitMinutes(t)
                    val enabled = t.active && wait == 0
                    Card(
                        modifier = Modifier.height(110.dp).clickable(enabled = enabled) { vm.chooseTruck(t) },
                        colors = CardDefaults.cardColors(
                            containerColor = when {
                                !enabled -> Color.LightGray
                                t.pending -> Color(0xFFFFF4D6)
                                else -> MaterialTheme.colorScheme.primaryContainer
                            }),
                    ) {
                        Column(Modifier.fillMaxSize().padding(6.dp), verticalArrangement = Arrangement.Center,
                            horizontalAlignment = Alignment.CenterHorizontally) {
                            Text(t.fleetNumber, fontSize = if (t.pending) 20.sp else 32.sp, fontWeight = FontWeight.Bold)
                            Text(t.plate.orEmpty(), fontSize = 12.sp)
                            if (t.pending) Text(stringResource(R.string.truck_pending_badge), fontSize = 11.sp, color = Color(0xFFB45309))
                            if (wait > 0) Text(stringResource(R.string.truck_wait, wait), fontSize = 14.sp)
                        }
                    }
                }
            }
            SecondaryButton(stringResource(R.string.add_truck), { vm.step = ADD_TRUCK_STEP })
        }

        ADD_TRUCK_STEP -> Page {
            Title(stringResource(R.string.add_truck_title))
            Text(stringResource(R.string.add_truck_explain), fontSize = 16.sp)
            var plate by remember { mutableStateOf("") }
            OutlinedTextField(
                value = plate,
                onValueChange = { v -> if (v.length <= 20) plate = v },
                label = { Text(stringResource(R.string.plate_label)) },
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Phone),
                singleLine = true,
                textStyle = androidx.compose.ui.text.TextStyle(fontSize = 26.sp, fontWeight = FontWeight.Bold),
                modifier = Modifier.fillMaxWidth(),
            )
            val normalized = PlateRules.normalize(plate)
            Text(normalized ?: stringResource(R.string.plate_invalid), fontSize = 18.sp,
                color = if (normalized == null) Color(0xFFC62828) else Color(0xFF2E7D32))
            Spacer(Modifier.weight(1f))
            BigButton(stringResource(R.string.add_truck_confirm), { vm.confirmAddTruck(plate) }, enabled = normalized != null)
            SecondaryButton(stringResource(R.string.back), { vm.step = 0 })
        }

        1 -> Page {
            Title(stringResource(R.string.choose_driver))
            LazyColumn(Modifier.weight(1f)) {
                items(cfg.drivers) { d ->
                    Row(Modifier.fillMaxWidth().clickable { vm.chooseDriver(d) }.padding(8.dp),
                        verticalAlignment = Alignment.CenterVertically) {
                        RadioButton(selected = vm.driver?.id == d.id, onClick = { vm.chooseDriver(d) })
                        Text(d.name + if (d.id == vm.truck?.defaultDriverId) "  ★" else "", fontSize = 20.sp)
                    }
                }
            }
            if (vm.driverChanged) {
                Text(stringResource(R.string.driver_reason), fontSize = 18.sp)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    listOf(R.string.reason_shift, R.string.reason_absence, R.string.reason_other).forEach { id ->
                        val label = stringResource(id)
                        FilterChip(selected = vm.driverChangeReason == label, onClick = { vm.driverChangeReason = label },
                            label = { Text(label) })
                    }
                }
            }
            BigButton(stringResource(R.string.next), { vm.step = 2 },
                enabled = vm.driver != null && (!vm.driverChanged || vm.driverChangeReason != null))
        }

        2 -> {
            val capture = remember { PhotoCapture() }
            val context = LocalContext.current
            Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Title(stringResource(R.string.take_photo))
                PhotoPreview(capture, Modifier.fillMaxWidth().weight(1f))
                BigButton(stringResource(if (vm.photo == null) R.string.capture else R.string.retake), {
                    capture.take(context, onSaved = { vm.photo = it }, onError = { vm.message = it })
                })
                BigButton(stringResource(R.string.next), { vm.noPhoto = false; vm.step = 3 }, enabled = vm.photo != null)
                if (!vm.photoRequired) {
                    SecondaryButton(stringResource(R.string.skip_photo), { vm.photo = null; vm.noPhoto = true; vm.step = 3 })
                }
            }
        }

        else -> Page {
            Title(stringResource(R.string.review_title))
            Text(vm.truck?.fleetNumber.orEmpty() + "  —  " + vm.truck?.plate.orEmpty(), fontSize = 30.sp, fontWeight = FontWeight.Bold)
            Text(vm.driver?.name.orEmpty(), fontSize = Big)
            vm.driverChangeReason?.let { Text(it, fontSize = 16.sp) }
            Text(vm.operator?.name.orEmpty(), fontSize = 18.sp)
            Text(stringResource(if (vm.photo != null) R.string.review_photo_yes else R.string.review_photo_no), fontSize = 16.sp)
            if (vm.truck?.pending == true) Text(stringResource(R.string.truck_pending_badge), color = Color(0xFFB45309), fontSize = 16.sp)
            if (vm.blocked.isNotEmpty()) {
                HorizontalDivider()
                Text(stringResource(R.string.blocked_title), color = Color(0xFFC62828), fontSize = 20.sp, fontWeight = FontWeight.Bold)
                vm.blocked.forEach { Text("• " + blockText(it), color = Color(0xFFC62828), fontSize = 18.sp) }
            }
            Spacer(Modifier.weight(1f))
            BigButton(stringResource(R.string.sign_and_print), { vm.signAndPrint(activity) })
            SecondaryButton(stringResource(R.string.cancel), { vm.go(Screen.Home) })
        }
    }
}

@Composable
private fun blockText(r: BlockReason): String = when (r) {
    BlockReason.DeviceRevoked -> stringResource(R.string.block_revoked)
    BlockReason.TruckInactive -> stringResource(R.string.block_truck_inactive)
    is BlockReason.TruckCooldown -> stringResource(R.string.block_cooldown, ((r.remainingS + 59) / 60).toInt())
    BlockReason.OutsideGeofence -> stringResource(R.string.block_geofence)
    is BlockReason.SyncOverdue -> stringResource(R.string.block_sync, r.hoursSinceSync.toInt())
    is BlockReason.TooManyUnsynced -> stringResource(R.string.block_unsynced, r.count)
}

// ------------------------------------------------------------------ history and detail

@Composable
private fun HistoryScreen(vm: QuarryViewModel) {
    BackHandler { vm.go(Screen.Home) }
    Page {
        Title(stringResource(R.string.history_title))
        LazyColumn(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            items(vm.tickets) { t -> TicketRow(vm, t) }
        }
        SecondaryButton(stringResource(R.string.back), { vm.go(Screen.Home) })
    }
}

@Composable
private fun TicketRow(vm: QuarryViewModel, t: StoredTicket) {
    val truck = vm.config?.truck(t.truckId)
    Card(Modifier.fillMaxWidth().clickable { vm.open(t) }) {
        Row(Modifier.padding(12.dp), horizontalArrangement = Arrangement.SpaceBetween) {
            Text("${t.ticketId}   ${truck?.fleetNumber ?: t.truckId}   ${time.format(Instant.ofEpochSecond(t.issuedAt))}",
                fontSize = 17.sp, modifier = Modifier.weight(1f))
            when {
                t.voidedAt != null -> Text(stringResource(R.string.voided), color = Color(0xFFC62828))
                t.printCount == 0 -> Text("⚠", color = Color(0xFFC62828))
                else -> Text("✓", color = Color(0xFF2E7D32))
            }
        }
        Text(syncText(t), fontSize = 13.sp, modifier = Modifier.padding(start = 12.dp, end = 12.dp, bottom = 8.dp),
            color = when (t.syncState) {
                SyncState.SYNCED -> Color(0xFF2E7D32)
                SyncState.FAILED -> Color(0xFFC62828)
                else -> Color(0xFFB45309)
            })
    }
}

/** Never says "synced" before the server confirmed it. */
@Composable
private fun syncText(t: StoredTicket): String = when (t.syncState) {
    SyncState.LOCAL -> stringResource(R.string.sync_state_local)
    SyncState.CHANGE_PENDING -> stringResource(R.string.sync_state_change)
    SyncState.SYNCED -> stringResource(R.string.sync_state_synced)
    SyncState.FAILED -> stringResource(R.string.sync_state_failed, t.syncError.orEmpty())
}

@Composable
private fun TicketQr(text: String) {
    val image = remember(text) {
        val m = QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0,
            mapOf(EncodeHintType.MARGIN to 2, EncodeHintType.ERROR_CORRECTION to ErrorCorrectionLevel.M))
        val pixels = IntArray(m.width * m.height) { i -> if (m.get(i % m.width, i / m.width)) 0xFF000000.toInt() else 0xFFFFFFFF.toInt() }
        Bitmap.createBitmap(pixels, m.width, m.height, Bitmap.Config.ARGB_8888).asImageBitmap()
    }
    Image(image, contentDescription = null, filterQuality = FilterQuality.None,
        modifier = Modifier.size(180.dp).background(Color.White))
}

@Composable
private fun TicketDetailScreen(vm: QuarryViewModel, activity: FragmentActivity) {
    val t = vm.selected ?: return
    var reason by remember { mutableStateOf("") }
    BackHandler { vm.go(Screen.History) }
    Page {
        Title(t.ticketId)
        Text(t.shortCode, fontSize = Big)
        Text(time.format(Instant.ofEpochSecond(t.issuedAt)), fontSize = 18.sp)
        if (t.voidedAt != null) {
            Text(stringResource(R.string.voided) + ": " + t.voidReason.orEmpty(), color = Color(0xFFC62828), fontSize = 20.sp)
        } else {
            // Same code as on the paper: the gate scanner can read it from this screen if the
            // printer is broken. A ticket is still accepted only once at the gate.
            TicketQr(t.qrText)
            Text(stringResource(R.string.ticket_qr_screen), fontSize = 14.sp)
            BigButton(stringResource(if (t.printCount == 0) R.string.retry else R.string.reprint), { vm.printStored(t.seq) })
            HorizontalDivider()
            OutlinedTextField(value = reason, onValueChange = { reason = it.take(120) },
                label = { Text(stringResource(R.string.void_reason)) }, modifier = Modifier.fillMaxWidth())
            SecondaryButton(stringResource(R.string.void_confirm), { vm.voidTicket(activity, t, reason) },
                enabled = reason.isNotBlank())
        }
        Spacer(Modifier.weight(1f))
        SecondaryButton(stringResource(R.string.back), { vm.go(Screen.History) })
    }
}

// ------------------------------------------------------------------ settings

@Composable
private fun SettingsScreen(vm: QuarryViewModel) {
    val printers = remember { vm.pairedPrinters() }
    var chosen by remember { mutableStateOf(vm.printerAddress) }
    var paper by remember { mutableStateOf(vm.paper) }
    var scanningAddress by remember { mutableStateOf(false) }
    if (scanningAddress) {
        BackHandler { scanningAddress = false }
        QrScanner(Modifier.fillMaxSize()) { text ->
            scanningAddress = false
            vm.changeServerAddress(text)
        }
        return
    }
    BackHandler { vm.go(Screen.Home) }
    Page {
        Title(stringResource(R.string.settings_title))
        Text(stringResource(R.string.device_info, vm.deviceId ?: 0, vm.serverUrl.orEmpty()), fontSize = 14.sp)
        Text(stringResource(R.string.printer_choose), fontSize = 18.sp)
        LazyColumn(Modifier.weight(1f)) {
            items(printers) { p ->
                Row(Modifier.fillMaxWidth().clickable { vm.choosePrinter(p); chosen = p.address }.padding(6.dp),
                    verticalAlignment = Alignment.CenterVertically) {
                    RadioButton(selected = chosen == p.address, onClick = { vm.choosePrinter(p); chosen = p.address })
                    Text("${p.name}  (${p.address})", fontSize = 16.sp)
                }
            }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilterChip(selected = paper == PaperWidth.MM58, onClick = { vm.choosePaper(PaperWidth.MM58); paper = PaperWidth.MM58 },
                label = { Text(stringResource(R.string.paper_58)) })
            FilterChip(selected = paper == PaperWidth.MM80, onClick = { vm.choosePaper(PaperWidth.MM80); paper = PaperWidth.MM80 },
                label = { Text(stringResource(R.string.paper_80)) })
        }
        SecondaryButton(stringResource(R.string.test_print), vm::testPrint, enabled = chosen != null)
        SecondaryButton(stringResource(R.string.sync_now), vm::syncNow)
        SecondaryButton(stringResource(R.string.test_connection), vm::testConnection)
        SecondaryButton(stringResource(R.string.change_server_address), { scanningAddress = true })
        SecondaryButton(stringResource(R.string.back), { vm.go(Screen.Home) })
    }
}
