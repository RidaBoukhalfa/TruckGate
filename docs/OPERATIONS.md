# Operations — supervisor, alerts, accountant, manager

> Version 1.1 adds new trucks registered on the quarry phones with supervisor approval, a price per
> truck, the passage video (30 s before and after each scan), the *Burned receipts* page, investigation
> notes, trip labels, adjustments and payments per truck. The complete workflow, the camera requirements
> and the acceptance scenarios are in [`WORKFLOW.md`](WORKFLOW.md).

## Trip life cycle

```
scan at entry ──► AT_GATE ──decision──► ENTERED ──exit empty──► EXITED_EMPTY ──► CLOSED ──payment──► PAID
                     │                     │                                        ▲
                     └──reasons──► PENDING_REVIEW ◄──exit loaded / too soon / never left
                                       │      └──accept──► ENTERED (still inside) or CLOSED (already out)
                                       └──reject──► REJECTED
```

Every change is a new event (who, when, why); nothing is edited or deleted. A trip is paid only when it is
`CLOSED`: the truck entered with a valid ticket, the gatekeeper saw the load, and it left empty — or a
supervisor accepted it. Since 1.1 it must also belong to an **approved truck with a price**, and (with
`pay.require_exit_scan`, default on) have a recorded exit.

## Supervisor (tab *Review*)

The queue lists trips waiting for a decision, urgent first (red: impossible times, empty or different
truck, loaded exit…). Opening a trip shows:

- the ticket: number, time and GPS position at the quarry, phone-clock difference, operator, whether the
  phone voided it, the reason given when the driver was not the usual one;
- the **photo taken at the quarry** (arrives when the phone syncs over Wi-Fi; its fingerprint is inside
  the signed ticket, so it cannot be swapped);
- the entry and exit scans with the gatekeeper's decisions and every camera picture;
- the history of the trip.

Decide **accept**, **accept as partial** (paid at the partial rate) or **reject** (reason required). One
decision closes all open reviews of the trip and cannot be changed afterwards. **Request investigation**
(with what to check) keeps the review open and marks it *investigating* in the queue; **notes** can be
added to any trip. The trip page also plays the **passage videos** (entry and exit) and shows when a
recording failed and why.

**Trucks (tab *Trucks*).** *Pending approval* lists trucks added on the quarry phones: who added it,
from which phone, the plate typed at the quarry and the plate seen at the gate, its trips, and a possible
duplicate. Complete the five fields (truck number, last name, first name, registration, price per trip)
and approve; or reject with a reason; or merge into the existing truck. *Registered trucks* opens each
truck: edit, plate change (history kept), price change (reason required, applies to new trips), drivers,
trips and the audit trail.

**Random sample.** Every night 5 % of the previous day's accepted trips (at least one) are added to the
queue, marked *sample*. Checking them keeps everybody honest and produces training labels. Rejecting a
sampled trip that was not paid yet removes it from payment.

**Labels for the future AI.** Trip labels (normal trip, empty truck, loaded truck, incomplete cargo,
wrong truck, plate unreadable, possible mismatch, camera failure, needs review) and, under each picture: the load seen (full / partial / empty / not visible),
the plate as you read it (type and press Enter), and the picture quality. The latest label wins; older
ones stay in the history. *Export training data* downloads a zip for a period: all pictures plus
`labels.jsonl` (camera, role, configuration version, gatekeeper decision, review decision, labels), and
`trips.jsonl` (one line per trip with the trip labels and `human_verified`); tick *with videos* to add the
passage videos that match their fingerprint. Nothing is labelled automatically.
Pictures failing their fingerprint are left out and listed.

## Alerts (tab *Alerts*)

Raised automatically, for example: forged or reused ticket, forged or reused receipt, truck left loaded,
truck never scanned out (default 2 h), entry left without a decision (10 min), quarry phone not synced
for 24 h, repeated partial loads by the same driver or operator (3 in 7 days), camera down, evidence
picture changed. Each is shown on screen until somebody marks it handled, and sent on the channels below.

Three channels, each optional, each with a **Test** button in *Settings*:

| Channel | Needs | Good for |
|---|---|---|
| WhatsApp (official Cloud API) | Internet, a Meta business account, a dedicated number | Everyday alerts with the camera picture |
| SMS through the ZTE router (or a USB modem) | The SIM in the router, **no internet** | Critical alerts when the internet is down |
| Telegram | Internet, a bot | Free alternative to WhatsApp |

### WhatsApp (official WhatsApp Business Platform)

Only Meta's official API is used: tools that drive a normal WhatsApp account through WhatsApp Web break
WhatsApp's terms and the number can be banned. The official way costs a small fee per message (Meta's
rate card for utility messages in your country) and needs a one-time setup:

1. **Number.** A phone number that is *not* used in the WhatsApp app (or delete WhatsApp from it first).
   A new SIM works; it only has to receive the verification code once — it can sit in the ZTE router,
   whose web page shows received SMS.
2. **Meta setup.** On developers.facebook.com create an app of type *Business*, add the **WhatsApp**
   product, add your number and verify it. Note the **Phone number ID** shown in *API setup*.
3. **Permanent token.** In Meta Business Settings → *System users*: add a system user, assign the app and
   the WhatsApp account, generate a token with `whatsapp_business_messaging` and
   `whatsapp_business_management`. (The token shown in *API setup* expires within a day.)
