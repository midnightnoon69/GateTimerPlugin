# GoldSaucerManager Memory Analysis

## Overview

The GoldSaucerManager struct extends well beyond its documented 0x28 bytes. A scan of 0x840 bytes reveals a **header + 13 repeating blocks** at 0x90-byte intervals, totaling 14 entries (block 0 is structurally different).

**Working hypothesis**: The 14 blocks (0–13) each represent one **GATE course variant**. With 4 AFO + 3 LoF + 2 Cliff + 1 AWTW + 1 SIR + 3 retired = 14, the count matches exactly. Unconfirmed — need scans during different course variants to verify.

## Block Layout

```
Base address: GoldSaucerManager + 0x0078
Block size:   0x90 (144 bytes)
Block count:  14 (one per course variant)

Per-block structure:
  +0x00  8 bytes   Hash/ID (incrementing index at byte +5: 0xED–0xFA)
  +0x08  8 bytes   Zeros
  +0x10  8 bytes   Pointer
  +0x18  8 bytes   Marker (block 0: unique ptr, blocks 1–13: AD 5E AD 5E 01 00 00 00)
  +0x20  8 bytes   Field (block 0: 0x9000, blocks 1–13: 0x67 = 103)
  +0x28  8 bytes   Zeros / data
  +0x30  8 bytes   Pointer
  +0x38  32 bytes  8 × float32 — per-gate-type values (see below)
  +0x58  56 bytes  Zeros (padding)
```

## Float Array Mapping (8 floats per block at +0x38)

Each block contains 8 floats. Float index 2 is **always zero** across all blocks and all 23 dumps, strongly suggesting the indices map to the 8 non-None gate types:

| Index | Gate Type    | Enum | Status  |
|-------|-------------|------|---------|
| 0     | Cliffhanger | 1    | Active  |
| 1     | Vase Off    | 2    | Retired (patch 4.4) |
| 2     | Skinchange  | 3    | Retired (patch 5.1) — **always zero** |
| 3     | Time of My Life | 4 | Retired (patch 4.5) |
| 4     | Any Way the Wind Blows | 5 | Active |
| 5     | Leap of Faith | 6   | Active  |
| 6     | Air Force One | 7   | Active  |
| 7     | The Slice Is Right | 8 | Active |

## GATE Course Variants (Reference)

### Active GATEs (10 courses)

| Gate | Course | Slot | Added |
|------|--------|------|-------|
| Leap of Faith | The Falling City of Nym | :00 | Patch 5.2 |
| Leap of Faith | The Fall of Belah'dia | :20 | Patch 4.4 (original) |
| Leap of Faith | Sylphstep | :40 | Patch 6.3 |
| Air Force One | The Gold Saucer | :20 | Patch 4.5 (original) |
| Air Force One | The Cieldalaes (Cave) | :00/:40 | Patch 7.4 |
| Air Force One | The Cieldalaes (Ship) | :00/:40 | Patch 7.4 |
| Air Force One | The Cieldalaes (Cliff) | :00/:40 | Patch 7.4 |
| Cliffhanger | Mt. Corel | :00 | Patch 2.51 (original) |
| Cliffhanger | El Coloso | :00? | Unknown patch |
| Any Way the Wind Blows | (single course) | :20 | Active |
| The Slice Is Right | (single course) | :20 | Active |

### Retired GATEs (3 courses)

| Gate | Courses | Removed |
|------|---------|---------|
| Skinchange We Can Believe In | 1 | Patch 5.1 |
| The Time of My Life | 1 | Patch 4.5 |
| Vase Off | 1 | Patch 4.4 |

**Total: 14 courses = 14 blocks (0–13)** — no header block needed, all 14 are course entries. — count matches perfectly.

## Observed Float Data (23 dumps, 2026-03-11/12)

### Block Classification Summary

| Block | Offset | Status | Non-zero types | Gate types present |
|-------|--------|--------|----------------|-------------------|
| 0  | 0x00B0 | ZERO | 0 | (different structure — likely oldest course, e.g. Cliff Mt. Corel or a retired gate from patch 2.51) |
| 1  | 0x0140 | ZERO | 0 | — |
| **2**  | **0x01D0** | **DYNAMIC** | **7** | Cliff, VaseOff, TimeMyL, AWTW, LoF, AFO, SIR |
| 3  | 0x0260 | ZERO | 0 | — |
| **4**  | **0x02F0** | **STATIC** | **5** | Cliff, VaseOff, TimeMyL, AWTW, LoF |
| **5**  | **0x0380** | **DYNAMIC** | **3–5** | varies: Cliff, VaseOff, TimeMyL, AWTW, LoF |
| **6**  | **0x0410** | **NEAR-STATIC** | **7** | Cliff, VaseOff, TimeMyL, AWTW, LoF, AFO, SIR |
| **7**  | **0x04A0** | **DYNAMIC** | **5** | Cliff, VaseOff, TimeMyL, AWTW, LoF |
| 8  | 0x0530 | ZERO | 0 | — |
| 9  | 0x05C0 | ZERO | 0 | — |
| 10 | 0x0650 | ZERO | 0 | — |
| **11** | **0x06E0** | **DYNAMIC** | **3–4** | Cliff, VaseOff, TimeMyL, LoF(≈0) |
| **12** | **0x0770** | **STATIC** | **3** | Cliff, VaseOff, TimeMyL |
| **13** | **0x0800** | **DYNAMIC** | **5** | Cliff, VaseOff, TimeMyL, AWTW, LoF |

