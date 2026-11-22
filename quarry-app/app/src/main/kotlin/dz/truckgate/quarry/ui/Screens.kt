@file:OptIn(ExperimentalMaterial3Api::class)

package dz.truckgate.quarry.ui

import androidx.activity.compose.BackHandler
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
import dz.truckgate.quarry.data.Operator
import dz.truckgate.quarry.data.StoredTicket
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

private val Big = 22.sp
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
            SecondaryButton(stringResource(R.string.config_refresh), vm::refreshConfig)
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
        }
        Spacer(Modifier.height(12.dp))
        BigButton(stringResource(R.string.home_new_ticket), vm::startTicket, enabled = cfg != null)
        Text(stringResource(R.string.home_today, vm.todayCount), fontSize = 18.sp)
        vm.tickets.firstOrNull()?.let { Text(stringResource(R.string.home_last, it.ticketId), fontSize = 16.sp) }
        Spacer(Modifier.weight(1f))
        SecondaryButton(stringResource(R.string.home_history), { vm.go(Screen.History) })
        SecondaryButton(stringResource(R.string.config_refresh), vm::refreshConfig)
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
        if (vm.step == 0) {
            vm.go(Screen.Home)
        } else {
            vm.step -= 1
        }
    }
    when (vm.step) {
        0 -> Page {
            Title(stringResource(R.string.choose_truck))
            LazyVerticalGrid(columns = GridCells.Adaptive(110.dp), verticalArrangement = Arrangement.spacedBy(8.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                items(cfg.trucks) { t ->
                    val wait = vm.waitMinutes(t)
                    val enabled = t.active && wait == 0
                    Card(
                        modifier = Modifier.height(110.dp).clickable(enabled = enabled) { vm.chooseTruck(t) },
                        colors = CardDefaults.cardColors(
                            containerColor = if (enabled) MaterialTheme.colorScheme.primaryContainer else Color.LightGray),
                    ) {
                        Column(Modifier.fillMaxSize().padding(6.dp), verticalArrangement = Arrangement.Center,
                            horizontalAlignment = Alignment.CenterHorizontally) {
                            Text(t.fleetNumber, fontSize = 32.sp, fontWeight = FontWeight.Bold)
                            Text(t.plate.orEmpty(), fontSize = 12.sp)
                            if (wait > 0) Text(stringResource(R.string.truck_wait, wait), fontSize = 14.sp)
                        }
                    }
                }
            }
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
                BigButton(stringResource(R.string.next), { vm.step = 3 }, enabled = vm.photo != null)
            }
        }

        else -> Page {
            Title(stringResource(R.string.review_title))
            Text(vm.truck?.fleetNumber.orEmpty() + "  —  " + vm.truck?.plate.orEmpty(), fontSize = 30.sp, fontWeight = FontWeight.Bold)
            Text(vm.driver?.name.orEmpty(), fontSize = Big)
            vm.driverChangeReason?.let { Text(it, fontSize = 16.sp) }
            Text(vm.operator?.name.orEmpty(), fontSize = 18.sp)
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
    }
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
    BackHandler { vm.go(Screen.Home) }
    val printers = remember { vm.pairedPrinters() }
    var chosen by remember { mutableStateOf(vm.printerAddress) }
    var paper by remember { mutableStateOf(vm.paper) }
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
        SecondaryButton(stringResource(R.string.config_refresh), vm::refreshConfig)
        SecondaryButton(stringResource(R.string.back), { vm.go(Screen.Home) })
    }
}
