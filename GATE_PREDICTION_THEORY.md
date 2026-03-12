# GATE Prediction Theory: Deterministic Hash Function

## Hypothesis

GATEs are not random — they are determined by a **deterministic hash function** seeded on time, identical to how FFXIV's weather system works.

## Confirmed Facts

### Globally Identical Across All Regions
- **NA**: Tested Siren (Aether), Sargatanas (Aether), Ultros (Primal), Leviathan (Primal), Kraken (Dynamis)
- **OCE**: Tested Sephirot (Materia)
- **Result**: All report the same GATE at the same slot. GATEs are globally deterministic — no region or DC parameter.

### Fixed Slot Pools

From 211+ samples (pre-fix data, may contain errors):

| Slot | Pool | Distribution |
|------|------|-------------|
| :00  | Leap of Faith, Air Force One, Cliffhanger | 37.9% / 34.8% / 27.3% |
| :20  | Any Way the Wind Blows, The Slice Is Right, Air Force One | 43.8% / 34.2% / 21.9% |
| :40  | Air Force One, Leap of Faith, **The Slice Is Right** | ~80% / ~15% / ~5% |

**Note:** :40 was originally thought to be 2 options (AFO/LoF), but post-fix clean data confirmed SIR also appears at :40. All 3 slots have 3 GATEs.

### GFateRoulette Weights (from game data)
Row weights: `[0, 10, 5, 5, 5, 5, 5, 5, 5, 0, 0]` — one GATE type gets double weight. This likely explains the heavy AFO skew at :40.

### No Repeating Period Detected
- Tested periods of 3, 6, 9, 12, 18, 24, 36, 72 slots
- :00 and :20 show no significant periodicity
- :40 shows high period scores (~70-83%) but this is just AFO repeating (83% base rate)
- **Conclusion**: Not a simple rotation — it's a hash/PRNG

### Repeats Allowed
- Same GATE can appear back-to-back in the same slot
- :40 repeats 69% of transitions (AFO→AFO dominates)

## The Weather Algorithm (Reference)

FFXIV weather was reverse-engineered from client disassembly (SaintCoinach by Rogueadyn, ~2015). The algorithm is a **degenerate Xorshift128 PRNG**:

```csharp
// Input: unix timestamp (real-world seconds since epoch)
int CalculateTarget(int unixSeconds) {
    var bell = unixSeconds / 175;                           // Eorzea hours
    var increment = ((uint)(bell + 8 - (bell % 8))) % 24;  // 0, 8, or 16
    var totalDays = (uint)(unixSeconds / 4200);             // Eorzea days

    var seed = (totalDays * 100) + increment;

    var step1 = (seed << 11) ^ seed;    // xorshift left 11
    var step2 = (step1 >> 8) ^ step1;   // xorshift right 8

    return (int)(step2 % 100);          // 0-99, mapped to weather rate thresholds
}
```

**Key properties:**
- 1 Eorzean hour (bell) = 175 real seconds
- 1 Eorzean day = 4200 real seconds (70 minutes)
- Weather changes every 8 bells = 1400 seconds (23m 20s)
- Three weather windows per Eorzean day: ET 16:00 (0), ET 00:00 (8), ET 08:00 (16)
- Output 0-99 is compared against cumulative rate thresholds per zone
- It's a single step of Xorshift128 with state initialized to (0,0,0,seed)

---

## Game Data Findings (Approach 2 Results)

**Checked all EXD sheets — no schedule table exists. Selection is computed at runtime.**

Key sheets found:
- **GFATE.csv** — 99 rows defining individual GATE instances/variants (e.g., 32 Leap of Faith courses)
- **GFateRoulette.csv** — Weight table: `[0, 10, 5, 5, 5, 5, 5, 5, 5, 0, 0]`. One type at 2x weight.
- **GFateType.csv** — Maps GateType IDs to GoldSaucerTextData row IDs (e.g., 171→"Air Force One")
- **GFateDirector** (client struct) — Receives active GATE from server, has GateType + EndTimestamp

**GateType enum:** None=0, Cliffhanger=1, VaseOff=2, SkinchangeWeCanBelieveIn=3, TheTimeOfMyLife=4, AnyWayTheWindBlows=5, LeapOfFaith=6, AirForceOne=7, SliceIsRight=8

**Retired GATEs:** The enum includes VaseOff (2), SkinchangeWeCanBelieveIn (3), TheTimeOfMyLife (4) — none observed in current data. The zero-weight rows in GFateRoulette (0, 9, 10) likely correspond to disabled/retired GATEs.

