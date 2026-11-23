# Field pilot (step 7) — 2 to 4 weeks next to the paper process

The software is complete for phase 1, but it has only run in automated tests. The pilot is where it
meets the real quarry, the real trucks, the real cameras and the real people. **Keep the current paper
process unchanged during the pilot** and compare every day.

## Before day 1

| Item | Check |
|---|---|
| Server PC | Installed (`GATE_STATION.md` §2), UPS, daily backup of the database **and** `data/` to a disk that leaves the factory |
| Accounts | Admin, 1 supervisor, the gatekeepers (PIN), the quarry operators (PIN), accountant, manager |
| Fleet | Every truck (fixed number, fleet number painted on it, plate) and every driver entered |
| Quarry site | Boundary drawn (walk the quarry edge with a phone GPS app and note 5–10 points) |
| Phones | 1–2 Android phones (Android 8.0+, fingerprint, GPS), enrolled at the factory, kiosk mode, Bluetooth printer paired and tested with Arabic, spare paper rolls, power bank |
| Cameras | Rear-plate and cargo cameras added and tested **at night**; plate readable at the stopping point |
| Gate PC | Chrome with `--kiosk-printing`, receipt printer, USB scanner in keyboard mode with Enter, tested on a printed ticket in daylight and at night |
| Alerts | WhatsApp template approved and tested to every manager; SMS through the ZTE router tested (critical alerts when the internet is down) |
| Price | *Settings* → price per trip = **1400.00** (DZD); partial rate confirmed (100 % by the agreed rule) |

## Every day of the pilot

1. Quarry: issue a ticket for every loaded truck **and** keep doing the paper as before.
2. Gate: scan every ticket at entry and every receipt at exit; still record the paper as before.
3. Evening (supervisor, 15 minutes):
   - *Today* tab: entries vs. the paper count for the day — they must match;
   - *Review* tab: empty the queue, including the samples; label at least 20 pictures;
   - *Alerts* tab: handle every alert, write down false alarms;
   - *Integrity* tab: **verify**, then **anchor**, and send the anchor code by SMS to the manager;
   - *Phones*: last sync of each phone is from today.
4. Note every problem in a shared sheet: date, who, what happened, what was expected.

## Weekly

- Accountant: produce the statement for the week and compare with the paper payroll **without recording
  the payment** in the system until the pilot is accepted.
- Export training data for the week (it is needed later for the AI).
- Restore last night's backup on a spare PC once, to be sure it works.

## What to measure

| Measure | Target to accept the pilot |
|---|---|
| Trips in system vs. paper | 100 % match, every difference explained |
| Tickets not printable / not scannable | < 1 %, with the cause |
| Time at the gate per truck (scan → decision) | ≤ 30 s |
| Rear plate readable in pictures (day / night) | ≥ 95 % / ≥ 85 % — otherwise move the camera or add IR light |
| Phones synced at least once per day | Every day |
| Alerts that were false alarms | Listed, settings adjusted |
| Statement vs. paper payroll | Equal |

## Known limits to watch

- **Cameras are not AI.** The system stores pictures and labels; a person still judges the load and the
  plate. The AI comes later, trained on the pilot's labelled pictures.
- **Collusion between the quarry operator and a driver** (a ticket for a truck that was not loaded) is not
  prevented by signatures. It is made visible by the quarry photo, the cargo camera at entry, the random
  sample, and the partial-load counters. Look at those every day during the pilot.
- **A phone with no signal for days** stops issuing after 24 h without sync (setting). Plan a daily moment
  where the phone sees the factory Wi-Fi or mobile data.
- **Time**: decisions use the server clock. Keep the server PC on time (Windows time service / NTP).
- **Paper tickets** are stamped and kept; the system is the record, the paper is the backup during the
  pilot.

## After the pilot

Fix what the sheet shows, then decide together: stop the paper process, record payments in the system,
and start collecting data for the AI phase.
