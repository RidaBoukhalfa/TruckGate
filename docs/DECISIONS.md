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