### Gate-Type Grouping Pattern

Blocks cluster into 3 groups by which gate types they track:

| Group | Gate types | Blocks | Count |
|-------|-----------|--------|-------|
| A — Full | All 7 (except Skinchg) | 2, 6 | 2 |
| B — No AFO/SIR | Cliff, VaseOff, TimeMyL, AWTW, LoF | 4, 5, 7, 13 | 4 |
| C — Minimal | Cliff, VaseOff, TimeMyL only | 11, 12 | 2 |
| — Zero | None | 0, 1, 3, 8, 9, 10 | 6 |

### Static Block Values

**Block 4** @ 0x02F0 (STATIC — identical across all 23 dumps):
```
Cliff=0.903149  VaseOff=0.900028  Skinchg=0  TimeMyL=0.386949  AWTW=0.204566  LoF=0.239894  AFO=0  SIR=0
```

**Block 12** @ 0x0770 (STATIC — identical across all 23 dumps):
```
Cliff=1.104576  VaseOff=1.000534  Skinchg=0  TimeMyL=0.259445  AWTW=0  LoF=0.000001  AFO=0  SIR=0
```

**Block 6** @ 0x0410 (NEAR-STATIC — first 5 values constant, AFO/SIR drift slightly):
```
Cliff=1.996210  VaseOff=2.037694  Skinchg=0  TimeMyL=0.705395  AWTW=1.056913  LoF=1.262014  AFO≈0.185–0.189  SIR≈0.182–0.187
```
Largest magnitudes of any block. AFO/SIR changed from 0.1837→0.1890 and 0.1805→0.1870 across the dump session.

### Dynamic Block Sample Data

**Block 2** (7 types, most active):
```
gsm_051910: Cliff=0.016 VaseOff=0.029 TimeMyL=0.010 AWTW=0.015 LoF=0.012 AFO=0.003 SIR=0.004
gsm_052012: Cliff=0.011 VaseOff=0.026 TimeMyL=0.020 AWTW=0.048 LoF=0.013 AFO=0.004 SIR=0.003
gsm_052021: Cliff=0.023 VaseOff=0.030 TimeMyL=0.010 AWTW=0.009 LoF=0.013 AFO=0.003 SIR=0.002
```

**Block 13** (5 types, newly captured):
```
gsm_051910: Cliff=0.031 VaseOff=0.048 TimeMyL=0.021 AWTW=0.036 LoF=0.035
gsm_051915: Cliff=0.050 VaseOff=0.035 TimeMyL=0.029 AWTW=0.029 LoF=0.034
gsm_052012: Cliff=0.023 VaseOff=0.059 TimeMyL=0.027 AWTW=0.048 LoF=0.035
gsm_052021: Cliff=0.052 VaseOff=0.052 TimeMyL=0.032 AWTW=0.020 LoF=0.039
```

### Zero Blocks

Blocks 0, 1, 3, 8, 9, 10 — all floats are zero across all 23 dumps.

## What Are the Blocks?

### Course mapping: unconfirmed, not yet disproven

The 13-course count matching 13 data blocks is suggestive, but current data neither confirms nor disproves it:

**Observations that complicate the theory:**

1. **No obvious per-block activation during specific GATEs.** Dynamic blocks fluctuate continuously during both AFO and AWTW — but this could mean the metrics update globally, not just when "their" course is active.

2. **Zero blocks stay zero through all observed GATE types.** Blocks 0, 1, 3, 8, 9, 10 remained zero during AFO and AWTW. Could mean those courses haven't generated data, or could mean the blocks aren't courses.

3. **Block 5 is intermittently zero.** Goes to all-zero in some scans (042815, 052325), has data in others — not clearly tied to any GATE phase.

4. **Each block has values across multiple float indices.** If the 8 floats = 8 gate types, a course-specific block shouldn't need values for other gate types. **However**, the 8 floats might represent 8 different metrics about each block's content, not 8 gate types. The "index 2 always zero = Skinchange" mapping is suggestive but unconfirmed.

5. **Static blocks include values at retired-gate indices.** VaseOff and TimeOfMyLife indices are non-zero in blocks 4, 6, and 12.

### Most likely: Gold Saucer activity metrics

