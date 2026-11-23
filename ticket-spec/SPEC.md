# TruckGate Ticket Specification — v1

Status: **draft for review** · Applies to: quarry app (Kotlin), server (C#), reference (Python)

This document is normative. The Python reference (`reference/tgticket.py`) is its executable form,
and the vectors in `test-vectors/` are the conformance suite. If this text and the vectors disagree,
that is a bug to fix in the same pull request.

---

## 1. Purpose and trust model

A **transport ticket** is created by the quarry operator's phone each time a truck is loaded, printed
on paper, carried by the driver and scanned once at the factory gate.

| Party | Holds | Can | Cannot |
|---|---|---|---|
| Quarry phone | ECDSA P-256 **private** key inside Android Keystore (hardware-backed, non-exportable) | Sign tickets | Export its key; sign without operator biometric |
| Gate / server | Quarry phones' **public** keys only | Verify tickets | Create a valid quarry ticket |
| Driver | Paper ticket | Present it | Alter it (signature breaks), reuse it (single use) |

The factory server has its own key (it signs phone configurations and gate receipts, §9). It is not a
registered quarry phone, so it can never produce an acceptable quarry ticket; a device registered as
`GATE_STATION` is refused for quarry tickets (`WRONG_DEVICE_KIND`).

## 2. Binary layout (127 bytes, big-endian)

| Offset | Size | Field | Type | Notes |
|---:|---:|---|---|---|
| 0 | 1 | `version` | u8 | `1` |
| 1 | 1 | `kind` | u8 | `0x01` transport · `0x02` décapage (reserved, never accepted at the gate) |
| 2 | 2 | `device_id` | u16 | Registered quarry phone |
| 4 | 4 | `seq` | u32 | Per-device counter, strictly increasing, never reset |
| 8 | 4 | `issued_at` | u32 | Unix seconds UTC — GPS time when available |
| 12 | 2 | `clock_delta` | i16 | Phone clock − GPS time, seconds, clamped to ±32767 |
| 14 | 2 | `truck_id` | u16 | |
| 16 | 2 | `driver_id` | u16 | |
| 18 | 2 | `operator_id` | u16 | Quarry operator who confirmed with biometric |
| 20 | 4 | `lat_e6` | i32 | Latitude × 10⁶; `0` when `NO_GPS` |
| 24 | 4 | `lon_e6` | i32 | Longitude × 10⁶; `0` when `NO_GPS` |
| 28 | 1 | `flags` | u8 | See §3 |
| 29 | 2 | `config_version` | u16 | Signed device configuration in force |
| 31 | 32 | `photo_sha256` | bytes | SHA-256 of the loaded-truck photo file |
| 63 | 64 | `signature` | bytes | ECDSA P-256 / SHA-256 over bytes `0..62`, raw `r‖s` (IEEE P1363) |

Decoders MUST reject: length ≠ 127, `version` ≠ 1, unknown `kind`, any reserved flag bit set.

**Ticket ID** (human and database key): `Q{device_id}-{seq zero-padded to 6}` → `Q1-000482`.
`(device_id, seq)` is unique for the life of the system.

## 3. Flags

| Bit | Name | Set by the app when | Gate effect |
|---|---|---|---|
| 0x01 | `NO_GPS` | No GPS fix; `issued_at` comes from the phone clock | PENDING |
| 0x02 | `OUTSIDE_GEOFENCE` | Reserved — the app blocks issuance outside the quarry; kept for supervised overrides | PENDING |
| 0x04 | `DRIVER_CHANGED` | Driver differs from the truck's default driver (reason recorded in the app) | Note only |
| 0x08 | `CLOCK_MISMATCH` | \|phone − GPS\| exceeds the configured threshold | PENDING |
| 0xF0 | reserved | — | MALFORMED if set |

## 4. QR text

`TG1:` + Base45 (RFC 9285) of the 127 bytes → 195 characters, all in the QR **alphanumeric** set,
so the code stays small and reads reliably on 58 mm thermal paper. Decoders MUST reject a missing or
different prefix, characters outside the Base45 alphabet (including lowercase), a length ≡ 1 (mod 3),
and chunks that overflow (triplet > 65535, final pair > 255).

## 5. Short code (manual fallback)

For a damaged QR the gatekeeper types the printed ticket ID and short code:

`short_code = Crockford-Base32(first 30 bits of SHA-256(ticket bytes))`, 6 characters shown `XXX-XXX`.

A short code is **not** a signature. A ticket entered this way is matched against the synced copy of
the ticket on the server and is always PENDING until a supervisor confirms it.

Server behaviour for manual entry (ticket ID + short code):

| Situation | Outcome | Reason |
|---|---|---|
| Ticket ID not in the server (phone not synced yet) | REJECT | `NO_SYNCED_COPY` |
| Short code differs from the synced copy | REJECT | `BAD_SHORT_CODE` |
| Otherwise | §6 policy applied to the synced bytes, then at least PENDING | `MANUAL_ENTRY` added |

## 6. Gate verification policy (normative order)

Inputs: QR text, server time `now`, device registry, truck registry, used IDs, voided IDs, parameters.

| Parameter | Default | Meaning |
|---|---:|---|
| `min_travel_s` | 600 | Ticket younger than this → `TOO_EARLY` |
| `max_validity_s` | 5400 | Ticket older than this → `EXPIRED` |
| `min_truck_gap_s` | 1200 | Same truck entered less than this ago **with another ticket** → `TRUCK_GAP` (a replay of the same ticket is `ALREADY_USED` only) |
| `future_tolerance_s` | 120 | `issued_at` beyond `now` + this → `ISSUED_IN_FUTURE` |

**Stage A — stop at the first failure (outcome REJECT):**
1. Decode QR text and bytes → `MALFORMED`
2. Device not registered → `UNKNOWN_DEVICE`
3. Device is not a quarry phone → `WRONG_DEVICE_KIND`
4. Signature invalid → `BAD_SIGNATURE`
5. Kind is not transport → `WRONG_KIND`

**Stage B — evaluate every check and collect reasons in this order:**

| # | Check | Reason | Class |
|---|---|---|---|
| 6 | Device revoked and `issued_at ≥ revoked_at` | `DEVICE_REVOKED` | reject |
| 6 | Device revoked and `issued_at < revoked_at` | `ISSUED_BEFORE_REVOCATION` | pending |
| 7 | Ticket ID already used | `ALREADY_USED` | reject |
| 8 | Ticket ID voided | `VOIDED` | reject |
| 9 | Truck unknown or inactive | `UNKNOWN_TRUCK` | pending |
| 9 | Else: `now − last_entry_at < min_truck_gap_s` | `TRUCK_GAP` | reject |
| 10 | `issued_at > now + future_tolerance_s` | `ISSUED_IN_FUTURE` | pending |
| 10 | Else `now − issued_at < min_travel_s` | `TOO_EARLY` | pending |
| 10 | Else `now − issued_at > max_validity_s` | `EXPIRED` | pending |
| 11 | Flag `NO_GPS` | `NO_GPS` | pending |
| 11 | Flag `OUTSIDE_GEOFENCE` | `OUTSIDE_GEOFENCE` | pending |
| 11 | Flag `CLOCK_MISMATCH` | `CLOCK_MISMATCH` | pending |
| 11 | Flag `DRIVER_CHANGED` | `DRIVER_CHANGED` | note |

**Outcome:** any reject reason → `REJECT` with the reject reasons only; else any pending reason →
`PENDING` with the pending reasons; else `ACCEPT`. Notes are returned in every case after Stage A.

Boundaries are inclusive for acceptance: age exactly `min_travel_s` or exactly `max_validity_s` and a
gap exactly `min_truck_gap_s` are accepted.

Nothing is ever discarded: every scan, including REJECT, is stored with its snapshots (see DECISIONS).

## 7. Security notes

- **Single use is keyed by ticket ID, never by ticket bytes.** ECDSA signatures are malleable:
  `(r, n−s)` is also valid for the same content. Vector `reject_malleated_copy_of_used_ticket` covers this.
- **Copies and photos of a ticket** carry the same ID, so only the first scan can succeed.
- **Deleted or hidden tickets** leave gaps in `seq`; the server reports gaps from scans alone, before sync.
- **Clock tampering:** the app records GPS time and the phone-clock delta; the server compares with its
  own time at the gate. Server time is the only time used for decisions.
- **Revocation:** revoking a lost phone rejects its tickets issued at or after the revocation time;
  earlier tickets become PENDING, never automatic.
- **Android Keystore** returns DER signatures; the app converts them to raw `r‖s` before encoding.
  Signatures from Keystore are randomized; the vectors use RFC 6979 only to be reproducible.

## 8. Void record (quarry phone)

An operator can void a ticket on the phone (reason required, fingerprint required). The phone signs,
with the **ticket key**, the UTF-8 text `TGVOID1|<ticket id>|<unix seconds>|<reason>` (reason trimmed of
leading and trailing spaces, never empty) and stores the raw `r‖s` signature. Void records are uploaded
with sync; the server verifies the signature with the phone's ticket key, then stores a `VOIDED` ticket
event and the gate rejects the ticket with `VOIDED`. A void never deletes the ticket. A void received for
a ticket that was already used at the gate is stored and raises an alert (it cannot undo the entry).

## 9. Gate receipt

Printed by the gate station for the driver after the entry decision, and scanned at exit to find the
trip. Signed by the **server key** (the key phones pin at enrollment). Only the server reads receipts.

| Offset | Size | Field | Type | Notes |
|---:|---:|---|---|---|
| 0 | 1 | `version` | u8 | `1` |
| 1 | 1 | `kind` | u8 | `0x10` gate receipt |
| 2 | 4 | `trip_id` | u32 | Trip number shown as `T-{trip_id zero-padded to 6}` |
| 6 | 2 | `gate_id` | u16 | Gate of the entry |
| 8 | 4 | `entered_at` | u32 | Unix seconds, server time of the entry scan |
| 12 | 2 | `truck_id` | u16 | |
| 14 | 2 | `ticket_device_id` | u16 | Quarry ticket used for the entry |
| 16 | 4 | `ticket_seq` | u32 | |
| 20 | 1 | `load` | u8 | `0` unknown · `1` full · `2` partial · `3` empty |
| 21 | 64 | `signature` | bytes | ECDSA P-256 / SHA-256 over bytes `0..20`, raw `r‖s` |

QR text: `TR1:` + Base45 of the 85 bytes. Decoders MUST reject: another prefix, length ≠ 85,
`version` ≠ 1, `kind` ≠ `0x10`, `load` > 3, and the Base45 errors of §4.

**Exit policy.** Stage A (REJECT, stop): `MALFORMED`, `BAD_SIGNATURE`. Stage B (server lookups):

| Check | Reason | Class |
|---|---|---|
| Trip does not exist, or its truck/ticket differ from the receipt | `RECEIPT_MISMATCH` | reject |
| Trip already has an exit scan | `ALREADY_EXITED` | reject |
| Entry not decided yet (trip still `AT_GATE`) | `ENTRY_NOT_DECIDED` | reject |
| `now − entered_at < min_unload_s` (default 180) | `EXIT_TOO_SOON` | pending |

When the receipt is lost the gatekeeper selects the truck among the trucks inside; the scan is stored as
`MANUAL_SELECT` with the note `RECEIPT_MISSING`. After the scan the gatekeeper records `EXIT_EMPTY` or
`EXIT_LOADED`; a loaded exit sends the trip to review and raises a critical alert.

## 10. Conformance

Every implementation must pass all files in `test-vectors/`:

| File | What it checks |
|---|---|
| `codec.json` | Fields ↔ bytes ↔ QR text ↔ short code, byte-exact |
| `signature.json` | Signature validity, including tamper, wrong key, malleated, zero values |
| `malformed.json` | Every case must fail decoding |
| `policy.json` | Full gate decision per case, with its own context |
| `void.json` | Void record message bytes (UTF-8, trimming) and signature validity per key |
| `receipt.json` | Gate receipt fields ↔ bytes ↔ QR text, server-key signature, malformed cases |

Regenerate with `python reference/gen_vectors.py`; CI runs `--check` so vectors cannot drift silently.
