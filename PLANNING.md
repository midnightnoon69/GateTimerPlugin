# GateNotifier Plugin - Planning Doc

## Feature 1: Active GATE Tracking (Implemented)

**Goal:** Track the current active GATE and display it in the overlay for as long as its registration window is open.

### Behavior
- Detect GATE via chat → store as the current active GATE with a registration countdown
- Show in overlay: "Air Force One — Join: 2:30" (countdown to registration close)
- Clear when registration window expires or when close message received via chat
- **Local detection always takes priority over API data** (Feature 3)
- State persists through plugin reloads via Configuration

### Data Sources (priority order)
1. **GATE Keeper NPC dialogue** — reveals next GATE immediately after current one ends (~7-15 min early)
2. **Local chat detection** — player is in Gold Saucer, GATE announced in chat (type 68)
3. **API fetch** — another user reported the GATE for this world (Feature 3, not yet implemented)

### Implementation
- `CurrentGateName`/`CurrentGateType`/`CurrentGateDetectedAt` — persisted in Configuration
- `LastDetectedGateName`/`LastDetectedGateType` — in-memory, promoted to Current at cycle boundary
- Registration close detected via chat message: "Entries for the special limited-time event are now closed"
- Fallback expiry via `JoinWindowSeconds` dictionary

---

## Feature 2: GATE Join Window Duration (Implemented)

**Goal:** Show how long each GATE's registration window stays open after it's announced.

### Current Values (approximate — gathering data via CSV logger)
- The Slice Is Right — ~2 min (Event Square GATE)
- Air Force One — ~10 min (needs verification)
- Cliffhanger — ~10 min (needs verification)
- Leap of Faith — ~8 min (measured in-game)
- Any Way the Wind Blows — ~2 min (Event Square GATE)

### Implementation
- `JoinWindowSeconds` dictionary in `GateDefinitions`
- Join countdown shown in overlay: "Air Force One — Join: 2:30"
- CSV logger (`gate_timings.csv`) records open/close pairs for verification
- Chat-based close detection takes priority over hardcoded timers

---

## Feature 4: GATE Keeper NPC Detection (Implemented)

**Goal:** Detect upcoming GATEs early by reading GATE Keeper NPC dialogue.

### Discovery
- The GATE Keeper NPC in Gold Saucer reveals the next GATE **immediately after the current one ends**
- This provides ~7-15 minutes of advance notice depending on when the current GATE finishes
- Dialogue format: `Our next scheduled event, "Air Force One," will be held in Round Square.`
- Second line gives time/location: `The next GATE will be held at 10:40 p.m. in Round Square.`

### Implementation
- Uses `IAddonLifecycle` to hook the "Talk" addon (`PostSetup` + `PostRefresh` events)
- Reads `AddonTalk->AtkTextNode220` (speaker) and `AtkTextNode228` (text)
- Filters for speaker containing "GATE Keeper"
- Matches GATE names using existing `ChatSubstrings` dictionary
- Stores as `LastDetectedGateName` for display in overlay

### Open Questions
- Can we parse the time from the second dialogue line for additional data?
- Does the GATE Keeper always use the same dialogue pattern for all GATEs?
- Is there a way to read the GATE Keeper's dialogue without the player manually interacting?

---

## Feature 3: Community GATE Sharing (API)

**Goal:** When a player in the Gold Saucer detects which GATE triggered, report it to a central API. Other plugin users (even those not in Gold Saucer) can then pull the confirmed GATE for their world instead of seeing "possible" GATEs.

### Flow
1. **Reporter:** Plugin detects GATE via chat or GATE Keeper → `POST` to API with `{ world, gateType, slot, timestamp }`
2. **API server (your app, to be created):** Receives report, validates, caches per world+slot
3. **Consumer:** Plugin periodically polls (or gets pushed) the current GATE for their world → overlay shows confirmed GATE instead of the 3 possibilities

### Open Questions
- **World scoping:** GATEs are the same across all worlds on a data center, or per-world? (Need to confirm — if per-DC, scope to data center instead)
- **Auth / abuse prevention:** How to prevent bad data?
  - API key per user?
  - Require multiple reporters to agree before caching?
  - Rate limiting per world/slot?
  - Trust-on-first-report (simple, fast) vs consensus?
- **API tech stack:** What do you want to build this in? (e.g., C#/ASP.NET, Node, Go, etc.)
- **Polling vs push:** Plugin polls every N seconds, or WebSocket/SSE for real-time?
- **Privacy:** Any concerns with sending world name + timestamp? (No character info needed)
- **Fallback:** If API is unreachable, plugin works exactly as it does today (local-only)
- **Cache TTL:** Each slot is 20 min, so cache entries expire after ~20 min automatically

### Implementation Notes — Plugin Side
- New `ApiService` class handling HTTP calls
- Config options: enable/disable sharing, API URL (for self-hosters?)
- On GATE detection in `OnChatMessage` or GATE Keeper, fire-and-forget POST
- On framework update, if no local detection, check API for confirmed GATE
- Needs the player's current world — available via `IClientState.LocalPlayer.CurrentWorld`

### Implementation Notes — API Side
- Simple REST API:
  - `POST /gate` — report a GATE `{ world, gate, slot, timestamp }`
  - `GET /gate/{world}` — get current confirmed GATE for a world
- Cache layer (in-memory or Redis) keyed by `world:slot`
- Minimal persistence needed — data is ephemeral (20 min cycles)

---

## Bug Fixes

### ~~Bug 1: Settings window flickers~~ (Resolved)
- Was loading an older build from a different path. Fixed OpenConfigUi/OpenMainUi to always open (not toggle) and stopped per-frame IsOpen assignment as a preventive measure.

### ~~Bug 2: Plugin marked as "decommissioned"~~ (Resolved)
- Was likely a stale Dalamud cache. Resolved after re-checking — repo.json is correct and plugin shows fine now.

### ~~Bug 3: Chat detection not working~~ (Resolved)
- **Root cause:** Chat type filter was checking for base type 57 (`XivChatType.SystemMessage`) but GATE announcements use chat type **68 (0x0044)**.
- **Fix:** Updated filter to `((int)type & 0x7F) != 68`.

---

## Future Ideas

- Parse GATE Keeper's second dialogue line for time/location data
- Automated GATE Keeper reading (if possible without player interaction)
- Historical GATE tracking on the server side
- Data center-wide GATE sharing (if GATEs are per-DC not per-world)
