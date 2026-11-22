# Operations — supervisor, alerts, accountant, manager

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
supervisor accepted it.

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
decision closes all open reviews of the trip and cannot be changed afterwards.

**Random sample.** Every night 5 % of the previous day's accepted trips (at least one) are added to the
queue, marked *sample*. Checking them keeps everybody honest and produces training labels. Rejecting a
sampled trip that was not paid yet removes it from payment.

**Labels for the future AI.** Under each picture: the load seen (full / partial / empty / not visible),
the plate as you read it (type and press Enter), and the picture quality. The latest label wins; older
ones stay in the history. *Export training data* downloads a zip for a period: all pictures plus
`labels.jsonl` (camera, role, configuration version, gatekeeper decision, review decision, labels).
Pictures failing their fingerprint are left out and listed.

## Alerts (tab *Alerts*)

Raised automatically, for example: forged or reused ticket, forged or reused receipt, truck left loaded,
truck never scanned out (default 2 h), entry left without a decision (10 min), quarry phone not synced
for 24 h, repeated partial loads by the same driver or operator (3 in 7 days), camera down, evidence
picture changed. Each is shown on screen until somebody marks it handled.

### SMS without internet (GSM modem)

1. Use a GSM modem that exposes a serial port accepting standard AT commands (industrial USB modems, SIM800 /
   SIM7600 boards; USB dongles only in their "modem" mode, not the web-interface mode). Put in a SIM card
   with credit and **no PIN**. Check the exact model with a test before buying several.
2. Plug it into the server PC; find its port (Windows: Device Manager → *Ports (COM & LPT)*, e.g. `COM3`;
   Linux: `/dev/ttyUSB0`, add the service user to the `dialout` group).
3. *Settings*: set the port, the phone numbers (`+213…`), enable SMS, then **Test SMS**.

SMS go only for the chosen minimum severity (default: critical), at most 10 per hour. Texts are short and
Arabic-capable (70 characters per SMS).

### Telegram (needs internet at the factory)

1. In Telegram talk to **@BotFather**, `/newbot`, copy the token (`123456:ABC…`).
2. Create a group with the managers, add the bot, send a message, and get the group id (for example by
   opening `https://api.telegram.org/bot<TOKEN>/getUpdates` once; group ids start with `-100`).
3. *Settings*: paste the token (stored encrypted), the chat ids, enable Telegram, **Test Telegram**.

Telegram messages carry the camera picture of the scan when there is one. WhatsApp is not supported:
it requires a Meta business account and an internet service; Telegram covers the same use.

## Accountant (tab *Payments*)

1. Set the **price per trip** once (*Settings*, admin). Partial loads are paid at `trip.partial_pay_percent`
   (100 % by the agreed rule) and stay marked.
2. Choose the period: the statement lists, per driver, the `CLOSED` trips not yet paid, with full and
   partial counts and amounts. Trips of the period still open (inside, waiting for review) are counted
   separately — wait or ask the supervisor.
3. **CSV (Excel)** downloads the detail and the totals (UTF-8, opens with Arabic text in Excel).
4. **Record payment** marks those trips `PAID` for good and keeps the batch (period, price, total, note,
   who). A paid trip cannot be rejected afterwards; a correction goes into the next payment.

## Manager (tab *Today*)

Entries, exits, accepted / pending / rejected, partial loads, trucks inside, trips waiting for review,
open alerts, phones not synced; entries per hour, per truck and per driver; the last scans. Refreshed every
30 seconds.

## Settings (admin)

All business parameters with their bounds: gate times, exit deadline, price and partial rate, sample
size, alert channels, quarry phone rules (pushed to the phones in their signed configuration at the next
sync), login lockout. Every change is in the audit chain.
