"""
Generates ticket-spec/test-vectors/*.json from the Python reference.

    python gen_vectors.py          # (re)write the vector files
    python gen_vectors.py --check  # fail if the committed files are out of date

Signatures are deterministic (RFC 6979), so output is byte-stable.
All keys here are TEST-ONLY keys derived from public labels.
"""
from __future__ import annotations

import base64
import calendar
import dataclasses
import hashlib
import json
import sys
from pathlib import Path

import tgticket as t

OUT = Path(__file__).resolve().parent.parent / "test-vectors"

T0 = calendar.timegm((2026, 11, 22, 6, 0, 0))  # 2026-11-22T06:00:00Z

KEYS = {
    "quarry-1": t.test_private_key("quarry-1"),
    "quarry-2": t.test_private_key("quarry-2"),
    "gate-1": t.test_private_key("gate-1"),
    "attacker": t.test_private_key("attacker"),
    "server-1": t.test_private_key("server-1"),
}


def b64(b: bytes) -> str:
    return base64.b64encode(b).decode()


def spki(label: str) -> bytes:
    return t.spki_der(KEYS[label].public_key())


def photo(label: str) -> bytes:
    return hashlib.sha256(("photo:" + label).encode()).digest()


def fields(**kw) -> t.TicketFields:
    base = dict(
        kind=t.KIND_TRANSPORT, device_id=1, seq=482, issued_at=T0, clock_delta=3,
        truck_id=5, driver_id=12, operator_id=2, lat_e6=35_123_456, lon_e6=-1_234_567,
        flags=0, config_version=7, photo_sha256=photo("default"),
    )
    base.update(kw)
    return t.TicketFields(**base)


def fields_json(f: t.TicketFields) -> dict:
    d = dataclasses.asdict(f)
    d["photo_sha256"] = f.photo_sha256.hex()
    return d


def malleate(ticket: bytes) -> bytes:
    """(r, s) -> (r, n - s): still a valid ECDSA signature over the same content."""
    r = ticket[t.SIGNED_LEN:t.SIGNED_LEN + 32]
    s = int.from_bytes(ticket[t.SIGNED_LEN + 32:], "big")
    return ticket[:t.SIGNED_LEN] + r + (t.P256_N - s).to_bytes(32, "big")


def raw_signed(label: str, signed: bytes) -> bytes:
    """Sign arbitrary bytes (used to craft structurally invalid but signed tickets)."""
    return signed + t.sign(KEYS[label], signed)


# ------------------------------------------------------------------ codec


def codec_vectors() -> dict:
    cases = []
    specs = [
        ("basic", "quarry-1", fields()),
        ("flags_driver_changed_no_gps", "quarry-1",
         fields(seq=1, flags=t.FLAG_DRIVER_CHANGED | t.FLAG_NO_GPS, lat_e6=0, lon_e6=0, clock_delta=0)),
        ("negative_values_and_max_ints", "quarry-2",
         fields(device_id=2, seq=0xFFFFFFFF, clock_delta=-32768, truck_id=0xFFFF, driver_id=0xFFFF,
                operator_id=0xFFFF, lat_e6=-90_000_000, lon_e6=180_000_000, config_version=0xFFFF,
                photo_sha256=photo("max"))),
        ("decapage_kind_reserved", "quarry-1", fields(kind=t.KIND_DECAPAGE, seq=900)),
    ]
    for name, key, f in specs:
        ticket = t.build_ticket(KEYS[key], f)
        cases.append({
            "name": name,
            "fields": fields_json(f),
            "signer": key,
            "signed_hex": ticket[:t.SIGNED_LEN].hex(),
            "signature_hex": ticket[t.SIGNED_LEN:].hex(),
            "ticket_hex": ticket.hex(),
            "qr_text": t.to_qr_text(ticket),
            "short_code": t.short_code(ticket),
            "ticket_id": f.ticket_id,
        })
    return {
        "description": "Encode/decode vectors. Implementations must produce identical signed_hex, "
                       "qr_text and short_code from fields, and decode ticket_hex back to fields.",
        "public_keys_spki_b64": {k: b64(spki(k)) for k in KEYS},
        "cases": cases,
    }


# ------------------------------------------------------------------ signature


