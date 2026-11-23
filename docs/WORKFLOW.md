# TruckGate 1.1 — Workflow, cameras, payments and training data

**ملخص بالعربية:** عامل المقلع يصدر الوصل من الهاتف (حتى بدون شبكة)، ويمكنه تسجيل شاحنة جديدة غير موجودة؛ تظهر عند المشرف في «الشاحنات بانتظار الاعتماد» فيكمل الحقول الخمسة (رقم الشاحنة، اللقب، الاسم، الماطريكول، سعر الرحلة الواحدة) ويعتمدها أو يرفضها بسبب أو يدمجها مع شاحنة موجودة. عند البوابة يُمسح الوصل فتظهر إحدى ست حالات (صالح، للمراجعة، شاحنة جديدة، مرفوض، مستعمل، غير معروف) وتحفظ الكاميرات فيديو 30 ثانية قبل المسح و30 ثانية بعده. كل وصل مستعمل يبقى في «الوصلات المحروقة» بتاريخه الكامل. المستحقات تُحسب لكل شاحنة: الرحلات المستحقة × سعر الشاحنة المعتمد، والتصحيحات تُسجَّل كتسويات ولا تُعدّل أي دفعة سابقة. تصنيفات تدريب الذكاء الاصطناعي يضعها المشرف فقط؛ لا يوجد أي تحليل آلي للصور في هذا الإصدار.

---

## 1. Roles

| Role | Where | What they do |
|---|---|---|
| Quarry operator | Android app (PIN + fingerprint) | Issues signed tickets offline; registers a truck that is not in the list; syncs. |
| Gatekeeper | Gate page | Scans entry tickets and exit receipts; decides the load; types the plate he reads for a new truck. |
| Supervisor | Web | Approves / rejects / merges new trucks and sets prices; reviews trips; investigates; labels trips; cameras; operating settings; alerts. |
| Accountant | Web | Per-truck statement, adjustments, payment batches, CSV and print/PDF. Read-only on trips. |
| Manager / auditor | Web | Dashboard, burned receipts, trip details (manager also alerts), audit chain. |
| Administrator | Web | Everything, plus users, phones, alert channels, sign-in rules, time zone. |

Settings are split: supervisors change quarry, gate, video, storage, review and payment rules; alert channels (`alerts.*`), sign-in rules (`auth.*`) and the time zone stay with the administrator (checked on the server, not only hidden in the page).

## 2. A truck that is not in the list (quarry → supervisor)

1. On the phone, *new ticket → truck not in the list → add a truck*: the operator types the registration number (6–12 digits; Arabic digits, spaces and dashes accepted). The phone refuses a plate already in its list (same digits, any formatting) and shows which truck it is.
2. The phone takes the lowest free number from the block of numbers reserved for it (40000–65535, ten at a time, given in the signed configuration). This works offline; two phones can never create the same number.
3. The ticket is issued at once; the truck is **pending approval** on the phone (marked as such on the printed receipt).
4. At the next sync the truck reaches the server first (before its tickets). The server stores it as `PENDING_APPROVAL`, points out a possible duplicate (same plate digits), and raises the alert **TRUCK_REGISTERED** — «تمت إضافة شاحنة جديدة بواسطة عامل المقلع» (web notification centre, and SMS / WhatsApp / Telegram if those channels are on for that severity).
5. Supervisor → *Trucks → pending approval*: completes **truck number, last name, first name, registration number, price per trip** and approves; or rejects with a reason; or merges into an existing approved truck. Every decision is in the audit chain.
6. On approval or merge, reviews whose only reason was "unknown / pending truck" are closed automatically; other reasons stay open. Trips of a merged truck count for the target truck.
7. Phones receive the approved list at their next configuration refresh (after each sync).

A pending truck can enter the factory: the gate shows **NEW TRUCK** (`TRUCK_PENDING_APPROVAL`). If its data has not reached the server yet (phone not synced) the number is still recognised as reserved for that phone; the gatekeeper must type the plate he reads before letting it in, and that observed plate is stored with the decision.

**Payable only when approved:** a trip is paid only if its truck (after merge) is `APPROVED`, has a price, the trip is closed, the exit was recorded (setting `pay.require_exit_scan`) and no review is open.