**Theory: Fixed function, patched weights.** The core hash/PRNG function has probably been the same since Gold Saucer launched (patch 2.51, 2015). Each patch updates the weight table — retired GATEs get weight 0, new ones get added. The slot pool assignments (which GATEs appear at :00/:20/:40) may also be per-patch config. This means:
1. We only need to crack the core function once — it doesn't change
2. The weight table is read from game data (`GFateRoulette.csv`) and applied on top
3. Predictions would need updating each patch only if weights change, not the algorithm

**Conclusion:** Selection is likely client-side (weights are shipped in client data, NPC knows instantly, same pattern as weather). The function is probably in `ffxiv_dx11.exe` — wherever the client reads `GFateRoulette` weights, that's the selection function.

---

## Algorithm Cracking Strategy

### Status: Brute-Force Round 1 Complete

**Tested 1,095,000+ configurations — zero perfect matches.**

Ruled out:
- Single-step xorshift with all shift pairs (1-15 x 1-15)
- LCG (multiple common multipliers/increments)
- Simple multiplicative hash
- Seed formulas: `unix/interval`, `unix/interval * mult + offset`, Eorzea time combos
- Epoch offsets: unix epoch, ARR launch, HW, SB, ShB, EW, DT
- Per-slot offsets (add, xor, multiply by slot index)
- Pool sizes 2-16
- Direct mapping (hash % N) and threshold-based mapping

**Best result was 66.4%** — barely above chance given the base distributions. This means the GATE hash is fundamentally different from the weather hash in structure.

---

### Approach 1: Multi-Step PRNG ⬜ NOT YET TRIED

The weather hash is a *degenerate* single-step Xorshift128 (state initialized to 0,0,0,seed). GATEs may use the **full Xorshift128** with non-zero initial state, or multiple rounds:

```
// Full Xorshift128
state = (a, b, c, seed)  // a,b,c could be constants
t = state[3]
s = state[0]
state[3] = state[2]; state[2] = state[1]; state[1] = s
t ^= t << 11; t ^= t >> 8
s ^= s >> 19
state[0] = t ^ s
result = state[0] % mod
```

Try:
- Full Xorshift128 with various initial states (a,b,c as small constants 0-255)
- Two rounds of the degenerate xorshift (hash the hash)
- Xorshift32, Xorshift64 variants

### Approach 2: Game Data Table Lookup ✅ CHECKED — No schedule table exists

Checked all EXD sheets — no GATE schedule table. Selection is computed at runtime server-side.

Key sheets found: GFATE.csv (99 rows of GATE instances), GFateRoulette.csv (weight table), GFateType.csv (type→text mapping). See "Game Data Findings" section above.

### Approach 3: Client Binary Disassembly ✅ DONE — Selection is SERVER-SIDE

Disassembled `ffxiv_dx11.exe` using Python (pefile + capstone). Key findings:

**Confirmed from FFXIVClientStructs IDA data (addresses match current binary):**
- `GoldSaucerManager::Update` (0x140E03740) — reads GateType from existing GFateDirector, does NOT compute selection
- `GoldSaucerManager::HandleGMCommand` (0x140E03090) — sends network packet (opcode 0x21D) to server
- `GetRunningGateType` reads `byte [rax + 0x79E]` confirming struct offsets

**GFateRoulette sheet (ID 378) references found:**
- 23 locations total, 13 clustered at 0x14197xxxx (Lua/scene event handlers)
- Key function at 0x141972350: **data serializer** with triple-nested loop (3×2×10=60 entries)
  - 3 = slot pools (:00, :20, :40)
  - 2 = groups (current + next?)
  - 10 = GATE type entries
  - Data stored at struct offset 0x2024+ in 16-byte entries
- Function 0x1419716C0: writes individual entries from **incoming server data** to the struct

**No selection algorithm exists in the client.** The server computes and sends GATE data. The client stores it at Director struct offsets 0x1FBC-0x2400.

**What the server actually sends:**
1. Pool configuration (60 entries: 3 pools × 2 groups × 10 GATE types) — weight/availability table, NOT a schedule
2. Current/next GATE (3-entry sliding window at handler +0x48, with GateType, GatePositionType, timestamp)
3. Timer countdown value
4. State byte