def signature_vectors() -> dict:
    good = t.build_ticket(KEYS["quarry-1"], fields())
    tampered = bytearray(good)
    tampered[15] ^= 0x01  # flip a bit in truck_id after signing
    attacker = t.build_ticket(KEYS["attacker"], fields())
    zero_sig = good[:t.SIGNED_LEN] + bytes(64)
    r_zero = good[:t.SIGNED_LEN] + bytes(32) + good[t.SIGNED_LEN + 32:]
    cases = [
        ("valid", good, "quarry-1", True),
        ("valid_malleated_signature", malleate(good), "quarry-1", True),
        ("tampered_truck_id", bytes(tampered), "quarry-1", False),
        ("signed_by_other_key", attacker, "quarry-1", False),
        ("verified_with_wrong_registered_key", good, "quarry-2", False),
        ("all_zero_signature", zero_sig, "quarry-1", False),
        ("r_is_zero", r_zero, "quarry-1", False),
    ]
    return {
        "description": "Signature checks over bytes 0..62 with SHA-256 / ECDSA P-256, raw r||s. "
                       "Note: valid_malleated_signature MUST verify; single-use is enforced by ticket ID.",
        "cases": [
            {"name": n, "ticket_hex": tk.hex(), "public_key_spki_b64": b64(spki(k)), "expected_valid": v}
            for n, tk, k, v in cases
        ],
    }


# ------------------------------------------------------------------ malformed


def malformed_vectors() -> dict:
    good = t.build_ticket(KEYS["quarry-1"], fields())
    qr = t.to_qr_text(good)
    bad_version = bytearray(good[:t.SIGNED_LEN]); bad_version[0] = 2
    bad_kind = bytearray(good[:t.SIGNED_LEN]); bad_kind[1] = 0x7F
    reserved = bytearray(good[:t.SIGNED_LEN]); reserved[28] = 0x10
    cases = [
        ("missing_prefix", qr[len(t.QR_PREFIX):]),
        ("wrong_prefix", "TG2:" + qr[len(t.QR_PREFIX):]),
        ("lowercase_base45", t.QR_PREFIX + qr[len(t.QR_PREFIX):].lower()),
        ("invalid_base45_char", t.QR_PREFIX + "#" + qr[len(t.QR_PREFIX) + 1:]),
        ("base45_bad_length", qr + "A"),
        ("base45_triplet_overflow", t.QR_PREFIX + ":::"),
        ("truncated_126_bytes", t.to_qr_text(good[:-1])),
        ("extended_128_bytes", t.to_qr_text(good + b"\x00")),
        ("empty", ""),
        ("version_2", t.to_qr_text(raw_signed("quarry-1", bytes(bad_version)))),
        ("unknown_kind", t.to_qr_text(raw_signed("quarry-1", bytes(bad_kind)))),
        ("reserved_flag_bit", t.to_qr_text(raw_signed("quarry-1", bytes(reserved)))),
    ]
    for name, text in cases:  # self-check
        try:
            t.decode(t.from_qr_text(text))
        except t.TicketFormatError:
            continue
        raise AssertionError(f"malformed vector {name} decoded successfully")
    return {
        "description": "Every qr_text here must be rejected at decode time (gate reason MALFORMED).",
        "cases": [{"name": n, "qr_text": q} for n, q in cases],
    }


# ------------------------------------------------------------------ policy


def device_json(d: t.Device) -> dict:
    return {"device_id": d.device_id, "kind": d.kind,
            "public_key_spki_b64": b64(d.public_key_spki_der), "revoked_at": d.revoked_at}


def base_context(now: int) -> t.GateContext:
    return t.GateContext(
        now=now,
        devices={
            1: t.Device(1, t.DEVICE_QUARRY_PHONE, spki("quarry-1")),
            2: t.Device(2, t.DEVICE_QUARRY_PHONE, spki("quarry-2")),
            3: t.Device(3, t.DEVICE_GATE_STATION, spki("gate-1")),
        },
        trucks={5: t.Truck(5), 6: t.Truck(6), 8: t.Truck(8, active=False)},
    )


def ctx_json(c: t.GateContext) -> dict:
    return {
        "now": c.now,
        "policy": dataclasses.asdict(c.policy),
        "devices": [device_json(d) for d in c.devices.values()],
        "trucks": [dataclasses.asdict(tr) for tr in c.trucks.values()],
        "used_ticket_ids": sorted(c.used_ticket_ids),
        "voided_ticket_ids": sorted(c.voided_ticket_ids),
    }


