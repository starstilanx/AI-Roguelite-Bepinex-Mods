# AIROG_RandomOrg — Patch Notes

## v1.1.0 (07/12/2026) — Dice-rolls-only quota saver

Fixes daily API quota drain reported by users: the mod previously replaced
**every** `Utils.RandDouble/RandInt/RandIntInclusive` call in the game with a
Random.org number — including visuals, color generation, voice picks, world
gen, and other non-gameplay randomness. That constant buffer churn triggered
frequent refetches and burned through the free daily quota.

### Changes
- **New `DiceRollsOnly` config (default: ON)** — Random.org numbers are now
  only spent on actual in-game dice/skill-check rolls: a Harmony
  prefix/finalizer pair marks a `[ThreadStatic]` roll scope while
  `Utils.GetRollOutcome(GameplayManager, …)` or
  `Utils.GetRollOutcomeForAttrOnly(…)` is on the call stack, and the
  `RandDouble/RandInt` patches only consume the buffer inside that scope.
  Covers the raw roll number, all `RollRandomness` dice variants
  (1d100 … 50d2), and the avg-outcome coin flip. Everything outside the scope
  falls through to the vanilla `System.Random`.
- Set `DiceRollsOnly = false` to restore the old all-randomness behavior.
- New "└ Dice rolls only (saves daily API quota)" toggle in the GenContext
  Mods menu.

### Notes
- Signatures verified against the 07/11/2026 game build.
- No change to fetch/buffer mechanics; the buffer simply drains ~1000× slower,
  so a single prefetch now lasts hundreds of rolls.

## v1.0.0 — Initial release
True randomness from Random.org for all RNG. Anonymous plain-text mode
(~1M bits/day) or JSON-RPC API-key mode. Pre-fetch buffer with background
refill, System.Random fallback, GenContext Mods-menu toggles.
