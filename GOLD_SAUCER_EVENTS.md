# Gold Saucer Non-GATE Event Analysis

Events observed via overnight plugin logging on Siren. Goal: determine exact frequencies
and schedules from real data rather than trusting potentially inaccurate online sources.

## Observed Events (from 2026-03-06/07 overnight session, ~16 hours)

### Chocobo Racing Registration
"Fancy your bird the fleetest in the realm? The chocobo registrar is ever accepting registrations for new race chocobos!"

Observed times (local):
- 19:10, 22:10, 01:10, 04:10, 07:10, 10:10

Apparent interval: **3 hours**, on the :10 mark
Chat type: 68 (Gold Saucer system)

### Jumbo Cactpot
"Tickets for drawing number {N} of the Jumbo Cactpot are now on sale. To try your luck, visit the Cactpot Board!"

Observed times:
- 19:50, 22:50, 01:50, 04:50, 07:50, 10:50

Apparent interval: **3 hours**, on the :50 mark
Drawing number observed: 652 (constant across session — weekly drawing?)

### Mini Cactpot - Ticket Sales
"Tickets for drawing number {N} of the Mini Cactpot are now on sale. To test your fortunes, make your way to Entrance Square!"

Observed times:
- 20:30, 23:30, 02:30, 05:30, 08:30

Apparent interval: **3 hours**, on the :30 mark
Drawing number observed: 4576, then 4577 (changed mid-session)

### Mini Cactpot - Daily Reset
Two messages mark the daily reset cycle:

**Entries closing warning:** "Entries for drawing number {N} of the Mini Cactpot will close momentarily. Those still wishing to purchase a ticket are encouraged to act quickly!"
- Observed: 06:50 (likely ~10 min before reset)

**New drawing available:** "Entries are now being accepted for drawing number {N} of the Mini Cactpot! Venture to Entrance Square to test your luck!"
- Observed: 07:00 (drawing 4577)

Daily reset appears to be at **07:00 local** (need to confirm timezone — likely server reset time).
Drawing number incremented from 4576 to 4577 at reset.

### Triple Triad Tournaments
Two announcement types observed — likely registration vs match start for rotating tournaments.

**Registration:** "An Open tournament is currently in session! Head to Card Square to sign up and put your Triple Triad talents to the test!"

Observed times:
- 21:02, 23:02, 01:02, 03:02, 05:02, 07:02, 09:02

Apparent interval: **2 hours**, on the :02 mark (just after the :00 GATE)

**Match start:** "A Triple Triad tournament is currently underway! Prospective challengers are invited to assemble in Card Square or the Battlehall."

Observed times:
- 21:21, 23:21 (gap) 03:21, 06:21, 09:21

Apparent interval: **~3 hours**, on the :21 mark — gaps likely due to different tournament
types rotating (Open, Roulette, etc.). Need to identify which tournament types exist and
their rotation schedule.

### Lord of Verminion
"Have you tested your tactics with Lord of Verminion? Assemble your loyal minions, and come to Minion Square!"

Observed times:
- 21:41, 00:41, 03:41, 06:41, 09:41

Apparent interval: **3 hours**, on the :41 mark

## Summary Table

| Event                    | Interval | Minute Mark | Confidence |
|--------------------------|----------|-------------|------------|
| Chocobo Racing           | 3h       | :10         | High       |
| Jumbo Cactpot            | 3h       | :50         | High       |
| Mini Cactpot Sales       | 3h       | :30         | High       |
| Mini Cactpot Reset       | daily    | ~07:00      | Medium     |
| TT Registration          | 2h       | :02         | High       |
| TT Match Start           | ~3h      | :21         | Medium     |
| Lord of Verminion        | 3h       | :41         | High       |

## Questions to Resolve

1. Are these intervals based on Eorzea time or real time?
2. Do schedules vary by server/data center?
3. What Triple Triad tournament types exist and how do they rotate?
4. Mini Cactpot daily reset — what timezone is 07:00 in? Server time / UTC / JST?
5. Are there additional events not seen in this session (e.g. Fashion Report, Make It Rain campaign events)?
6. Jumbo Cactpot — weekly drawing day? (drawing 652 was constant all session)

## Data Collection Plan

- Continue overnight logging to confirm intervals across multiple sessions
- Compare data across different days/weeks for seasonal or weekly patterns
- Check if Jumbo Cactpot drawing number increments weekly (Saturday reset?)
- Cross-reference with known Eorzea time cycles (1 Eorzea day = 70 real minutes)