**Why the NPC knows the next GATE:** The server explicitly sends the next GATE in the 3-entry window. Only 1 upcoming GATE is known to the client at a time — there is no future schedule.

**The /45 magic multipliers found were angle snapping (45°), not GATE weight division.**

**Implication:** The deterministic algorithm is in the server binary, not extractable from the client. Must crack from observed output data.

### Maintenance Boundary Anomaly (2026-03-11) ⚠️ CRITICAL FINDING

**Observed:** At exactly 11:00 PM PST (07:00 UTC) on Mar 10, 2026 — the scheduled maintenance start time — the GATE selection was overridden mid-cycle.

**Timeline:**
1. Before 11 PM PST: GATE Keeper NPC dialogue said the next GATE would be **Air Force One**
2. At exactly 11 PM PST (maintenance start): The system announced **Leap of Faith** instead
3. The GATE Keeper NPC then updated to show the current GATE as Leap of Faith

**Significance:**
1. **The server pre-computes the next GATE and sends it to the client in advance** — the NPC had already received "next = AFO" before the cycle started
2. **At maintenance boundaries, the server overrides this pre-computed value** — it recalculated and chose a different GATE
3. **This proves the GATE selection can be re-seeded or recalculated at specific time boundaries** — the pre-maintenance sequence and post-maintenance sequence are independent
4. **The NPC's "next GATE" is a prediction, not a commitment** — the server can change it right up until announcement time

**Implications for algorithm cracking:**
- Pre-maintenance data (Mar 6-11, ~263 records) represents one continuous PRNG sequence
- Post-maintenance data will be a different sequence (new seed)
- If we observe the SAME gate sequence repeating after a future maintenance with the same patch version, the seed is deterministic (e.g., derived from patch version or maintenance timestamp)
- If different, the seed includes a random component (e.g., server boot time in milliseconds)

### Approach 4: Eorzea Time Alignment ⬜ NOT YET TRIED

GATEs tick on real-world 20-min intervals (:00/:20/:40 UTC), but the *seed* might still use Eorzea time internally. The alignment between Eorzea time and real time drifts continuously — this could explain why simple real-time seeds don't work.

Try:
- Compute the exact Eorzea time at each GATE cycle start
- Use Eorzea hour/day/week as seed components
- The GATE system might use a *different* Eorzea time subdivision than weather (e.g., every 7 bells instead of 8)
- Check if GATE cycles align with any Eorzea calendar pattern

### Approach 5: Stateful PRNG (Server-Seeded) 🔬 MOST LIKELY

GATEs use a **stateful PRNG** that's seeded at server maintenance and advanced each cycle:

```
// At maintenance end (or start):
rng_state = seed(maintenance_time_or_patch_version)

// Every 20 minutes:
rng_state = next(rng_state)
gate = weighted_pool[rng_state % pool_size]
```

**Evidence supporting this model:**
- **Maintenance boundary anomaly (Mar 11):** Server overrode pre-computed "next GATE" at exactly the maintenance start time, proving re-seeding occurs at maintenance boundaries
- **No time-based hash found:** 2M+ hash configurations tested across CRC32, FNV-1a, MurmurHash, djb2, xorshift, LCG — none exceeded chance (~50%) on 258 clean records
- **Small-state PRNG ruled out:** ≤20-bit LCGs and xorshift variants tested against 48 consecutive slots — zero matches for interleaved model, zero matches for :20/:40 independent models
- **Weighted distributions don't match simple modular mapping:** :20 (48/33/18%) and :40 (75/16/8%) can't be produced by `state % 3` — require weighted threshold tables

**What's still unknown:**
1. PRNG algorithm (likely 32-bit+ LCG, xorshift, or Mersenne Twister)
2. Per-slot weighted threshold tables (how PRNG output maps to gate selection)
3. Seed formula (maintenance timestamp? patch version? constant?)
4. Whether PRNG advances once per slot or once per hour

**How to crack:**
- Compare pre-maintenance vs post-maintenance sequences — if identical across same-version maintenances, seed is deterministic
- Collect 30+ consecutive cycles per slot after maintenance to attempt state recovery with known PRNG algorithms
- Read per-slot pool weights from GFateDirector memory if possible

### Approach 6: Weighted Random with Larger Pool ⬜ NOT YET TRIED

The distributions (83/17, 38/35/27, 44/34/22) might come from a pool with **repeated entries**:

