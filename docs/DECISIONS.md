# TruckGate — Agreed Decisions (architecture record)

Approved with the product owner on 2026-11-21/22. Change only by explicit decision; note the date.

## Scope
- New standalone commercial product. No dependency on any earlier project or database.
- Phase 1 covers **transport** (quarry → factory) only. Décapage is out of scope for now
  (ticket kind `0x02` is reserved).
- AI is **not** used for decisions in phase 1, but all gate images are captured, hashed and labeled
  from day one so models can be trained later and plugged into an existing slot in the decision engine.

## Operations (factory of reference)
| Item | Value |
|---|---|
| Trucks | 15–30 dump trucks (Foton, with cab canopy) |
| Volume | ~200 loaded entries per day; trucks exit through the same gate |
| Quarry → factory | ~15 min; full cycle ~30 min; shortest possible cycle 20 min |
| Night work | Yes |
| Loads | Uncovered (no tarp) |
| Quarry | No power, near-zero mobile coverage |

## Trust chain
1. Quarry phone signs a single-use transport ticket (ECDSA P-256, Android Keystore). Printed on a
   Bluetooth thermal printer. Spec: `ticket-spec/SPEC.md`.
2. Gatekeeper scans it; the server verifies offline with registered public keys (no link to the quarry needed).
3. Two gate cameras take automatic snapshots at scan time: **rear plate** (front plate is unusable at night
   because of headlights) and **cargo** (pole-mounted, looking into the body).
4. Gatekeeper decides (full / partial / empty / different truck / plate unreadable); the gate prints a receipt.
5. At exit, the receipt QR is scanned; cameras capture the truck leaving (empty-check, unloading proof).
6. Supervisor reviews pending trips plus a daily random sample of accepted trips (also produces training labels).

## Business rules
| Rule | Value |
|---|---|
| Payment | Fixed price per trip |
| Partial load | Paid in full **with a note**; per-driver/operator counters and alert threshold; partial pay rate is a setting (100 % now) |
| Driver selection | From a list; truck's default driver preselected; change requires a reason |
| Ticket min age at gate | 10 min |
| Ticket max validity | 90 min (then PENDING) |
| Min gap between two entries of the same truck | 20 min (REJECT) |
| Min gap between two tickets for the same truck at the quarry | 20 min (app refuses) |
| Unreadable rear plate | Trip PENDING, not paid until reviewed |
| Paper ticket after scan | Digitally "burned"; paper stamped/punched and kept in a daily envelope (not destroyed) |
| Nothing is deleted | Rejections and corrections are new records with author and reason |

## Sync (quarry phone)
- Tickets (≈1 KB each) sync automatically in the background whenever any signal appears.
- Photos sync over factory Wi-Fi (or good signal). SMS fallback for ticket summaries is an option.
- Tickets must sync within 24 h, otherwise the app stops issuing until synced.
- Sequence gaps are also detected at the gate from scanned tickets alone.
- App records GPS time and phone-clock delta; server time is authoritative.

## Technology
| Part | Choice |
|---|---|
| Quarry app | Kotlin (native Android, Jetpack Compose), kiosk mode, Android Keystore |
| Server | .NET 10 LTS (ASP.NET Core), PostgreSQL, local and offline-first |
| Web UI | React + TypeScript, Arabic (RTL) and French (LTR) |
| AI (later) | Python service; permissive-licence models (Ultralytics AGPL avoided unless licensed) |
| Alerts | SMS via GSM modem (critical, works offline) + Telegram bot (details, photos) |
| Gate station | Normal screen; keyboard + 2D scanner in USB-serial mode |
| Printing | Tickets and receipts rendered as images (reliable Arabic on thermal printers) |

## Storage estimate
~200 entries + ~200 exits per day ≈ 8–9 GB/day ≈ 250 GB/month. Full clips kept 90 days; selected
labeled frames kept permanently; trip evidence kept per accounting rules.

## Delivery plan (phase 1)
1. Ticket core: spec, Kotlin signing/encoding, C# verification + gate policy, shared vectors, attack tests.
2. Server core: database, verification API, hash-chained audit log.
3. Quarry app: enrollment, issuance, printing, local storage, geofence.
4. Gate station: scan, snapshots, receipt, exit.
5. Supervisor review + labels + alerts.
6. Manager, accountant, admin, sync channels.
7. Field pilot in parallel with the paper process (2–4 weeks).

## Gate station (step 4)
| Topic | Decision |
|---|---|
| Where | Everything at the gate runs on the factory PC, in a browser, on the LAN. No internet needed |
| Scanner | USB 2D scanner in keyboard mode, Enter suffix; the scan box always has focus |
| Camera discovery | ONVIF WS-Discovery on every LAN interface; ONVIF login lists every stream (NVR channels) with a preview; non-ONVIF cameras by snapshot URL |
| Camera selection | Administrator chooses per camera: role (rear plate / cargo / overview), gate, *capture at scan*, frames 1–10 |
| Capture | Starts in parallel with ticket verification; a dead camera never blocks the gate (error recorded, one alert per 30 min) |
| Evidence | Each JPEG stored with SHA-256 in an append-only table under the audit chain; re-checked whenever it is opened; camera configuration version stored with every picture |
| Rejected scans | Also photographed and kept |
| Logins | Gatekeepers and quarry operators: PIN; other roles: password of 10+ characters; lockout after 5 failures for 5 min; quarry operators cannot use the web UI |
| Camera passwords | AES-256-GCM, key file outside the database, never in the audit log or API responses |

