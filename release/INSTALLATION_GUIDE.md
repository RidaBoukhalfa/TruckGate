# TruckGate 1.0.0 — Installation guide

Two files:

| File | Where it goes |
|---|---|
| `TruckGate-Windows-Setup.exe` | The factory PC that will be the **server** (and usually also the gate station). Optionally other PCs as extra gate stations. |
| `TruckGate-Quarry.apk` | Each quarry phone (Android 11 or newer). |

**ملخص بالعربية:** ثبّت `TruckGate-Windows-Setup.exe` على حاسوب المصنع (الخادم) واختر «Server + gate station»، ثم أنشئ حساب المدير. ثبّت `TruckGate-Quarry.apk` على هاتف المقلع، واسمح بالكاميرا والموقع والبلوتوث، ثم امسح رمز التسجيل الذي تعرضه شاشة الإدارة (هواتف المقلع ← رمز تسجيل جديد). يجب أن يكون الهاتف على شبكة Wi‑Fi المصنع نفسها. إذا تغيّر عنوان الخادم: «Change server address» في قائمة ابدأ، ثم «رمز عنوان الخادم» لكل هاتف.

---

## 0. Before you start — the network

1. **Give the server PC a fixed IP address** on the factory network (a DHCP reservation in the router is the easiest way, e.g. `192.168.1.10`). The phones are given this address when they enroll; if it changes, every phone has to be moved to the new address (section 6).
2. The quarry phones must reach the server over the **factory Wi-Fi** (same network). Nothing needs the internet, except WhatsApp/Telegram alerts.
3. Ports: the server listens on **TCP 5080** (web interface, phones). The installer opens it in the Windows firewall **for the local subnet only**. The database uses TCP 5433 on `127.0.0.1` only (not reachable from the network).

## 1. Install the server (Windows)

**Requirements:** Windows 10 version 1809 or newer, Windows 11, or Windows Server 2019/2022/2025 — **64-bit (x64)**. Administrator rights. About 1 GB for the program, plus space for the database and camera pictures (an SSD is recommended). 8 GB RAM recommended. No other software is needed: the installer contains the .NET runtime, PostgreSQL 16 and the Microsoft Visual C++ runtime.

1. Check the file (optional but recommended). In PowerShell:
   ```powershell
   Get-FileHash .\TruckGate-Windows-Setup.exe -Algorithm SHA256
   ```
   Compare with the SHA-256 in `BUILD_REPORT.md`.
2. Double-click `TruckGate-Windows-Setup.exe`. The installer is **not code-signed**, so Windows SmartScreen may show *"Windows protected your PC"*: click **More info → Run anyway**. Accept the administrator prompt.
3. Choose the language (English / Français), then the type **Server + gate station (this PC is the TruckGate server)**.
4. **Address used by the quarry phones**: the installer fills in this PC's address, e.g. `http://192.168.1.10:5080`. Check that it is the fixed address from step 0. Do not use `localhost` — phones cannot reach it.
5. Install. The last step (database and services) takes 1–3 minutes.
6. On the last page keep **Create the first administrator account now** ticked and click Finish. A black window asks for a user name, full name and a password (10 characters or more, typed twice). This works only once — for the first administrator.

What the installer sets up:

| Item | Detail |
|---|---|
| Program | `C:\Program Files\TruckGate\` (server, PostgreSQL 16 binaries, scripts) |
| Data | `C:\ProgramData\TruckGate\` — `pgdata` (database), `data` (server signing key, encrypted channel secrets, camera pictures), `config` (settings and generated database passwords), `logs`, `backups`. Readable only by administrators and the TruckGate services. |
| Services | **TruckGateDB** (PostgreSQL, account NetworkService) and **TruckGate** (server, its own account `NT SERVICE\TruckGate`), both automatic, restart on failure |
| Firewall | Inbound TCP 5080 from the local subnet |
| Backup | Scheduled task *TruckGate daily backup* at 02:30: database dump + server keys into `backups` (14 kept). Camera pictures are **not** in the backup. |
| Start menu → TruckGate | Gate Station · Change server address · Create administrator account · Back up now · Log files · Installation guide |

The database passwords are generated on this PC during installation; no password or key is inside the installer.

Silent installation (for IT staff):
```
TruckGate-Windows-Setup.exe /VERYSILENT /TYPE=server /SERVERURL=http://192.168.1.10:5080
```
Exit code 0 = success; 10 = the server part failed (see `C:\ProgramData\TruckGate\logs\install.log`).

## 2. First sign-in and site setup

1. Start menu → **TruckGate Gate Station**. It opens TruckGate in Chrome (or Edge if Chrome is not installed) in its own window, with silent receipt printing.
2. Sign in with the administrator account.
3. In the tabs (administrator):
   - **الكاميرات / Cameras**: **first create the gate** (code and name, e.g. `G1` / `Main gate`) — the gate page stays empty («اختر البوابة») until a gate exists. Then discover ONVIF cameras or enter snapshot URLs and assign them to the gate.
   - **الشاحنات والسائقون / Fleet**: trucks (number, plate) and drivers.
   - **المستخدمون / Users**: gatekeepers (PIN), quarry operators (PIN), supervisors, accountant.
   - **هواتف المقلع / Phones**: create the quarry site and draw its boundary (GPS points), then enroll the phones (section 4).
   - **الإعدادات / Settings**: trip price (1400.00 DZD), WhatsApp and SMS alerts (see `docs/OPERATIONS.md`).
4. **Receipt printer at the gate**: install the printer's Windows driver and make it the **Windows default printer**. The Gate Station shortcut prints receipts to the default printer without a dialog.
5. **Barcode scanner**: USB scanner in keyboard mode, sending Enter after the code. Click in the scan field on the gate page.

## 3. Another PC as an extra gate station (optional)

Run the same installer on that PC and choose **Gate station only (the TruckGate server is another PC)**. Enter the server address (e.g. `http://192.168.1.10:5080`); the installer tests the connection and says clearly if the server cannot be reached. Then use Start menu → **TruckGate Gate Station**.