## 3. Ticket issue at the quarry

- Setting `quarry.photo_required` (default on): with it, no ticket without a cargo photo. Off: the operator may skip the photo; the ticket then carries an all-zero photo fingerprint, which the gate and the trip page show as "issued without photo" (never as "photo missing").
- One ticket per press: the button is blocked while a ticket is being signed and printed; the truck cool-down is checked again just before signing.
- Printed receipt: factory name (`site.factory_name`), ticket number and short code, QR, truck number and plate, driver, operator, date and time, photo status, truck status (pending / approved).
- Per-ticket sync state on the phone: *saved on the phone* → *waiting* → *synced* (only after the server confirmed it) or *failed* (with the reason). Nothing is shown as synced before the server answers.
- No sync for 24 h: the supervisor gets **SYNC_OVERDUE**, the phone shows a banner, and with `quarry.sync_overdue_policy = BLOCK` (default) it stops issuing new tickets until it syncs (`WARN`: warning only).

## 4. Gate screen states

| State | Meaning | Gate action |
|---|---|---|
| **VALID** (green) | Genuine ticket, first use, all checks passed | Decide the load (1–5) |
| **PENDING REVIEW** (orange) | Genuine but something needs the supervisor (too early, driver changed, clock…) | Decide; the trip goes to review |
| **NEW TRUCK** (amber, striped) | Truck registered on a quarry phone, not approved yet | Type the plate if asked, decide; supervisor approves later |
| **REJECTED** (red) | Not acceptable (revoked phone, voided ticket, too soon after the last entry…) | No entry |
| **ALREADY USED** (dark red) | This ticket / receipt was used before (one trip per ticket, enforced by the database) | No entry; critical alert |
| **UNKNOWN RECEIPT** (grey) | Not signed by an enrolled phone or by this gate (forged, damaged, foreign) | No entry; critical alert |

Shown with it: truck number, plate, truck status, driver (usual or not), operator, ticket issue time and age, the quarry cargo photo (or "not received yet" / "issued without photo"), previous passages of the same truck, camera pictures, and the live state of each passage video (recording / recorded / failed).

## 5. Passage video (30 s before + 30 s after)

How it works (implemented in `VideoService` / `CameraRecorder`):

- For every active camera with *record video* and an RTSP address, the server runs one `ffmpeg` process that copies the camera stream (no re-encoding) into 2-second MPEG-TS segments in `data\video-buffer\cam<ID>`. Only the last few minutes are kept.
- On each scan (entry or exit) one recording row per camera is created with the window `[scan − video.pre_s, scan + video.post_s]` (30 s / 30 s by default).
- When the window has passed, the segments covering it are joined (stream copy) into one MP4 under
  `data\snapshots\media\YYYY\MM\DD\trips\T-xxxxxx\entry_camNN_v<id>.mp4` (or `exit_…`) with a JSON description next to it (receipt id, trip number, camera, operation, window, real media start/end, fingerprint). Its SHA-256 is stored in the database; the file is checked against it every time it is played or exported.
- Status per recording: `RECORDING` → `OK`, `PARTIAL` (stream gap: the clip exists but does not cover the whole window) or `FAILED` (no footage; the reason is stored). `FAILED` raises **RECORDING_FAILED** and, with `video.failure_opens_review`, a review reason `VIDEO_MISSING_ENTRY/EXIT`. A camera that stops sending raises **RECORDER_OFFLINE**. A failed recording is never shown as recorded.
- Two trucks scanned close together get separate files (one row per scan and camera, unique in the database).
- Retention: after `video.retention_days` (30) files are deleted and the row becomes `PURGED` (fingerprint and description kept); with `video.keep_reviewed`, videos of trips that had a review are kept. **STORAGE_LOW** when free space falls under `storage.min_free_gb`.
- Video is played in the trip page (supervisor, administrator, manager, auditor) through the server only, after the fingerprint check; cameras are never exposed to the browser.

### Camera hardware and software requirements