4. **Template.** In WhatsApp Manager → *Message templates* create a **Utility** template named
   `truckgate_alert`, language Arabic (`ar`), body exactly with one variable inside the text:

   > تنبيه من نظام TruckGate: {{1}}. يرجى المراجعة في لوحة التحكم.

   Optional French version (language `fr`): *Alerte TruckGate : {{1}}. Merci de vérifier dans le tableau
   de bord.* To receive the camera picture, add an **Image** header to the template and turn on
   *attach picture*. Wait for the template to be **Approved** (usually minutes, up to 48 h).
5. **TruckGate Settings:** WhatsApp phone number ID, recipients (`+213…`), template name and language,
   minimum severity (default: medium), paste the token (stored encrypted), enable, **Test WhatsApp**.

If the test fails, the message shows Meta's error code (for example 132001 = template name or language
does not exist, 131030 = recipient not allowed while the app is in development mode, 190 = token expired).

### SMS through the ZTE G5C router (no internet needed)

The SIM in the router sends the SMS, the way the router's own web page does. ZTE does not document this
interface. Two families exist and TruckGate detects which one the router speaks:

- **G5 series (G5C, G5TS…)**: encrypted JSON-RPC on `https://<router>/ubus/` — salted SHA-256 login, an
  AES-256-GCM key exchanged with the router's RSA key, number and text encrypted;
- **older models (MF / MU / MC series)**: `/goform/` form requests.

The router's HTTPS certificate is self-signed; TruckGate accepts it only for private LAN addresses
(192.168.x.x, 10.x.x.x, 172.16–31.x.x).

1. Put a SIM with SMS credit in the router and check, from the router's web page, that you can send an
   SMS by hand. Note the address of that page (on the label; for the G5 series usually
   `https://192.168.0.1`).
2. *Settings*: SMS transport `ZTE_ROUTER`, router address exactly as you open it (with `https://`),
   router user only if the login page asks for one, recipients, enable; save the router password;
   **Test SMS**.
3. **Log out of the router's web page first**: the router allows one web session, so TruckGate cannot log
   in while someone is logged in there (the test then says so).
4. If the test still fails, the message names the step (login, key exchange, send). Send that message and
   the *firmware version* (router page → device information) so the protocol can be adjusted. Until then
   WhatsApp carries the alerts; a USB GSM modem (transport `MODEM`) is the other fallback.

### USB GSM modem (alternative)

A modem exposing a serial port with standard AT commands (industrial USB modems, SIM800 / SIM7600 boards;
USB dongles only in "modem" mode). Transport `MODEM`, port `COM3` (Windows) or `/dev/ttyUSB0` (Linux),
SIM without PIN.

### Telegram

1. In Telegram talk to **@BotFather**, `/newbot`, copy the token (`123456:ABC…`).
2. Create a group with the managers, add the bot, send a message, and get the group id (for example by
   opening `https://api.telegram.org/bot<TOKEN>/getUpdates` once; group ids start with `-100`).
3. *Settings*: paste the token, the chat ids, enable Telegram, **Test Telegram**.

## Accountant (tab *Payments*)

1. Each truck has its **own price per trip** (set by the supervisor when approving the truck, changed
   with a reason). Partial loads are paid at `trip.partial_pay_percent` and stay marked.
2. Choose the period. *Statement by truck*: per truck — eligible trips (full / partial), price, trips
   amount, pending adjustments, total, already paid in the period, status. **Total = eligible trips ×
   the truck's approved price (+ adjustments).** *Closed trips not payable yet* lists each excluded trip
   and why (truck not approved, no price, exit not recorded, review open). *By driver* keeps the
   per-driver view.
3. **Adjustments**: a positive or negative amount for a truck (optionally a trip) with a reason. It is
   applied in that truck's next payment; a recorded payment never changes.
4. **CSV (Excel)** downloads trucks, trips, adjustments and drivers (UTF-8, opens with Arabic text in
   Excel). **Print / PDF** prints the statement with a signature line (choose *Save as PDF* in the
   print dialog).
5. **Record the payment of the selected trucks** marks their trips `PAID` for good and keeps the batch
   (period, per-truck prices, adjustments, total, note, who). Batches open in detail and print again.

## Manager / supervisor (tab *Today*)

*Needs your attention*: trucks waiting for approval, trips waiting for review, critical and other alerts,
camera failures, failed recordings today, sync problems, unclosed trips — each tile opens the page that
handles it. *Today*: trips, completed trips, trucks inside, burned receipts, entries, exits, partial
loads, rejected; entries per hour, per truck and per driver; the last scans. Refreshed every 30 seconds.

**Burned receipts (tab *Burned receipts*)**: every quarry ticket used at the gate with its whole life,
video and photo state, review and payment; filters by period, state, review and search.

## Settings (supervisor and administrator)

All business parameters with their bounds, grouped: quarry phones (cargo photo required, policy after
24 h without sync, cool-down, GPS — pushed to the phones in their signed configuration at the next sync),
gate times, video (seconds before / after, segment length, retention, keep reviewed, failure opens a
review) and storage threshold, finance (default price, partial rate, exit required for payment), review
sample size, factory name. Supervisors change these; **alert channels, sign-in rules and the time zone
are for the administrator only** (refused by the server for other roles). Every change is in the audit
chain.
