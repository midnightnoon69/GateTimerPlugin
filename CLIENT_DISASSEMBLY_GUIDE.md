# GATE Selection Function — Client Disassembly Guide

## Goal
Find the function in `ffxiv_dx11.exe` that determines which GATE runs at each :00/:20/:40 cycle. Extract the seed construction, hash/PRNG, and weight table lookup so we can predict GATEs.

## Prerequisites

### Tools
- **Ghidra** (free, NSA) — download from https://ghidra-sre.org
  - Requires JDK 17+ (Ghidra bundles it in recent versions)
  - Extract zip, run `ghidraRun.bat`
- Alternative: **IDA Free** (less capable but works)

### Files
- **`ffxiv_dx11.exe`** — the FFXIV client binary (x64)
  - Default location: `C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\ffxiv_dx11.exe`
  - ~150MB, x86_64 PE executable
- **FFXIVClientStructs** — community reverse-engineered struct definitions
  - Repo: https://github.com/aers/FFXIVClientStructs
  - Key file: `FFXIVClientStructs/FFXIV/Client/Game/GoldSaucer/GFateDirector.cs`
  - Has known struct offsets and vtable indices

### Reference: Known Struct Layout
```
GFateDirector (size 0x7B0, inherits GoldSaucerDirector -> Director)
  +0x788  EndTimestamp (int)
  +0x79E  GateType (byte) — enum: None=0, Cliffhanger=1, VaseOff=2,
          SkinchangeWeCanBelieveIn=3, TheTimeOfMyLife=4, AnyWayTheWindBlows=5,
          LeapOfFaith=6, AirForceOne=7, SliceIsRight=8
  +0x79F  GatePositionType (byte) — None=0, WonderSquareEast=1,
          EventSquare=2, RoundSquare=3, TheCactpotBoard=4
  +0x7A4  Flags (GFateDirectorFlag)

GoldSaucerManager (singleton)
  +0x28   CurrentGFateDirector (pointer)
```

### Reference: Game Data Sheets
```
GFateRoulette.csv — weight table: [0, 10, 5, 5, 5, 5, 5, 5, 5, 0, 0]
  Row 1: weight 10 (double probability)
  Rows 2-8: weight 5
  Rows 0, 9, 10: weight 0 (disabled/retired)
  Total active weight: 10 + 7*5 = 45

GFateType.csv — maps GateType ID to GoldSaucerTextData row
  0=65535(None), 1=91(Cliffhanger), 2=51(VaseOff), 3=81(Skinchange),
  4=61(TimeOfMyLife), 5=71(AWTW), 6=161(LoF), 7=171(AFO), 8=181(SIR)

GFATE.csv — 99 rows of individual GATE instances/variants
```

---

## Step 1: Initial Setup in Ghidra

1. Create a new project in Ghidra
2. Import `ffxiv_dx11.exe` (File → Import)
3. Accept default x64 analysis options
4. **Wait for auto-analysis to complete** — takes 30-60 minutes on first load
5. Save the project (analysis results are cached for future sessions)

## Step 2: Find the GATE Selection Function

Three approaches, try in order:

### Approach A: Search for "GFateRoulette" string
The client loads EXD sheets by name at runtime.

1. **Search → For Strings** (or `Search > Program Text`)
2. Search for `GFateRoulette` (case-sensitive)
3. If found, double-click to go to the string location
4. Right-click → **References → Find References to** (or press `Ctrl+Shift+F`)
5. Follow cross-references — the code that loads this sheet is near the selection function
6. The function will likely:
   - Load the GFateRoulette sheet
   - Read weight values from it
   - Use them in a weighted random selection

### Approach B: Find GFateDirector.GateType write
Wherever the code writes to offset 0x79E of a GFateDirector, that's where the selected GATE is stored.

1. Find the GFateDirector vtable:
   - Search for known virtual function patterns from FFXIVClientStructs
   - `IsRunningGate()` is vfunc 3, `IsAcceptingGate()` is vfunc 294
2. Once you have the struct base address pattern, search for writes to `[reg + 0x79E]`
3. Trace back from the write to find what computed the value

### Approach C: Search for weight constants
The total weight is 45 (10 + 7*5). The function likely:
- Generates a random number mod 45 (or mod total_weight)
- Walks the weight array to find which GATE it falls into

1. Search for the constant **45** used near a modulo operation (`div`/`idiv` or `and` for power-of-2)
2. Or search for the constant **10** being compared in a loop that also uses **5**
3. Or search for a loop pattern: accumulate weights, compare against random value

### Approach D: Search for Xorshift patterns
If the function uses the same PRNG as weather:

1. Search for the instruction pattern: `shl reg, 11` followed by `xor`
2. Or search for `shr reg, 8` followed by `xor`
3. Or search for shift amounts 11 and 8 used together in the same function

## Step 3: Analyze the Function

Once found, the function likely follows this pattern:

```
// Pseudocode — what we expect to find
int SelectGate(int slotIndex) {
    // 1. Compute seed from time
    int seed = f(currentTime, slotIndex);  // <-- THE KEY UNKNOWN

    // 2. Hash/PRNG
    int hash = prng(seed);                 // xorshift? LCG? something else?

    // 3. Load weights from GFateRoulette
    int totalWeight = sum(weights[]);       // = 45
    int roll = hash % totalWeight;          // or hash % 100, then mapped

    // 4. Walk weight table
    int cumulative = 0;
    for (int i = 0; i < numGates; i++) {
        cumulative += weights[i];
        if (roll < cumulative)
            return i;  // GateType index
    }
}
```

**What to extract:**
1. The seed construction (`f(currentTime, slotIndex)`) — what time value? what arithmetic?
2. The PRNG/hash function — shift amounts, operations, number of rounds
3. How the hash output maps to the weight table — mod value, threshold comparison
4. Whether slot pools (:00/:20/:40) are hardcoded or from game data

## Step 4: Validate

Once you have the algorithm:
1. Implement it in Python
2. Test against all known clean GATE data (post-fix, from 2026-03-10 06:00 UTC onward)
3. Must match 100% to confirm correctness
4. Predict future GATEs and verify in-game

---

## Tips

- **Save often** — Ghidra projects preserve your analysis, labels, and comments
- **Rename functions and variables** as you identify them — makes revisiting easier
- **Look for nearby functions** — the GATE selection function is probably near other Gold Saucer functions (e.g., Cactpot, Triple Triad)
- **Check FFXIVClientStructs issues/PRs** — someone may have already partially documented the function
- **The weather function is a good reference** — find it first (search for weather-related strings) to understand how the codebase structures these deterministic computations
- **Dalamud SigScanner** — if you can identify the function signature, Dalamud's SigScanner can hook it at runtime to verify behavior

## Alternative: Runtime Hooking via Dalamud

Instead of static disassembly, you could hook the function at runtime:

1. Find the function address using Dalamud's SigScanner with a byte pattern
2. Hook it to log inputs (time/seed) and outputs (selected GateType)
3. Observe the seed values over multiple cycles to reverse-engineer the formula
4. This requires writing a Dalamud plugin (or adding to GateNotifier) that hooks the function

This is faster than static analysis if you can find the right signature, but you need some starting point (a known byte pattern or function address).

---

## Files Referenced
- `GATE_PREDICTION_THEORY.md` — full prediction theory and cracking strategy
- `gate_bruteforce.py` / `gate_bruteforce2.py` — brute force scripts (ruled out simple xorshift)
- `gate_analysis.py` — distribution and periodicity analysis