def policy_vectors() -> dict:
    now = T0 + 900  # 15 min after issue: the normal case
    q1 = lambda **kw: t.to_qr_text(t.build_ticket(KEYS["quarry-1"], fields(**kw)))
    normal = t.build_ticket(KEYS["quarry-1"], fields())

    def case(name, qr, mutate=None, at=now):
        c = base_context(at)
        if mutate:
            mutate(c)
        r = t.evaluate(qr, c)
        return {"name": name, "qr_text": qr, "context": ctx_json(c),
                "expected": {"outcome": r.outcome, "reasons": r.reasons,
                             "notes": r.notes, "ticket_id": r.ticket_id}}

    def used(*ids):
        return lambda c: c.used_ticket_ids.update(ids)

    def revoke(dev, at):
        def m(c): c.devices[dev].revoked_at = at
        return m

    def last_entry(truck, at):
        def m(c): c.trucks[truck].last_entry_at = at
        return m

    gate_key_ticket = t.to_qr_text(t.build_ticket(KEYS["gate-1"], fields(device_id=3)))
    reserved = bytearray(normal[:t.SIGNED_LEN]); reserved[28] = 0x20

    cases = [
        case("accept_normal", t.to_qr_text(normal)),
        case("accept_with_driver_changed_note", q1(flags=t.FLAG_DRIVER_CHANGED)),
        case("accept_exactly_min_travel", q1(), at=T0 + 600),
        case("accept_exactly_max_validity", q1(), at=T0 + 5400),
        case("pending_too_early", q1(), at=T0 + 300),
        case("pending_expired", q1(), at=T0 + 6000),
        case("pending_issued_in_future", q1(issued_at=now + 600)),
        case("pending_within_future_tolerance_too_early", q1(issued_at=now + 60)),
        case("pending_no_gps", q1(flags=t.FLAG_NO_GPS, lat_e6=0, lon_e6=0)),
        case("pending_clock_mismatch", q1(flags=t.FLAG_CLOCK_MISMATCH, clock_delta=900)),
        case("pending_outside_geofence", q1(flags=t.FLAG_OUTSIDE_GEOFENCE)),
        case("pending_multiple_reasons", q1(flags=t.FLAG_NO_GPS | t.FLAG_DRIVER_CHANGED), at=T0 + 6000),
        case("pending_unknown_truck", q1(truck_id=99)),
        case("pending_inactive_truck", q1(truck_id=8)),
        case("reject_already_used", t.to_qr_text(normal), used("Q1-000482")),
        case("reject_malleated_copy_of_used_ticket", t.to_qr_text(malleate(normal)), used("Q1-000482")),
        case("reject_voided", t.to_qr_text(normal),
             lambda c: c.voided_ticket_ids.add("Q1-000482")),
        case("reject_truck_gap", t.to_qr_text(normal), last_entry(5, now - 600)),
        case("accept_truck_gap_exactly_min", t.to_qr_text(normal), last_entry(5, now - 1200)),
        case("reject_device_revoked", t.to_qr_text(t.build_ticket(KEYS["quarry-2"], fields(device_id=2))),
             revoke(2, T0 - 60)),
        case("pending_issued_before_revocation",
             t.to_qr_text(t.build_ticket(KEYS["quarry-2"], fields(device_id=2))), revoke(2, T0 + 60)),
        case("reject_unknown_device", t.to_qr_text(t.build_ticket(KEYS["attacker"], fields(device_id=7)))),
        case("reject_forged_claiming_device_1", t.to_qr_text(t.build_ticket(KEYS["attacker"], fields()))),
        case("reject_tampered", t.to_qr_text(bytes(normal[:15]) + bytes([normal[15] ^ 1]) + normal[16:])),
        case("reject_gate_station_key", gate_key_ticket),
        case("reject_decapage_kind", q1(kind=t.KIND_DECAPAGE)),
        case("reject_reserved_flag", t.to_qr_text(raw_signed("quarry-1", bytes(reserved)))),
        case("reject_malformed", "TG1:NOT-A-TICKET"),
        case("reject_combined_used_and_too_early", t.to_qr_text(normal), used("Q1-000482"), at=T0 + 300),
    ]
    return {
        "description": "Gate policy vectors. Each case carries its full context. Implementations must return "
                       "the same outcome, reasons (same order) and notes. Check order is normative (SPEC §6).",
        "cases": cases,
    }


# ------------------------------------------------------------------ void records (§8)


