# Gate station — installation and daily use

The gate station is the factory PC at the entrance. It runs in a normal web browser against the
TruckGate server on the factory LAN. Nothing at the gate needs the internet.

محطة البوابة: حاسوب عادي عند مدخل المصنع، يعمل من المتصفح على شبكة المصنع المحلية، بدون إنترنت.

## 1. What is needed

| Item | Notes |
|---|---|
| Server PC | Windows or Linux, always on, on the camera LAN. 8 GB RAM, SSD for the database, a large disk for snapshots (see storage estimate in `DECISIONS.md`) |
| PostgreSQL 16 | On the server PC |
| Gate PC | Any PC with a browser (Chrome/Edge/Firefox). Can be the server PC itself |
| 2D barcode scanner | USB, **keyboard mode** (HID), configured to send **Enter** after the code. Must read QR from paper |
| Cameras | Any IP camera or NVR that supports **ONVIF** (Hikvision, Dahua, Uniview, Reolink, Axis…) or exposes a JPEG snapshot URL |
| UPS | For the server, the gate PC and the camera switch |

## 2. Install the server

```bash
# 1. Database roles and database (once). Choose two strong passwords.
psql -U postgres -v owner_pw='…' -v app_pw='…' -f deploy/db-setup.sql

# 2. Build the web UI into the server (needs Node 22)
cd web && npm ci && npm run build && cd ..

# 3. Publish the server (needs .NET 10 SDK)
cd server && dotnet publish src/TruckGate.Api -c Release -o /opt/truckgate
```

Edit `/opt/truckgate/appsettings.json` (or use environment variables):

| Setting | Value |
|---|---|
| `ConnectionStrings:App` | `Host=localhost;Database=truckgate;Username=truckgate_app;Password=…` |
| `ConnectionStrings:Owner` | `…Username=truckgate_owner;Password=…` — only when installing or upgrading; remove it afterwards |
| `Server:PublicUrl` | The address **phones** use to reach the server, e.g. `http://192.168.1.10:5080`. It is written into the enrollment QR |
| `Urls` | Default `http://0.0.0.0:5080` (whole LAN). Use `http://127.0.0.1:5080` if only this PC uses it |
| `Data:Directory` | Default `data/` next to the server. Holds the server signing key, the camera-password key and the snapshots |

```bash
# 4. First start applies the database migrations, then create the first administrator:
dotnet TruckGate.Api.dll create-admin
# 5. Run it as a service (systemd on Linux, NSSM or sc.exe on Windows)
dotnet TruckGate.Api.dll
```

**Back up `data/` and the database every day, to a disk that leaves the factory.** Losing
`data/server-signing-key.pem` means every phone must be enrolled again; losing `data/secrets.key`
means every camera password must be typed again; losing snapshots loses the evidence.

**TLS.** On a closed LAN plain HTTP is acceptable for a pilot. For production put the server behind
HTTPS (a reverse proxy such as Caddy or IIS with a certificate trusted by the gate PC). Phones verify the
server by its signing key, which is pinned in the enrollment QR, so a fake server cannot hand them a
configuration even over HTTP.

## 3. First configuration (administrator, in the browser)

1. **Users** — create the gatekeepers (PIN of 4–8 digits, not 1111 or 1234-style) and the quarry operators (PIN). Other roles
   (supervisor, accountant, manager, auditor, admin) use a password of at least 10 characters.
   Five wrong attempts lock the account for 5 minutes.
2. **Fleet** — add the drivers, then the trucks: truck ID (the number printed in the ticket), fleet
   number painted on the truck, current plate, usual driver. A plate change is kept in history.
3. **Phones** — add the quarry site and draw its boundary (one `latitude, longitude` per line, at
   least three points). Then *New enrollment code* and scan it with the quarry phone. A lost phone is
   **revoked** here; its tickets are rejected from that moment.
4. **Cameras** — see below.

## 4. Cameras: discover, choose, test

Open **Cameras**.

0. If the gate does not exist yet, create it at the bottom of the page (code + name).
1. Press **Discover**. The server sends an ONVIF probe on the LAN and lists every camera and NVR that
   answers (address, maker, model). Cameras on another subnet or with ONVIF disabled do not answer:
   enable ONVIF on the camera/NVR (Hikvision: *Network → Advanced → Integration protocol*; Dahua:
   *Network → ONVIF*) and create an ONVIF user there. You can also type the address by hand.
2. Pick one, type its **ONVIF user and password**, press **Connect**. The server lists every stream
   ("profile") — for an NVR one per channel. Press **Preview** on each to see a live picture and find
   which channel looks at what.
3. On the picture you want, choose the **role** (*rear plate*, *cargo* or *overview*) and the **gate**,
   then **Add**. A new camera starts with *capture at scan* ticked, 3 frames 300 ms apart.
4. In the camera table you then decide, per camera:
   - **Capture at scan** ✓/✗ — only ticked cameras take pictures when a ticket is scanned;
   - **Frames** (1–10) — two or three frames give a better chance of one sharp plate;
   - role, gate, enable/disable, **Test**. The health column shows the last success or error.
5. A camera without ONVIF can be added with its **snapshot URL** (the camera manual gives it, e.g.
   `http://IP/ISAPI/Streaming/channels/101/picture` on Hikvision): type it with the user and password,
   press **Preview**, then choose role and gate as above.

Camera passwords are stored encrypted (AES-256-GCM, key in `data/secrets.key`) and never appear in the
audit log or the web pages. Changing a camera's role, gate or stream raises its configuration version;
every picture records which version took it, so later training data knows exactly what the camera was.

**Placement (from the agreed design).** Rear plate: low, behind the truck as it stops, with an IR
illuminator; the front plate is unusable at night because of headlights. Cargo: on a pole, high enough to
look down into the body. Test both at night before the pilot.

## 5. At the gate (gatekeeper)

1. Log in with the username and PIN, choose the gate once.
2. The driver hands over the paper ticket. **Scan it.** The cursor is always in the scan box; no click
   is needed.
3. In the same second the selected cameras take their pictures. The screen shows:
   - **green ACCEPT**, **orange PENDING** or **red REJECT**, with the reasons in plain words
     (forged signature, already used, revoked phone, too young, too old, truck came back too soon,
     unknown truck…);
   - the fleet number in large digits and the plate written on the ticket, the driver (marked if not
     the usual one), the quarry operator and the age of the ticket;
   - the pictures just taken — click to enlarge.
4. Compare the plate in the picture and the truck in front of you with the ticket, look at the load,
   then decide with the mouse or the keys **1–5**: full, partial, empty, different truck, plate
   unreadable. **Esc** clears the screen.
5. Stamp the paper and put it in the daily envelope (it is not destroyed).

A rejected or replayed ticket is recorded with its pictures too — attempts are evidence.

What the gatekeeper cannot do: change a decision after it is saved, delete a scan or a picture, see or
change settings. A supervisor corrects mistakes by adding a review, never by editing.

## 6. Integrity checks (admin, manager, auditor)

**Integrity** recomputes the hash chain over every record and picture fingerprint. *Anchor* prints a
short code: write it down or send it outside the factory every day — if somebody with database access
rewrites history, the chain will no longer match the anchor. *Missing ticket numbers* lists gaps in each
phone's sequence (a ticket printed but never scanned at the gate).

Every picture is checked against its SHA-256 fingerprint each time it is opened; a changed or missing
file raises a critical alert.

## Not built yet

Exit scan and gate receipt, supervisor review screen, alerts by SMS/Telegram, phone sync of tickets
and voids, manual entry of the short code when a QR is damaged. See the plan in `DECISIONS.md`.
