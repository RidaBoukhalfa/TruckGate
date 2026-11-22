"""
TruckGate ticket format — Python REFERENCE implementation (spec v1).

This module is the executable form of ticket-spec/SPEC.md. It is used to
generate the shared test vectors that the Kotlin (quarry app) and C# (server)
implementations must reproduce byte-for-byte. It is NOT deployed anywhere.

Requires: cryptography >= 44 (deterministic ECDSA for reproducible vectors).
"""
from __future__ import annotations

import hashlib
import struct
from dataclasses import dataclass, field
from typing import Optional

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.asymmetric.utils import (
    decode_dss_signature,
    encode_dss_signature,
)

# ---------------------------------------------------------------- constants

FORMAT_VERSION = 1
KIND_TRANSPORT = 0x01
KIND_DECAPAGE = 0x02  # reserved for a future version, never accepted at the gate
KNOWN_KINDS = (KIND_TRANSPORT, KIND_DECAPAGE)

FLAG_NO_GPS = 0x01
FLAG_OUTSIDE_GEOFENCE = 0x02
FLAG_DRIVER_CHANGED = 0x04
FLAG_CLOCK_MISMATCH = 0x08
FLAGS_RESERVED_MASK = 0xF0

SIGNED_LEN = 63
SIGNATURE_LEN = 64
TICKET_LEN = SIGNED_LEN + SIGNATURE_LEN  # 127

QR_PREFIX = "TG1:"
BASE45_ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:"
CROCKFORD32 = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

# P-256 group order, used only to build the malleated-signature test vector.
P256_N = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551


class TicketFormatError(ValueError):
    pass


# ---------------------------------------------------------------- model


@dataclass(frozen=True)
class TicketFields:
    kind: int
    device_id: int
    seq: int
    issued_at: int          # unix seconds, UTC (GPS time when available)
    clock_delta: int        # phone clock minus GPS time, seconds (i16)
    truck_id: int
    driver_id: int
    operator_id: int
    lat_e6: int             # latitude  * 1e6 (i32), 0 when NO_GPS
    lon_e6: int             # longitude * 1e6 (i32), 0 when NO_GPS
    flags: int
    config_version: int
    photo_sha256: bytes     # 32 bytes

    @property
    def ticket_id(self) -> str:
        return f"Q{self.device_id}-{self.seq:06d}"


# ---------------------------------------------------------------- codec

_LAYOUT = struct.Struct(">BBHIIhHHHiiBH32s")  # 63 bytes
assert _LAYOUT.size == SIGNED_LEN


def encode_signed_part(f: TicketFields) -> bytes:
    if len(f.photo_sha256) != 32:
        raise TicketFormatError("photo_sha256 must be 32 bytes")
    if f.flags & FLAGS_RESERVED_MASK:
        raise TicketFormatError("reserved flag bits set")
    if f.kind not in KNOWN_KINDS:
        raise TicketFormatError("unknown kind")
    return _LAYOUT.pack(
        FORMAT_VERSION, f.kind, f.device_id, f.seq, f.issued_at, f.clock_delta,
        f.truck_id, f.driver_id, f.operator_id, f.lat_e6, f.lon_e6, f.flags,
        f.config_version, f.photo_sha256,
    )


def decode(ticket: bytes) -> tuple[TicketFields, bytes, bytes]:
    """Returns (fields, signed_part, signature). Raises TicketFormatError."""
    if len(ticket) != TICKET_LEN:
        raise TicketFormatError(f"length {len(ticket)} != {TICKET_LEN}")
    signed, sig = ticket[:SIGNED_LEN], ticket[SIGNED_LEN:]
    (ver, kind, dev, seq, issued, delta, truck, driver, op,
     lat, lon, flags, cfg, photo) = _LAYOUT.unpack(signed)
    if ver != FORMAT_VERSION:
        raise TicketFormatError(f"unsupported version {ver}")
    if kind not in KNOWN_KINDS:
        raise TicketFormatError(f"unknown kind {kind}")
    if flags & FLAGS_RESERVED_MASK:
        raise TicketFormatError("reserved flag bits set")
    return (
        TicketFields(kind, dev, seq, issued, delta, truck, driver, op,
                     lat, lon, flags, cfg, photo),
        signed,
        sig,
    )


