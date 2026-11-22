# TruckGate

Signed trip tickets, gate verification and camera evidence for quarry-to-factory truck transport.
Offline-first, tamper-evident, built to be sold as a standalone industrial product.

نظام لإدارة رحلات نقل الطين من المقلع إلى المصنع: تذاكر موقّعة رقمياً لا يمكن تزويرها أو تكرارها،
تحقق عند بوابة المصنع، ولقطات تلقائية من الكاميرات كدليل وكبيانات لتدريب الذكاء الاصطناعي لاحقاً.

## Repository layout

| Path | What |
|---|---|
| `ticket-spec/` | **Normative ticket format** (`SPEC.md`), Python reference, shared test vectors |
| `quarry-app/` | Kotlin. `ticket-format/` = pure-JVM ticket encoding and signing (used by the Android app) |
| `server/` | .NET 10. `TruckGate.Crypto` = ticket decoding, signature verification, gate policy |
| `docs/DECISIONS.md` | Everything agreed so far: rules, parameters, technology, plan |

## Status

Step 1 of 7 — ticket core. See `docs/DECISIONS.md` for the plan.

## Running the checks

```bash
# Regenerate / check the shared vectors (Python 3.11+)
cd ticket-spec/reference && pip install -r requirements.txt && python gen_vectors.py --check

# Kotlin
cd quarry-app && ./gradlew :ticket-format:test

# C#
cd server && dotnet test tests/TruckGate.Crypto.Tests
```

All three implementations must pass the same vectors. CI runs them on every pull request.
