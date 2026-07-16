# AIROG_Mythic — Patch Notes

## v1.0.0 (07/15/2026) — The Director Takes the Chair

Initial release: a Mythic GME–style **chaos director** for AI Roguelite. The world
gets a Chaos Factor that rises when the player loses control and falls when they win
it back; it gates how often oracle-rolled events disrupt play and changes the prose
register the narrative AI writes in. **Zero extra AI calls** — dice and tables
simulate, GenContext narrates (the ALife doctrine).

Design doc: `Claude_MythicOracle_Design.md` (repo root). Inspiration:
`Inspiration/mythic_gme_mechanics_complete.txt` + `mythic_gme_narrative.txt`, with
three deliberate fixes (CF-gated event rate, published scene-test split, 1e
exceptional math — see the design doc's review notes).

### Chaos Factor engine (`ChaosEngine.cs`)
- CF 1–9, starts at `StartingChaosFactor` (default 5), persisted per save
  (`mythic_data.json`).
- A **scene** = one stay at a top-level place (`ApplyLocationChange` boundaries).
  During a scene, real outcomes accumulate a signed control score:
  - hostile kills witnessed near the player: +1 each (capped at +3/scene — no CF
    farming off trash mobs) via `SetAsCorpse` postfix;
  - NPC deaths in the player's presence: −1;
  - player first drops below 25% HP in a scene: −1;
  - NPCExpansion quests completed/failed (reflection poll, watermarked so loading a
    save never re-scores history): +2/−2.
- Scene end moves the CF by **exactly ±1** (Mythic's rule): positive score → CF−1,
  negative → CF+1.
- `ChaosVariation` config: Standard (full effect) / Low (CF clamped 4–6) / None
  (CF tracked but the oracle always sees 5).

### Random events — the director (`RandomEventEngine.cs`, `OracleTables.cs`)
- Each turn rolls d100; **doubles whose digit ≤ CF** fire an event (the published 1e
  rule — chaos gates disruption; flat-rate triggers were the inspiration doc's bug).
  Cooldown (default 8 turns) + per-scene cap (default 2).
- Event focus mapped onto real game state: NPC actions target a random living NPC at
  the player's top-level place; goal events target a random active NPCExpansion quest
  (soft reflection bridge — degrades to "the player's current pursuit"); plus remote
  events, PC positive/negative, new arrivals, and the signature **Ambiguous Event**
  (directive explicitly forbids the AI from explaining it).
- Meaning inspiration from **original** Action/Descriptor word lists (~100 each,
  Mythic-spirited but our own words — no Word Mill table text ships).
- Events are queued as GenContext directives with a 3-turn injection window
  (consumed by expiry, not mutation — safe across multi-generation turns).

### GenContext injection (`MythicProvider.cs`)
- Priority 82 (above WorldContext 80 — the register directive is cheap and
  load-bearing).
- Always injects a ≤100-token **world-volatility register** distilled from the Mythic
  narrative guide (LOW/MODERATE/HIGH prose registers; "never mention volatility,
  chaos levels, dice, or oracles").
- Injects up to 2 pending director events + guidance ("arrivals, not announcements"),
  and pending oracle rulings as established facts with exceptional-result weighting.

### The oracle (`MYTHIC ASK`)
- `MYTHIC ASK <odds> <question>` — full 1e Fate Chart roll (11 odds × 9 CF, the
  correct ⌊T/5⌋ exceptional math) at the effective CF. The modal shows the mechanics;
  the ruling is injected so the AI honors it as fact for the next 2 turns.
- ASK rolls can themselves trigger a director event (doubles ≤ CF), exactly as in
  tabletop Mythic.
- Rolls use `UnityEngine.Random` → free true-randomness synergy when AIROG_RandomOrg
  is installed.

### Scene testing (`SceneTest.cs`, off by default)
- On arrival at a top-level place: d10 ≤ CF → scene modified, **odd = Altered, even =
  Interrupted** (published split; the inspiration doc's "below = interrupted" version
  gave 70%+ interrupts at high CF).
- Altered → "one significant element differs" directive; Interrupted → a chaos-event
  directive (new threat / someone turns / place gone wrong / time pressure).
- `SceneTestOnlyNewPlaces` (default on) limits tests to first visits.

### Console commands
- `MYTHIC` — CF, register tier, scene ledger, pending events/rulings.
- `MYTHIC LOG` — recent director/oracle log.
- `MYTHIC CF <1-9>` — manual override. `MYTHIC EVENT` — force-fire an event.
- `MYTHIC ASK <odds> <question>` — the oracle (odds: impossible, noway, veryunlikely,
  unlikely, 5050, somewhat, likely, verylikely, nearsure, sure, hasto).

### Deliberately NOT in this mod
Threads, NPC disposition, Features, progress tracks, dreams — NPCExpansion,
WorldExpansion, Chronicle, and Reverie own those; Mythic reads (quests via
reflection) rather than duplicating.