## 4. Install the quarry phone app (Android)

**Requirements:** Android 11 or newer, camera, GPS, Bluetooth (for the ticket printer), a **screen lock with a fingerprint** registered (each ticket is signed after a fingerprint check).

1. Copy `TruckGate-Quarry.apk` to the phone (USB cable or a file-sharing app; some messaging apps rename `.apk` files).
2. Open it from the phone's file manager. Android asks to allow **Install unknown apps** for that file manager: allow it, go back, install. Google Play Protect may warn that the app is unknown: choose **Install anyway** (the app is not distributed through the Play Store).
3. Open **TruckGate**. Tap **السماح** and allow: Camera, Location (**Precise**), and on Android 12+ **Nearby devices** (Bluetooth printer).
4. **Enroll the phone** (phone on the factory Wi-Fi):
   - On the server: **هواتف المقلع / Phones → رمز تسجيل جديد / New enrollment code**, give the phone a name. A QR code is shown (valid 15 minutes, one use).
   - On the phone: **مسح رمز التسجيل**, scan the QR. The phone receives its number and configuration.
   - Phone → Settings → **اختبار الاتصال بالخادم** (test connection) should say the connection works.
5. **Ticket printer**: pair the Bluetooth thermal printer in Android Settings → Bluetooth first. Then in TruckGate → Settings choose the printer and the paper width (58 / 80 mm) and do a **test print**.

> This APK is a **debug build** (see `BUILD_REPORT.md`): to install a newer build later you will probably have to uninstall this one first, which deletes the tickets stored on the phone. **Sync the phone first** (Settings → مزامنة الآن) and check on the server that its tickets arrived.

## 5. First test — end to end

1. Quarry phone: sign in with an operator PIN → new ticket → truck and driver → fingerprint → the ticket prints.
2. Factory gate: scan the ticket with the USB scanner → the gate page shows the ticket and the camera pictures → accept → a gate receipt prints.
3. Unloading done → scan the receipt at the exit (or select the truck in "trucks inside") → exit recorded.
4. Phone → Settings → **مزامنة الآن** (sync) → the ticket appears as synced; **لوحة اليوم / Dashboard** shows the trip.

## 6. Change the server address

If the server PC got a new IP address:

1. Server PC: Start menu → **Change server address** → enter `http://NEW-IP:5080` → **Test** → **Save**. The tool refuses `localhost`, addresses that do not belong to this PC, or a server that does not answer, and says why. The service restarts.
2. Phones already enrolled: on the server open **هواتف المقلع / Phones → عرض رمز عنوان الخادم** (server address QR). On each phone: Settings → **تغيير عنوان الخادم** → scan. The phone accepts the code only if it was signed by this factory's server, checks that the new address answers, then switches. Tickets not yet synced stay on the phone and are sent to the new address.
3. Extra gate-station PCs: Start menu → **Change server address** on that PC.

## 7. Troubleshooting

| Symptom | Likely cause → what to do |
|---|---|
| Phone: «تعذّر الاتصال بالخادم» (cannot connect) | Phone not on the factory Wi-Fi, server PC off, service stopped, or firewall. From the phone's browser open `http://SERVER-IP:5080/health` — it must show `"status":"ok"`. |
| Phone: «انتهت مهلة الاتصال» (timeout) or «الهاتف لا يرى الخادم» | Wrong network or the server IP changed → section 6. |
| Phone: «ساعة الهاتف تختلف عن ساعة الخادم» | Turn on automatic date and time on the phone (and check the server PC clock). Ticket times come from the phone clock. |
| Phone stays on the permission screen | Allow all asked permissions; for location choose **Precise**. If refused twice, allow them in Android Settings → Apps → TruckGate → Permissions. |
| Installer: "The TruckGate server could not be set up" | Read the end of `C:\ProgramData\TruckGate\logs\install.log`. Common causes: port 5080 or 5433 used by another program (the log names it); antivirus blocking PowerShell or PostgreSQL. Fix and run the installer again — data is kept. |
| Web page does not open | `services.msc`: **TruckGate** and **TruckGateDB** must be *Running*. Logs: Start menu → TruckGate → Log files (`truckgate-YYYYMMDD.log`); Windows Event Viewer → Application → source *TruckGate*. |
| `/health` says `"database":"unreachable"` | Service **TruckGateDB** is stopped; start it. Its log: `C:\ProgramData\TruckGate\pgdata\log`. |
| Receipts open a print dialog | Use the **TruckGate Gate Station** shortcut (it adds silent printing) and set the receipt printer as Windows default. |
| Forgot the administrator password | Another administrator can reset it in Users. *Create administrator account* only works while no administrator exists. |

## 8. Upgrade, backup, uninstall

- **Upgrade**: run the newer `TruckGate-Windows-Setup.exe` over the installed one. Services are stopped, files replaced, database migrated, services restarted. Data, keys, settings and passwords are kept.
- **Backup**: automatic every night, or Start menu → *Back up now*. Copy `C:\ProgramData\TruckGate\backups` **and** `C:\ProgramData\TruckGate\data\snapshots` (camera pictures) to another disk regularly. A backup zip contains the server's private signing key and the database passwords: keep it as safely as the server itself.
- **Uninstall**: Windows Settings → Apps → TruckGate → Uninstall. Services, firewall rule and backup task are removed; the data in `C:\ProgramData\TruckGate` is **kept** unless you answer *Yes* to deleting it. Reinstalling later reuses the kept data.
