# TruckGate

Signed trip tickets, gate verification and camera evidence for quarry-to-factory truck transport.
Offline-first, tamper-evident, built to be sold as a standalone industrial product.

نظام لإدارة رحلات نقل الطين من المقلع إلى المصنع: تذاكر موقّعة رقمياً لا يمكن تزويرها أو تكرارها،
تحقق عند بوابة المصنع، ولقطات تلقائية من الكاميرات كدليل وكبيانات لتدريب الذكاء الاصطناعي لاحقاً.

## Repository layout

| Path | What |
|---|---|
| `ticket-spec/` | **Normative ticket format** (`SPEC.md`), Python reference, shared test vectors |
| `quarry-app/` | Kotlin. `ticket-format/` ticket encoding and signing, `quarry-core/` issuance rules, geofence, ESC/POS printing, enrollment; `app/` the Android quarry app |
| `server/` | .NET 10. `TruckGate.Crypto` ticket verification and gate policy, `TruckGate.Data` PostgreSQL schema and migrations, `TruckGate.Application` services (gate, cameras, auth, admin, audit), `TruckGate.Api` the HTTP server |
| `web/` | React + TypeScript gate station and administration UI (Arabic / French), built into the server |
| `deploy/` | One-time database setup |
| `docs/DECISIONS.md` | Everything agreed so far: rules, parameters, technology, plan |
| `docs/GATE_STATION.md` | Installation, camera discovery and selection, daily use at the gate |

## Status

Steps 1–4 of 7: ticket core, server core, quarry app, gate station with camera discovery.
Not built yet: exit scan and receipt, supervisor review, alerts, phone sync. See `docs/DECISIONS.md`.

## Running the checks

```bash
# Regenerate / check the shared vectors (Python 3.11+)
cd ticket-spec/reference && pip install -r requirements.txt && python gen_vectors.py --check

# Kotlin (+ debug APK)
cd quarry-app && ./gradlew :ticket-format:test :quarry-core:test :app:assembleDebug

# C# (server tests need PostgreSQL 16; they create throw-away databases)
export TRUCKGATE_TEST_PG='Host=localhost;Username=postgres;Password=postgres'
cd server && dotnet test

# Web UI (type check + build into server/src/TruckGate.Api/wwwroot)
cd web && npm ci && npm run build
```

All three implementations must pass the same vectors. CI runs them on every pull request.