# ---------------------------------------------------------------- base45 (RFC 9285)


def base45_encode(data: bytes) -> str:
    out = []
    for i in range(0, len(data) - 1, 2):
        n = data[i] * 256 + data[i + 1]
        c, n = n % 45, n // 45
        d, e = n % 45, n // 45
        out += [BASE45_ALPHABET[c], BASE45_ALPHABET[d], BASE45_ALPHABET[e]]
    if len(data) % 2:
        n = data[-1]
        out += [BASE45_ALPHABET[n % 45], BASE45_ALPHABET[n // 45]]
    return "".join(out)


def base45_decode(text: str) -> bytes:
    vals = []
    for ch in text:
        idx = BASE45_ALPHABET.find(ch)
        if idx < 0:
            raise TicketFormatError("invalid base45 character")
        vals.append(idx)
    if len(vals) % 3 == 1:
        raise TicketFormatError("invalid base45 length")
    out = bytearray()
    for i in range(0, len(vals) - 2, 3):
        n = vals[i] + vals[i + 1] * 45 + vals[i + 2] * 45 * 45
        if n > 0xFFFF:
            raise TicketFormatError("base45 triplet overflow")
        out += bytes([n >> 8, n & 0xFF])
    if len(vals) % 3 == 2:
        n = vals[-2] + vals[-1] * 45
        if n > 0xFF:
            raise TicketFormatError("base45 pair overflow")
        out.append(n)
    return bytes(out)


def to_qr_text(ticket: bytes) -> str:
    return QR_PREFIX + base45_encode(ticket)


def from_qr_text(text: str) -> bytes:
    if not text.startswith(QR_PREFIX):
        raise TicketFormatError("missing TG1: prefix")
    return base45_decode(text[len(QR_PREFIX):])


# ---------------------------------------------------------------- short code


def short_code(ticket: bytes) -> str:
    """First 30 bits of SHA-256(ticket) as 6 Crockford base32 chars, 'XXX-XXX'."""
    h = hashlib.sha256(ticket).digest()
    v = int.from_bytes(h[:4], "big") >> 2  # 30 bits
    chars = [CROCKFORD32[(v >> (25 - 5 * i)) & 0x1F] for i in range(6)]
    return "".join(chars[:3]) + "-" + "".join(chars[3:])


# ---------------------------------------------------------------- signatures


def der_to_p1363(der: bytes) -> bytes:
    r, s = decode_dss_signature(der)
    return r.to_bytes(32, "big") + s.to_bytes(32, "big")


def p1363_to_der(raw: bytes) -> bytes:
    if len(raw) != 64:
        raise TicketFormatError("signature must be 64 bytes")
    return encode_dss_signature(int.from_bytes(raw[:32], "big"), int.from_bytes(raw[32:], "big"))


def sign(private_key: ec.EllipticCurvePrivateKey, signed_part: bytes) -> bytes:
    der = private_key.sign(signed_part, ec.ECDSA(hashes.SHA256(), deterministic_signing=True))
    return der_to_p1363(der)


def build_ticket(private_key: ec.EllipticCurvePrivateKey, f: TicketFields) -> bytes:
    signed = encode_signed_part(f)
    return signed + sign(private_key, signed)


def verify_signature(public_key_spki_der: bytes, signed_part: bytes, sig: bytes) -> bool:
    try:
        pub = serialization.load_der_public_key(public_key_spki_der)
        if not isinstance(pub, ec.EllipticCurvePublicKey) or pub.curve.name != "secp256r1":
            return False
        pub.verify(p1363_to_der(sig), signed_part, ec.ECDSA(hashes.SHA256()))
        return True
    except (InvalidSignature, ValueError, TicketFormatError):
        return False


def spki_der(public_key: ec.EllipticCurvePublicKey) -> bytes:
    return public_key.public_bytes(
        serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo
    )


# ---------------------------------------------------------------- gate policy

DEVICE_QUARRY_PHONE = "QUARRY_PHONE"
DEVICE_GATE_STATION = "GATE_STATION"


@dataclass
class Device:
    device_id: int
    kind: str
    public_key_spki_der: bytes
    revoked_at: Optional[int] = None


@dataclass
class Truck:
    truck_id: int
    active: bool = True
    last_entry_at: Optional[int] = None


@dataclass
class Policy:
    min_travel_s: int = 600        # 10 min
    max_validity_s: int = 5400     # 90 min
    min_truck_gap_s: int = 1200    # 20 min
    future_tolerance_s: int = 120  # 2 min


@dataclass
class GateContext:
    now: int
    devices: dict[int, Device]
    trucks: dict[int, Truck]
    used_ticket_ids: set[str] = field(default_factory=set)
    voided_ticket_ids: set[str] = field(default_factory=set)
    policy: Policy = field(default_factory=Policy)


@dataclass
class GateResult:
    outcome: str                      # ACCEPT | PENDING | REJECT
    reasons: list[str]                # REJECT/PENDING reason codes, in check order
    notes: list[str]                  # informational codes (do not block)
    ticket_id: Optional[str] = None


def evaluate(qr_text: str, ctx: GateContext) -> GateResult:
    """Gate decision for a scanned transport ticket. Order of checks is normative."""
    # 1. structure
    try:
        f, signed, sig = decode(from_qr_text(qr_text))
    except TicketFormatError:
        return GateResult("REJECT", ["MALFORMED"], [])
    tid = f.ticket_id
    # 2. device
    dev = ctx.devices.get(f.device_id)
    if dev is None:
        return GateResult("REJECT", ["UNKNOWN_DEVICE"], [], tid)
    if dev.kind != DEVICE_QUARRY_PHONE:
        return GateResult("REJECT", ["WRONG_DEVICE_KIND"], [], tid)
    # 3. signature
    if not verify_signature(dev.public_key_spki_der, signed, sig):
        return GateResult("REJECT", ["BAD_SIGNATURE"], [], tid)
    # 4. kind
    if f.kind != KIND_TRANSPORT:
        return GateResult("REJECT", ["WRONG_KIND"], [], tid)

    reject: list[str] = []
    pending: list[str] = []
    notes: list[str] = []

    # 5. revocation
    if dev.revoked_at is not None:
        if f.issued_at >= dev.revoked_at:
            reject.append("DEVICE_REVOKED")
        else:
            pending.append("ISSUED_BEFORE_REVOCATION")
    # 6. single use — keyed by ticket ID, never by bytes (ECDSA malleability)
    if tid in ctx.used_ticket_ids:
        reject.append("ALREADY_USED")
    if tid in ctx.voided_ticket_ids:
        reject.append("VOIDED")
    # 7. truck
    truck = ctx.trucks.get(f.truck_id)
    if truck is None or not truck.active:
        pending.append("UNKNOWN_TRUCK")
    elif truck.last_entry_at is not None and ctx.now - truck.last_entry_at < ctx.policy.min_truck_gap_s:
        reject.append("TRUCK_GAP")
    # 8. time window
    age = ctx.now - f.issued_at
    if f.issued_at > ctx.now + ctx.policy.future_tolerance_s:
        pending.append("ISSUED_IN_FUTURE")
    elif age < ctx.policy.min_travel_s:
        pending.append("TOO_EARLY")
    elif age > ctx.policy.max_validity_s:
        pending.append("EXPIRED")
    # 9. ticket flags
    if f.flags & FLAG_NO_GPS:
        pending.append("NO_GPS")
    if f.flags & FLAG_OUTSIDE_GEOFENCE:
        pending.append("OUTSIDE_GEOFENCE")
    if f.flags & FLAG_CLOCK_MISMATCH:
        pending.append("CLOCK_MISMATCH")
    if f.flags & FLAG_DRIVER_CHANGED:
        notes.append("DRIVER_CHANGED")

    if reject:
        return GateResult("REJECT", reject, notes, tid)
    if pending:
        return GateResult("PENDING", pending, notes, tid)
    return GateResult("ACCEPT", [], notes, tid)


# ---------------------------------------------------------------- test keys


def test_private_key(label: str) -> ec.EllipticCurvePrivateKey:
    """Deterministic TEST-ONLY key derived from a label. Never use in production."""
    scalar = int.from_bytes(hashlib.sha256(("truckgate-test-key:" + label).encode()).digest(), "big")
    scalar = scalar % (P256_N - 1) + 1
    return ec.derive_private_key(scalar, ec.SECP256R1())



# ---------------------------------------------------------------- void record (§8)


def void_message(ticket_id: str, at: int, reason: str) -> bytes:
    """Text signed by the ticket key to void a ticket: TGVOID1|<ticket id>|<unix s>|<reason>."""
    if "|" in ticket_id or not reason.strip():
        raise TicketFormatError("invalid void record")
    return f"TGVOID1|{ticket_id}|{at}|{reason.strip()}".encode("utf-8")


# ---------------------------------------------------------------- gate receipt (§9)

RECEIPT_KIND = 0x10
RECEIPT_SIGNED_LEN = 21
RECEIPT_LEN = RECEIPT_SIGNED_LEN + SIGNATURE_LEN  # 85
RECEIPT_PREFIX = "TR1:"
LOAD_UNKNOWN, LOAD_FULL, LOAD_PARTIAL, LOAD_EMPTY = 0, 1, 2, 3

_RECEIPT_LAYOUT = struct.Struct(">BBIHIHHIB")  # 21 bytes
assert _RECEIPT_LAYOUT.size == RECEIPT_SIGNED_LEN


@dataclass(frozen=True)
class ReceiptFields:
    trip_id: int            # u32
    gate_id: int            # u16
    entered_at: int         # unix seconds, server time of the entry scan
    truck_id: int           # u16
    ticket_device_id: int   # u16
    ticket_seq: int         # u32
    load: int               # 0 unknown, 1 full, 2 partial, 3 empty

    @property
    def trip_number(self) -> str:
        return f"T-{self.trip_id:06d}"


def encode_receipt_signed_part(r: ReceiptFields) -> bytes:
    if r.load not in (LOAD_UNKNOWN, LOAD_FULL, LOAD_PARTIAL, LOAD_EMPTY):
        raise TicketFormatError("unknown load value")
    return _RECEIPT_LAYOUT.pack(FORMAT_VERSION, RECEIPT_KIND, r.trip_id, r.gate_id, r.entered_at,
                                r.truck_id, r.ticket_device_id, r.ticket_seq, r.load)


def build_receipt(server_key: ec.EllipticCurvePrivateKey, r: ReceiptFields) -> bytes:
    signed = encode_receipt_signed_part(r)
    return signed + sign(server_key, signed)


def decode_receipt(receipt: bytes) -> tuple[ReceiptFields, bytes, bytes]:
    if len(receipt) != RECEIPT_LEN:
        raise TicketFormatError(f"length {len(receipt)} != {RECEIPT_LEN}")
    signed, sig = receipt[:RECEIPT_SIGNED_LEN], receipt[RECEIPT_SIGNED_LEN:]
    ver, kind, trip, gate, entered, truck, dev, seq, load = _RECEIPT_LAYOUT.unpack(signed)
    if ver != FORMAT_VERSION:
        raise TicketFormatError(f"unsupported version {ver}")
    if kind != RECEIPT_KIND:
        raise TicketFormatError(f"not a receipt (kind {kind})")
    if load > LOAD_EMPTY:
        raise TicketFormatError("unknown load value")
    return ReceiptFields(trip, gate, entered, truck, dev, seq, load), signed, sig


def receipt_to_qr_text(receipt: bytes) -> str:
    return RECEIPT_PREFIX + base45_encode(receipt)


def receipt_from_qr_text(text: str) -> bytes:
    if not text.startswith(RECEIPT_PREFIX):
        raise TicketFormatError("missing TR1: prefix")
    return base45_decode(text[len(RECEIPT_PREFIX):])
