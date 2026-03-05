# GateNotifier Plugin - Planning Doc

## Feature 1: Active GATE Tracking

**Goal:** Track the current active GATE and display it in the overlay for as long as its registration window is open.

### Behavior
- Detect GATE via chat → store as the current active GATE with a registration countdown
- Show in overlay: "Air Force One — Join: 2:30" (countdown to registration close)
- Clear when registration window expires (ties into Feature 2's join durations)
- **Local detection always takes priority over API data** (Feature 3)

### Data Sources (priority order)
1. **Local chat detection** — player is in Gold Saucer, GATE announced in chat
2. **API fetch** — another user reported the GATE for this world (Feature 3)

### Implementation Notes
- Already partially implemented: `CurrentGateName`/`LastDetectedGateName` in Plugin.cs
- Need to add a `DateTime` for when the GATE was detected, so we can compute registration time remaining
- No history log needed — just the current active GATE per slot
- Clear active GATE when registration window closes (not at next cycle)

---

## Feature 2: GATE Join Window Duration

**Goal:** Show how long each GATE's registration window stays open after it's announced, so the player knows how much time they have to get there.

### Open Questions
- Are the join windows confirmed fixed per GATE type? Assumed durations (need verification):
  - The Slice Is Right — ?
  - Air Force One — ?
  - Cliffhanger — ?
  - Leap of Faith — ?
  - Any Way the Wind Blows — ?
- Where to display this info?
  - In the overlay next to the active GATE name (e.g., "Air Force One — 3:00 to join")
  - As a countdown once the GATE is detected (time remaining to register)
  - In a tooltip or info section

### Implementation Notes
- Add a `JoinWindowSeconds` dictionary to `GateDefinitions` mapping `GateType → int`
- Once a GATE is detected via chat, start a join window countdown alongside the existing cycle countdown
- Could show "Join closes in X:XX" in the overlay when a GATE is active

---

## Feature 3: Community GATE Sharing (API)

**Goal:** When a player in the Gold Saucer detects which GATE triggered, report it to a central API. Other plugin users (even those not in Gold Saucer) can then pull the confirmed GATE for their world instead of seeing "possible" GATEs.

### Flow
1. **Reporter:** Plugin detects GATE via chat → `POST` to API with `{ world, gateType, slot, timestamp }`
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
- On GATE detection in `OnChatMessage`, fire-and-forget POST
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

_(Space for additional features as they come up)_