The 14 blocks likely represent **Gold Saucer content categories** (not just GATEs), with floats being per-gate-type metrics. Candidate content types for the 14 blocks:

- GATEs (the float array tracks gate types)
- Chocobo Racing
- Triple Triad
- Lord of Verminion
- Mini Cactpot / Jumbo Cactpot
- Various mini-games (Doman Mahjong, Fashion Report, etc.)
- Possibly aggregate/summary blocks

The static blocks (4, 12) would be configuration data, the near-static block (6) an aggregate or slowly-updating metric, and dynamic blocks (2, 5, 7, 11, 13) runtime rate accumulators or smoothed participation stats.

### What the floats probably ARE

- **Static blocks 4, 12**: Configuration or weight tables. Values 0.2–1.1, never change.
- **Near-static block 6**: Master/aggregate weights. Largest magnitudes (1.0–2.0). AFO/SIR slowly drift (added later than other gates, still converging?).
- **Dynamic blocks 2, 5, 7, 11, 13**: Small values (0.001–0.07) that change every scan. Likely exponential moving averages, smoothed rates, or participation counters.

## Scan Context Log (25 dumps, 2026-03-11/12)

| Dump | Time | Context | GATE state |
|------|------|---------|------------|
| 035331–041324 | earlier | Various (pre-expansion scans, 0x200 range) | Mixed |
| 041848–042209 | earlier | AWTW :20 scans (pre-expansion, 0x200) | Mixed |
| 042815 | 21:28 | After NPC predicted AFO for :40 | Between GATEs, director=null |
| 044004 | 21:40 | During AFO :40 (Cieldalaes Cliff) | director=active, byte=7, pos=3 |
| 045018 | 21:50 | After AFO :40 ended, before NPC | Between GATEs, director=null |
| 045028 | 21:50 | After NPC predicted AFO for :00 | Between GATEs, director=null |
| 050011 | 22:00 | During AFO :00 (Cieldalaes Cliff) | director=active, byte=7, pos=3 |
| 050408 | 22:04 | During AFO :00 (Cieldalaes Cliff, later) | director=active, byte=7, pos=3 |
| 051910 | 22:19 | After AFO :00 ended, before NPC | Between GATEs, director=null |
| 051915 | 22:19 | After NPC predicted AWTW for :20 | Between GATEs, director=null |
| 052012 | 22:20 | During AWTW :20 | director=active, byte=5, pos=2 |
| 052021 | 22:20 | During AWTW :20 (second scan) | director=active, byte=5, pos=2 |
| 052325 | 22:23 | After AWTW :20 ended, before NPC | Between GATEs, director=null |
| 052332 | 22:23 | After NPC predicted AFO for :40 | Between GATEs, director=null |
| — | 22:40 | During AFO :40 (Cieldalaes Cliff) | director=active, byte=7, pos=3, flags=0x08 |

**Key finding**: All three AFO instances had pos=3 = **Cieldalaes Cliff** course. AWTW had pos=2.

**Caveat on field names**: `GateType` and `GatePositionType` are community-assigned names from FFXIVClientStructs, not official SE names. We assume `GatePositionType` indicates course variant, but it could mean something else (location, difficulty, instance ID, etc.). Only 2 distinct values observed so far (2 and 3) — not enough to confirm meaning.

## Open Questions

1. **Where is the NPC "next GATE" prediction stored?** Not in GSM 0x000–0x840 or GFateDirector. The GATE Keeper knows the next GATE before announcement but this data isn't in either scanned structure. May be server-sent as part of NPC dialogue packet rather than stored in a persistent client structure.

2. **Do blocks map to GATE course variants?** Count matches (13 courses = 13 blocks), but we've only scanned during AFO pos=3 and AWTW pos=2. **Critical missing data**: Need scans during LoF, Cliff, SIR, and different AFO pos values to see if different blocks respond to different courses. Zero blocks (1, 3, 8, 9, 10) might activate during untested GATE types.

3. **Are the static blocks (4, 12) GATE selection weights?** Their values could be roulette weights, but they include retired gates. The GFateRoulette row weights from game data `[0, 10, 5, 5, 5, 5, 5, 5, 5, 0, 0]` don't obviously match these floats.

4. **What do the float magnitudes mean?** Static values range 0.2–2.0, dynamic values 0.001–0.07. Block 6 has the largest values. The relationship between blocks is unclear.

5. **What do the 8 floats per block represent?** Assumed to be 8 gate types (index 2 = Skinchange always zero), but could alternatively be 8 different metrics about each block's content.

## How to Verify

- **Scan during different GATE types** (LoF, Cliff, SIR) to see if different dynamic blocks activate
- **Scan during different AFO courses** (pos=1 vs pos=2 vs pos=3) to check if specific blocks correlate with pos values
- **Compare block changes between scans** during a single GATE vs between GATEs
- **Cross-reference with GFateDirector** PosType field to determine course variant mapping