## Exit, review, alerts, sync, payment (steps 5–6)
| Topic | Decision |
|---|---|
| Gate receipt | Printed at entry from the browser on any installed printer; QR `TR1:` signed by the server key (85 bytes, spec §9); scanned in the same box as tickets |
| Lost receipt | Exit chosen from the list of trucks inside, recorded `MANUAL_SELECT` / `RECEIPT_MISSING` |
| Exit checks | Receipt forged → critical alert; reused → critical alert; exit < 3 min after entry or loaded exit → review |
| Closing | Exit empty with no open review → trip `CLOSED` (payable) at once |
| Truck never out | After 2 h → review `NO_EXIT_SCAN` + alert; supervisor acceptance closes it |
| Damaged QR | Ticket number + short code, only against the synced copy, always PENDING (spec §5) |
| Review | One decision per trip closes all its open reviews; reject needs a reason; decisions are final |
| Random sample | 5 % of yesterday's accepted trips, at least one, every night |
| AI labels | Per picture: load, plate text, quality; latest wins, history kept; zip export with labels.jsonl |
| Alerts | WhatsApp through Meta's official Cloud API (approved utility template `truckgate_alert`, optional picture); SMS through the ZTE router's web interface or a USB GSM modem (works offline, UCS-2 Arabic, ≤ 10/h, critical by default); Telegram bot; unofficial WhatsApp Web tools refused (ban risk); 5 attempts per channel; every attempt logged |
| Watchdog | Every minute: undecided entries, trucks never out, phones not synced 24 h, nightly sample; partial-load counters per driver and operator at decision time |
| Phone sync | WorkManager whenever a network exists (≥ every 15 min) and right after each ticket; tickets, print counts and signed voids; photos only on Wi-Fi; requests signed with the auth key and time-stamped with the server clock |
| Server checks on sync | Ticket signature with the phone's key, same number with different content → critical alert, void signature, photo SHA-256 equals the hash inside the ticket |
| Payment | Statement per driver of `CLOSED` trips by entry date; CSV for Excel; recording a payment moves trips to `PAID` for good and keeps the batch |
| Settings | Only known keys with bounds; quarry parameters reach phones in the signed configuration |

## Site values given (factory pilot)
| Item | Value |
|---|---|
| Price per trip | 1400.00 DZD (set in *Settings* → `trip.price_per_trip`) |
| Factory internet | Yes — WhatsApp is the main alert channel |
| Router / modem | ZTE **G5C** — SMS through its web interface (G5-series JSON-RPC, goform as fallback), to be confirmed with the Test button |

Router protocols were implemented from public descriptions of the routers' web interfaces; no third-party
source code is included in TruckGate.

## Oldest supported Android: 8.0 (API 26) — 2026-11-22

The first field phone runs an Android version older than 11, so the minimum went from Android 11 to
Android 8.0. The ticket key keeps the same rule on every version: a fingerprint for every signature
(Android 11+: `setUserAuthenticationParameters(0, BIOMETRIC_STRONG)`; Android 8–10: validity −1,
which only a fingerprint through BiometricPrompt can unlock — the screen PIN cannot). Differences on
older phones: StrongBox only from Android 9 and only on some phones; some older phones cannot produce
a key attestation certificate — the key is then still created in Android Keystore and the server
stores the attestation as missing. Android 8.0 is the floor because the app uses java.time and
java.util.Base64 (API 26). Emulator tests run on Android 8.0, 9, 10, 11 and 14.

## Workflow 1.1 — 2026-11-23

- **New trucks from the quarry phone.** The phone may register a truck offline. Numbers 40000–65535 are
  reserved for phones in blocks of ten (`truck_id_blocks`, given in the signed configuration), so two
  phones never create the same number. Plates compare by digits only (Arabic digits, spaces, dashes).
  The server keeps the truck `PENDING_APPROVAL` until a supervisor approves it with the five fields
  (truck number, last name, first name, registration, price), rejects it with a reason, or merges it into
  an existing truck (`merged_into`; trips count for the target through `tg.effective_truck_id`). A
  database trigger refuses any other status change. Existing trucks were migrated as approved with the
  global price in force at the time of the upgrade.
- **Pending trucks may enter.** The gate shows *NEW TRUCK* (`TRUCK_PENDING_APPROVAL`, pending outcome);
  a number reserved for the signing phone but not synced yet is treated the same way and the gatekeeper
  types the plate he reads (`gate_decisions.observed_plate`). Approval / merge closes reviews whose only
  reasons were the unknown / pending truck.
- **Payable = approved + priced + closed + exit recorded + no open review.** Price per truck with
  history (`truck_prices`, price in force at entry). Corrections are adjustments applied to the next
  batch; recorded batches never change. Batches are per truck (`pricing = PER_TRUCK`); 1.0 batches keep
  their global price.
- **Passage video.** Continuous stream copy (no re-encoding) of each recording camera into 2-second
  MPEG-TS segments by `ffmpeg`; on each scan the window [scan − 30 s, scan + 30 s] is cut from the
  segments into one MP4 per camera and scan, with JSON metadata and SHA-256 in the database. Status is
  never upgraded: missing footage is `FAILED` (alert + review reason), a gap is `PARTIAL`. Times come
  from the server clock (segment file times), not from the cameras. FFmpeg LGPL (shared libraries) is
  shipped with the Windows installer; the recorder can be turned off with `Video:Disabled`.
- **Supervisors** manage trucks, cameras and operating settings; alert channels, sign-in rules and the
  time zone remain administrator-only (enforced in `SettingsService`).
- **Training data is human-labelled only.** Trip labels and picture labels are set by supervisors; the
  export marks `human_verified`. TruckGate 1.1 contains **no** automatic image or video analysis.
- **Web interface**: Arabic-first RTL, IBM Plex Sans Arabic (OFL, bundled — no internet needed), graphite
  sidebar, signal colours reserved for states, six fixed gate states.