```
// Slot :40 pool (6 entries): [AFO, AFO, AFO, AFO, AFO, LoF]  → 83%/17%
// Slot :00 pool (8 entries): [LoF, LoF, LoF, AFO, AFO, AFO, Cliff, Cliff] → 37.5%/37.5%/25%
```

If the pool sizes are known, the hash just needs to be `hash(time) % pool_size`.

Try:
- Pool sizes 5-20 with various gate distributions matching observed percentages
- The pool might be defined in game data (see Approach 2)

### Approach 7: CRC32 / Other Hash Functions ⬜ NOT YET TRIED

Instead of xorshift, try:
- **CRC32** of the timestamp bytes
- **FNV-1a** hash
- **MurmurHash** (used in some game engines)
- **djb2** hash
- Simple **bit rotation** (ROTR/ROTL) instead of shifts

### Approach 8: Statistical Sequence Analysis ⬜ NOT YET TRIED

Instead of guessing the algorithm, analyze the output sequence mathematically:
- **Autocorrelation** — does the sequence correlate with itself at any lag?
- **Spectral analysis** (FFT) — are there hidden frequencies?
- **Runs test** — is the run length distribution consistent with a PRNG?
- **Chi-squared test** against known PRNGs — generate candidate outputs and compare distributions
- **Linear complexity** — if the PRNG is an LFSR, the Berlekamp-Massey algorithm can recover it

Need **gap-free consecutive data** for this to work — current gaps break the sequence.

---

## Priority Order (Updated after disassembly)

~~1. Approach 2 (Game data tables) — DONE, no schedule table exists~~
~~2. Approach 3 (Client disassembly) — DONE, confirmed server-side~~

Remaining approaches, re-prioritized after brute force + maintenance anomaly:
1. **Approach 5 (Stateful PRNG)** — **Most likely model.** Confirmed re-seeding at maintenance. Need post-maintenance data to compare sequences and attempt state recovery with 32-bit+ PRNGs.
2. **Approach 8 (Statistical analysis)** — Apply to post-maintenance gap-free data. Autocorrelation and sequence analysis to identify PRNG structure.
3. **Approach 4 (Eorzea time alignment)** — Still possible the seed incorporates ET components. Test with post-maintenance data.
4. ~~Approach 1 (Multi-step PRNG)~~ — Subsumed by Approach 5.
5. ~~Approach 6 (Weighted pool)~~ — Confirmed distributions don't match GFateRoulette weights. Per-slot weights are server-configured.
6. ~~Approach 7 (CRC32 etc.)~~ — Tested CRC32, FNV-1a, MurmurHash, djb2. None work as stateless hash.

---

## Data Collection Status

| Source | Reports | Period | Notes |
|--------|---------|--------|-------|
| Pre-maintenance sequence | 263 total, 258 clean | Mar 6-11, 2026 | Single PRNG seed, ended at maintenance |
| Longest consecutive run | 48 slots (16 hours) | Mar 7 03:20-19:00 | Best run for state recovery |
| Post-maintenance sequence | TBD | Mar 11+ | New PRNG seed, collection pending |

**API**: https://saucyxiv.duckdns.org — live, collecting continuously

**Per-slot distributions (clean data, pre-maintenance):**
- :00 — LoF 35%, AFO 36%, Cliff 29% (statistically uniform, chi²=4.3)
- :20 — AWTW 48%, SIR 33%, AFO 18% (weighted, chi²=7.0)
- :40 — AFO 75%, LoF 16%, SIR 8% (heavily weighted, best fit: weights [15,3,2], chi²=1.16)

**Scripts:**
- `gate_analysis.py` — distribution analysis, pool validation, periodicity checks
- `gate_bruteforce.py` — Phase 1 brute force (threshold-based, 71K combos)
- `gate_bruteforce2.py` — Phase 2 brute force (per-slot, 1.1M combos)
- `gate_crack.py` — Statistical analysis (autocorrelation, transitions, cross-slot, Eorzea time)
- `gate_crack2.py` — Extended hash brute force (2M+ configs, all hash families)
- `gate_crack3.py` — Weather hash reuse, LCG, structured seeds, threshold mapping
- `gate_crack4.py` — PRNG state recovery (small-state enumeration, Models A/B/C)

---

## Implications

If cracked, the plugin could:
1. **Predict GATEs indefinitely** with zero API dependency
2. **Show future GATE schedules** in the UI (calendar/timeline view)
3. **Eliminate polling entirely** — pure math computation
4. Community API becomes verification only
5. First public GATE prediction tool for FFXIV