def void_vectors() -> dict:
    cases = []
    for name, tid, at, reason in [
        ("latin", "Q1-000482", T0 + 300, "printer jam"),
        ("arabic", "Q1-000483", T0 + 900, "الشاحنة تعطلت"),
        ("trimmed", "Q2-000001", T0 + 60, "  wrong truck  "),
    ]:
        msg = t.void_message(tid, at, reason)
        cases.append({
            "name": name, "ticket_id": tid, "at": at, "reason": reason,
            "message_hex": msg.hex(),
            "signature_hex": t.sign(KEYS["quarry-1"], msg).hex(),
            "signer": "quarry-1",
            "valid_for": {"quarry-1": True, "quarry-2": False},
        })
    return {
        "description": "Void record: TGVOID1|ticket id|unix s|reason (trimmed, UTF-8), signed by the ticket key",
        "public_keys_spki_b64": {k: b64(spki(k)) for k in ("quarry-1", "quarry-2")},
        "cases": cases,
    }


# ------------------------------------------------------------------ gate receipts (§9)


def receipt_vectors() -> dict:
    def rf(**kw) -> t.ReceiptFields:
        base = dict(trip_id=123, gate_id=1, entered_at=T0 + 1200, truck_id=5, ticket_device_id=1,
                    ticket_seq=482, load=t.LOAD_FULL)
        base.update(kw)
        return t.ReceiptFields(**base)

    valid = []
    for name, f in [
        ("basic", rf()),
        ("partial_load", rf(trip_id=4_000_000_000, load=t.LOAD_PARTIAL, truck_id=65535, ticket_seq=4_294_967_295)),
        ("unknown_load", rf(trip_id=1, gate_id=2, load=t.LOAD_UNKNOWN)),
    ]:
        r = t.build_receipt(KEYS["server-1"], f)
        valid.append({
            "name": name, "fields": dataclasses.asdict(f), "trip_number": f.trip_number,
            "receipt_hex": r.hex(), "qr_text": t.receipt_to_qr_text(r),
        })

    good = t.build_receipt(KEYS["server-1"], rf())
    forged = t.build_receipt(KEYS["attacker"], rf())
    signed = good[:t.RECEIPT_SIGNED_LEN]
    malformed = [
        ("ticket_prefix", "TG1:" + t.base45_encode(good)),
        ("too_short", t.receipt_to_qr_text(good[:-1])),
        ("too_long", t.receipt_to_qr_text(good + b"\x00")),
        ("bad_version", t.receipt_to_qr_text(bytes([2]) + good[1:])),
        ("ticket_kind", t.receipt_to_qr_text(bytes([1, t.KIND_TRANSPORT]) + good[2:])),
        ("bad_load", t.receipt_to_qr_text(signed[:-1] + bytes([4]) + good[t.RECEIPT_SIGNED_LEN:])),
        ("lowercase", t.receipt_to_qr_text(good).lower()),
    ]
    for _, qr in malformed:
        try:
            t.decode_receipt(t.receipt_from_qr_text(qr))
            raise AssertionError("malformed receipt decoded")
        except t.TicketFormatError:
            pass
    return {
        "description": "Gate receipt TR1: 85 bytes, signed by the server key. Decoders must reject the malformed cases.",
        "server_key_spki_b64": b64(spki("server-1")),
        "valid": valid,
        "forged": {"qr_text": t.receipt_to_qr_text(forged), "signer": "attacker"},
        "malformed": [{"name": n, "qr_text": q} for n, q in malformed],
    }


# ------------------------------------------------------------------ main


def main() -> int:
    files = {
        "codec.json": codec_vectors(),
        "signature.json": signature_vectors(),
        "malformed.json": malformed_vectors(),
        "policy.json": policy_vectors(),
        "void.json": void_vectors(),
        "receipt.json": receipt_vectors(),
    }
    check = "--check" in sys.argv
    OUT.mkdir(parents=True, exist_ok=True)
    stale = []
    for name, data in files.items():
        text = json.dumps(data, indent=2, ensure_ascii=False) + "\n"
        path = OUT / name
        if check:
            if not path.exists() or path.read_text(encoding="utf-8") != text:
                stale.append(name)
        else:
            path.write_text(text, encoding="utf-8")
    if stale:
        print("Out-of-date vector files:", ", ".join(stale))
        return 1
    print("check OK" if check else f"wrote {len(files)} files to {OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