| Item | Requirement |
|---|---|
| Camera / recorder | IP camera or NVR channel giving an **RTSP H.264** stream (H.265 is copied as-is but many browsers cannot play it: use H.264). ONVIF Profile S recommended (discovery, stream and snapshot addresses). |
| Stream to record | The **sub-stream** (e.g. 640×360 or 704×576, 10–15 fps, 512–1024 kbit/s, I-frame interval ≤ 2 s). The main stream works but needs much more disk. |
| Snapshot | HTTP JPEG snapshot address (used for the scan pictures, unchanged since 1.0). |
| Network | Wired Ethernet from cameras to the server PC (same LAN, no internet). RTSP over TCP (port 554 by default). |
| Credentials | A camera user with *live view* rights; typed in the camera fields (stored encrypted), **never** in the RTSP address (the server refuses addresses with a password). |
| Clock | Server PC clock synchronised (Windows time service); camera clocks are not used (segment times come from the server). |
| Server PC | One `ffmpeg` process per recording camera, copying the stream without re-encoding (light load; **measure on site** — not yet measured on the factory PC). |
| Disk | Per clip ≈ bitrate × 60 s. Example: 1 Mbit/s → ~7.5 MB per camera per passage; 2 cameras × (entry + exit) × 100 trips/day ≈ 3 GB/day ≈ 90 GB for 30 days. Use a separate data disk or SSD; keep `storage.min_free_gb` above a few days of video. |
| Software | FFmpeg (LGPL build) is installed with the server in `C:\Program Files\TruckGate\ffmpeg` and found automatically. Another build can be used with `Video:FfmpegPath` / `Video:FfprobePath` in `C:\ProgramData\TruckGate\config\appsettings.json`. `Video:Disabled = "true"` turns the recorder off. |

Setup: *Cameras* page → for each gate camera enter the RTSP sub-stream address (ONVIF discovery fills it when the camera reports it), tick **record video**, check that the recorder column shows *recording* and *last frame* moves. Then scan a test ticket and play the clip in the trip page.

**Privacy:** video is recorded only around scans, kept for the retention period, and visible only to authorised roles. Put a sign "zone under video surveillance" at the gate and follow local law on video surveillance of workers and drivers.

## 6. Receipt life cycle and "burned receipts"

Every quarry ticket used at the gate is listed in *Burned receipts* (never deleted): ticket, trip, truck (+ status), driver, operator, issue / entry / exit times, life-cycle state (*scanned at entry → under review → inside the factory → scanned at exit → closed*, or *rejected*), review state (open / investigating / decided), entry and exit video state, photo state, paid. Filters: period, state, review, search (ticket, trip, truck number, plate). A row opens the complete trip (photo, pictures, videos, notes, labels, reviews, history).

## 7. Payments per truck

- Each truck has its own price, with history (`truck_prices`); a trip is paid at the price in force when it entered. Changing a price needs a reason and applies to new trips only.
- Statement (period): per truck — eligible trips (full / partial), price, trips amount, pending adjustments, total, already paid in the period, status. **Total = eligible trips × approved price of the truck (+ adjustments)**; a partial trip is paid at `trip.partial_pay_percent`.
- *Closed trips not payable yet* lists each excluded trip and why (truck not approved, no price, exit not recorded, review open).
- Corrections are **adjustments** (positive or negative, with a reason), applied in the truck's next payment batch. A recorded payment never changes.
- Payment batches by truck (select the trucks), CSV for Excel, print / PDF from the browser with a signature line.

## 8. Training data (human labels only)

- Supervisors label pictures (load, plate as read, quality) and whole trips: `NORMAL_TRIP, EMPTY_TRUCK, LOADED_TRUCK, INCOMPLETE_CARGO, WRONG_TRUCK, PLATE_UNREADABLE, POSSIBLE_MISMATCH, CAMERA_FAILURE, NEEDS_REVIEW`. The latest label wins; earlier ones stay in the history.
- *Review → export training data* (zip): `images/` + `labels.jsonl` (one line per picture), `trips.jsonl` (one line per trip: decisions, trip labels, `human_verified`, video list with fingerprints), optional `videos/` (only files that match their fingerprint), `README.txt`, `problems.txt`.
- **No automatic image analysis exists in TruckGate 1.1.** Labels are never produced by a model; this data is collected so that a model can be trained and evaluated later.

