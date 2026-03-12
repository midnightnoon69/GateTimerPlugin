# Saucy - Gold Saucer Companion Plugin

Expanding from GATE Notifier into a comprehensive Gold Saucer companion.

## Current Features (GATE Notifier)

- GATE detection via chat messages and NPC dialogue (GATE Keeper)
- Next GATE prediction from GATE Keeper dialogue
- Overlay window with countdown timer
- Chat, toast, and sound notifications
- DTR bar entry
- Community GATE sharing via API (saucyxiv.duckdns.org)

## Planned Features

### Gold Saucer Event Notifications
- Mini Cactpot: ticket sales, drawing close warnings, new drawings
- Jumbo Cactpot: ticket sales reminders
- Triple Triad: Open tournament sessions, tournament underway alerts
- Lord of Verminion: event announcements
- Chocobo Racing: registration reminders
- Configurable per-event notification toggles

### In-GATE Filtering
- Filter out in-GATE messages (e.g. Air Force One bonus phase) from event detection
- Known in-GATE messages:
  - "Get your trigger finger ready"
  - "That's it for the bonus phase"

### Event Schedule Tracker
- Track and display schedules for recurring Gold Saucer events
- Build frequency data from observed announcements (see GOLD_SAUCER_EVENTS.md)
- Overlay showing upcoming events across all categories

### API Expansion
- Report and share non-GATE event timings
- Community-sourced schedule verification

## Rebrand Notes
- Plugin name: Saucy (or SaucyXIV)
- API domain: saucyxiv.duckdns.org
- Internal name migration from GateNotifier TBD
