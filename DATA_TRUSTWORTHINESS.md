# Data Trustworthiness Tracker

**Overall trust level: LOW**

Reference this document when selecting data for algorithm cracking. Each era has different guarantees about what fields are reliable.

## Data Eras

### Era 0: Manual Collection (before 2026-03-07)
**Period**: Unknown – 2026-03-07T03:20 UTC
**Rows**: 0 in DB (data exists in spreadsheets/notes only — ~211 samples)
**Fields available**: Gate name, slot (manually recorded)
**Missing**: source, structured fields, course variant
**Trust**: USABLE FOR GATE TYPE ONLY — sufficient to analyze which GATE *type* appears in which slot, but no course variant info. Cannot distinguish AFO Gold Saucer from AFO Cieldalaes Cliff, etc.
**Use for**: Slot pool analysis (which types appear at :00/:20/:40), frequency distribution

### Era 1: Early Plugin → API (2026-03-07 – 2026-03-11)
**Period**: 2026-03-07T03:20 – 2026-03-11T08:00 UTC (server maintenance)
**Rows**: ~245 (source=NULL, pre-source-tracking)
**Fields available**: world, gate, slot, cycle_time, reported_at, reporter_ip_hash
**Missing**: source, gate_type_byte, position_type, flags, raw_text
**Known issues**:
- No `source` field — all rows have NULL source
- Rate limiter was shared between /gate and /event — some reports may have been silently 429'd
- Slot computed from `GetNextGateTime()` — correct for predictions but wrong if any were director-sourced (unlikely, director detection didn't exist yet)
**Trust**: USABLE FOR GATE TYPE + SLOT — gate name and slot minute are reliable. No course variant data.
**Use for**: GATE type sequence analysis, slot pool validation, frequency stats

### Era 2: Post-Maintenance, Source Tracking Added (2026-03-11 – 2026-03-12T05:45 UTC)
**Period**: 2026-03-11T08:00 – 2026-03-12T05:45 UTC
**Rows**: ~17 (sources: npc_gate_keeper, memory_director)
**Fields available**: world, gate, slot, source, reported_at
**Missing**: gate_type_byte, position_type, flags (schema not yet migrated)
**Known issues**:
- Server PRNG re-seeded at maintenance boundary (confirmed sequence break: NPC predicted AFO, server announced LoF)
- `memory_director` entries have NULL structured fields (columns didn't exist yet)
- Rate limiter collision bug still active
**Trust**: USABLE FOR GATE TYPE + SLOT — same as Era 1 but with source attribution. First row after maintenance marks new PRNG seed.
**Use for**: GATE type sequence from new seed, sequence break detection baseline

### Era 3: Schema Migration + Bug Fixes In Progress (2026-03-12T05:45 – 06:25 UTC)
**Period**: 2026-03-12T05:45 – 2026-03-12T06:25 UTC
**Rows**: ~3
**Fields available**: world, gate, slot, source (structured fields existed but weren't populated)
**Known issues**:
- Structured fields columns added but `memory_director` POST was still being skipped by duplicate check (fix deployed ~05:45 but only partially working)
- `memory_director` slot bug: used `GetNextGateTime()` instead of `GetCurrentGateTime()` — one bad row (SIR slot=40, should be 20) was manually deleted
- Rate limiter split deployed ~05:45
**Trust**: LOW — transitional period, bugs being fixed mid-stream. Gate name + slot OK, structured fields unreliable.
**Use for**: Probably skip this era for algorithm work

### Era 4: All Known Bugs Fixed (2026-03-12T06:25 UTC onward)
**Period**: 2026-03-12T06:25 UTC – present
**Rows**: 0 so far (pipeline just fixed, awaiting first clean data)
**Fields available**: world, gate, slot, source, gate_type_byte, position_type, flags, raw_text
**Known issues**:
- Zero confirmed clean rows yet — trust is theoretical until verified
- Dedup resolved at 06:39 UTC (version 7) — structured fields now upserted
**Trust**: MEDIUM — all known bugs fixed but unverified. Will upgrade to HIGH after 24h clean collection.
**Use for**: Full algorithm cracking (gate type + course variant via position_type)

## Version Change Log

Exact deploy timestamps for determining which code produced a given DB row. Compare a row's `reported_at` against these timestamps to know what bugs were active.

| # | Deployed (UTC) | Component | Change | Impact on Data |
|---|----------------|-----------|--------|----------------|
| 1 | 2026-03-07 ~03:00 | API + Plugin | Initial deploy. POST /gate, basic gate+slot+world. No source field. | Rows have NULL source. Slot via `GetNextGateTime` (correct for chat predictions). |
| 2 | 2026-03-10 ~08:00 | Plugin | Added `source` field to POST body, NPC gate keeper detection, memory director detection. | Rows now have source. Director detection fires but structured fields not in schema yet. |
| 3 | 2026-03-11 ~08:00 | Server | FFXIV maintenance. Server PRNG re-seeded. | Sequence break at boundary (NPC predicted AFO, server announced LoF). New PRNG seed starts here — all prior sequence data is from old seed. |
| 4 | 2026-03-12 ~05:45 | API | DB schema migration: added `gate_type_byte`, `position_type`, `flags` columns. Split rate limiters. Reduced /gate rate limit 10s→2s. | Structured field columns exist but plugin not yet sending them correctly. |
| 5 | 2026-03-12 ~05:45 | Plugin | Added structured fields to `ReportGate()`. Moved director API POST before duplicate skip check. | Director POST now fires, but `GetNextGateTime` slot bug still present. `chat_underway` still wins race → dedup drops director row. |
| 6 | 2026-03-12 ~06:25 | Plugin | Fixed director slot: `GetNextGateTime` → `GetCurrentGateTime`. | Slot values now correct for director source. One bad row (SIR slot=40) manually deleted. |
| 7 | 2026-03-12 06:39 | API | Upsert: `INSERT OR IGNORE` → `ON CONFLICT DO UPDATE` for structured fields. SQLite write moved outside `gateStore.TryAdd` gate so upsert always runs. | Structured fields now survive even when `chat_underway` wins the INSERT race. Dedup problem resolved. |
| 8 | 2026-03-12 07:05 | API | Gate rate limit 2s → 0.5s. Previous 2s window blocked `memory_director` arriving ~200ms after `chat_underway`. | Both POSTs now land, upsert merges structured fields. |
| 9 | 2026-03-12 07:08 | Plugin | Removed `chat_underway` API POST. Director is sole POST source (has structured fields). Chat path still handles local state/alerts. | Single POST per GATE, no more race condition or rate limit issues. |

**How to use**: For any DB row, find the latest version # where `deployed < reported_at`. That version's "Impact on Data" tells you what to trust about that row.

## Dedup Problem — RESOLVED (version 7, 2026-03-12 06:39 UTC)

The DB UNIQUE constraint is on `(world, cycle_time)` — one row per world per 20-min cycle. Previously used `INSERT OR IGNORE`, so if `chat_underway` won the race, `memory_director`'s structured fields were silently dropped.

**Fix**: Changed to `ON CONFLICT DO UPDATE` with `COALESCE` — second report fills in structured fields without overwriting existing data. Source field gets comma-appended (e.g., `"chat_underway,memory_director"`).

## Data Selection Guide for Algorithm Cracking

| Question | Usable Eras | Min Fields Needed |
|----------|-------------|-------------------|
| What GATE type runs in each slot? | 0, 1, 2, 4 | gate, slot |
| What is the GATE sequence since last maintenance? | 2, 3, 4 | gate, slot, reported_at |
| What course variant runs? | 4 only | position_type, gate_type_byte |
| Is the PRNG output the GATE type, course, or both? | 4 only | position_type + gate type correlation |

## What "HIGH" Trust Requires

- [ ] Multiple GATE cycles with correct structured fields confirmed in DB
- [x] Dedup issue resolved — structured fields now upserted (version 7)
- [ ] At least one instance of each GATE type with correct pos/byte values
- [ ] No new bugs discovered for 24+ hours of continuous collection