## 9. Acceptance scenarios and how each is checked

"Automated" tests are in `server/tests` (PostgreSQL integration tests, run by CI) and `quarry-app/quarry-core` (pure Kotlin checks). "On site" means it needs the real phone, printer, scanner or cameras.

| # | Scenario | Check |
|---|---|---|
| 1 | Operator issues a receipt for an existing truck | Automated (`GateFlowTests`, `EnrollmentTests.Enrolled_phone_can_issue_tickets…`); on site with the printer |
| 2 | Operator registers a truck that does not exist | Phone logic in `quarry-core` checks (plates, reserved numbers); on site |
| 3 | New truck appears on the server as pending | Automated (`WorkflowTests.New_truck_from_phone_is_pending…`) |
| 4 | Supervisor is notified | Automated (alert `TRUCK_REGISTERED` asserted in the same test); SMS/WhatsApp on site |
| 5–6 | Five fields completed, truck approved | Automated (same test) |
| 7 | Phone receives the updated list | Automated (signed configuration after approval: status `APPROVED`, new number); on the phone on site |
| 8, 11, 13 | Valid receipt scanned, consumed, truck inside | Automated (`GateFlowTests`) |
| 9–10 | 30 s before / after, linked to the trip | Automated with a real RTSP test stream (`VideoTests.Scan_saves_30_seconds…`, needs `ffmpeg` + `mediamtx`, provided in CI); on site with the real cameras |
| 12 | Duplicate scan detected | Automated (`Replayed_ticket_is_rejected…`, `Simultaneous_scans_of_one_ticket…`) |
| 14–16 | Exit receipt, exit video, trip closed | Automated (`ExitReviewTests`, exit clip in `VideoTests`) |
| 17 | Receipt appears in burned receipts | Automated (`WorkflowTests`, `ReceiptsService`) |
| 18–19 | Trip in the statement; unapproved truck not payable | Automated (`WorkflowTests`, `SyncPayTests`) |
| 20 | Camera failure → visible alert | Automated (`VideoTests` dead camera, `CameraTests.A_dead_camera…`) |
| 21 | Offline phone sync without duplicates | Automated server side (`SyncPayTests`, `HttpTests.Quarry_phone_syncs…`); phone side on site |
| 22 | Two scans do not mix recordings | Automated (`VideoTests`: two trucks one second apart, separate files) |
| 23 | History kept after truck changes | Automated (`WorkflowTests` merge / price history, plate history) |
| 24 | Critical actions in the audit log | Automated (`IntegrityTests`, audit chain verified at the end of the workflow tests) |

Whether these tests **passed** is reported in `release/BUILD_REPORT.md` for each build; a scenario is not "passed" until CI (or the site test) ran it.

## 10. Factory test, step by step

1. Install / upgrade the server (installation guide). Sign in as administrator; check *Settings*: factory name, price per trip (used as the default when approving trucks), photo policy, sync policy, video 30 s / 30 s, retention.
2. *Cameras*: RTSP sub-stream + record video for each gate camera; wait until *recording*; check *storage*.
3. Update the quarry phones to app 1.1.0; sync each phone once (it receives its reserved truck numbers).
4. Phone: issue a ticket for an existing truck. Phone: register a new truck with a plate that is not in the list and issue a ticket for it. Do not sync yet.
5. Gate: scan the first ticket → VALID → FULL → receipt prints. Scan it again → ALREADY USED. Scan the second ticket → NEW TRUCK → type the plate → FULL.
6. Wait one minute; open the trip from *Burned receipts*: both passage videos play; the times match the scan.
7. Sync the phone → *Trucks → pending approval* shows the new truck (and the alert). Complete the five fields → approve. The trip's review closes; on the phone, after the next sync, the truck is approved.
8. Exit: scan the gate receipt → EXIT EMPTY → trip closed; the exit video plays.
9. Unplug one camera's network cable for a scan → the recording shows *failed*, an alert appears, the trip goes to review.
10. *Payments*: the closed trip of the approved truck is in the statement at its price; record a test payment only if this is the real period, otherwise just print the statement.
11. *Integrity*: verify the audit chain.
