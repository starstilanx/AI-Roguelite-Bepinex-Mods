# AIROG_Reverie — The Dream Layer (v1.0.0)

Your own play history becomes a procedurally haunted second world.

When the player character sleeps, there is a chance they slip into a **playable dream**:
a surreal recombination of their Chronicle beats — places stitched together wrong, the
dead speaking, old triumphs replayed as threats. Dreams are low-stakes on the body
(HP is snapshotted and restored on waking) but high-stakes on the mind (a Lucidity
meter). Master the dream and wake with a **prophetic Omen** that the narrative then
works to make true; lose yourself in it and something **follows you out**.

## Design thesis
Use the LLM as a judge of fuzzy concepts. The mod owns the *structure* (when dreams
happen, how long, win/lose bookkeeping, rewards); the AI owns the *content* (what the
dream looks like, whether the dreamer is gaining or losing lucidity, what the prophecy
is). Communication is via injected directives (GenContext) downstream and a hidden
`<DREAM_STATE>` extraction block (Insight/Chronicle pattern) upstream.

## Player-facing loop
1. Player types a rest-like action ("I make camp for the night", "sleep at the inn").
2. Roll: if off cooldown (≥ 12 turns since last dream) and a 35% chance hits, the
   narration transitions into a dream instead of a plain night's rest.
3. The dreamscape is woven locally from 2–3 Chronicle beats + a random theme
   ("The Pursuit", "The Drowned Feast", "The Double", …) with a central confrontation.
4. The dream lasts up to 5 dream-turns. Each AI response carries a hidden
   `<DREAM_STATE>` block: `lucidity_delta` (−1/0/+1), `progress` (0–100), `event`.
   - Lucidity starts at 3, capped at 5. Hits 0 → **nightmare**: forced wake, Haunting.
   - Progress ≥ 100 → **triumph**: forced wake, Omen.
   - Turns exhausted → resolve by progress (≥ 60 triumph, else neutral wake).
5. Waking: HP restored to the falling-asleep snapshot (dreams never kill the body), a
   one-shot [WAKING] directive tells the AI how to narrate the transition, and the
   outcome is written to the game log.
6. Aftermath, injected while awake:
   - **Omen** (max 2 live, TTL 40 turns): `[PROPHETIC OMEN]` directive — *the narrative
     should bend toward making this foreseen thing come true*. Injection makes the
     prophecy self-fulfilling.
   - **Haunting** (one at a time, TTL 25 turns): `[HAUNTED]` directive — something that
     escaped the dream manifests in subtle, unsettling ways. Cleared early by a
     subsequent dream triumph.

## Architecture (files)
- `ReveriePlugin.cs` — BepInEx bootstrap; Harmony patch-all; registers `ReverieProvider`
  with GenContext (soft dependency, same pattern as Chronicle); console commands.
- `ReverieData.cs` — `ReverieState` (persisted to `{save}/reverie.json`), `DreamRecord`,
  `Omen`, `Haunting`, `DreamPhase` enum (Awake/Dreaming), `WakeOutcome` enum.
- `DreamWeaver.cs` — reads `chronicle.json` via stub classes (no hard Chronicle
  dependency), picks weighted beats (milestones ×3, deaths ×2), picks a theme from a
  12-entry table, composes the dreamscape premise + core confrontation. Fallback premise
  when no chronicle exists.
- `ReverieManager.cs` — the state machine: rest-intent detection handling, dream
  entry/exit, `<DREAM_STATE>` processing, lucidity/progress bookkeeping, HP
  snapshot/restore, omen/haunting lifecycle, turn-based TTLs, save/load.
- `ReverieInterceptor.cs` — Harmony patches:
  - `GameplayManager.DoConvoTextFieldSubmission` (prefix): rest-intent regex on
    `npcConvoTextInput.text`; only when awake, out of combat, off cooldown.
  - `AIAsker.GenerateTxtNoTryStrStyle` (postfix): extract + strip `<DREAM_STATE>`
    (STORY_COMPLETER / UNIFIED only). Internal AI calls never see the dream directive
    (it is injected via `BuildPromptString`, which direct AIAsker calls bypass), so no
    internal-call flag is needed.
  - `SaveIO.ReadSaveFile` / `SaveIO.WriteSaveFile` / `GameplayManager.doNewGame`:
    load/save/reset lifecycle (Chronicle pattern).
  - `GameplayManager.TurnHappenedEvent` subscription: global turn counter, TTL expiry,
    and a real-turn timeout (8 turns) as a failsafe if DREAM_STATE blocks stop arriving.
- `ReverieProvider.cs` — `IContextProvider`, Priority **96** (above Chronicle's 88 so
  dream framing dominates): DREAMING → dream directives (+ climax directive on the last
  dream-turn, asking for an `omen:` line on triumph); wake pending → one-shot [WAKING]
  block; AWAKE → live omens + haunting.

## Dream-state block contract (AI → mod)
```
<DREAM_STATE>
lucidity_delta: -1 | 0 | 1     (did the dreamer lose or gain grip on the dream?)
progress: 0-100                 (how close to confronting the dream's core)
event: one-line summary of what happened in the dream
omen: <only on the climax turn, only if the dreamer triumphed>
</DREAM_STATE>
```
Extracted and stripped before the text reaches the screen; parsed line-by-line
(Chronicle `ProcessBeatBlock` pattern — tolerant of missing keys).

## Key constants
| Constant | Value | Meaning |
|---|---|---|
| DREAM_CHANCE | 0.35 | chance a qualifying rest becomes a dream |
| MIN_TURNS_BETWEEN_DREAMS | 12 | cooldown |
| DREAM_LENGTH | 5 | max dream-turns (DREAM_STATE blocks) |
| REAL_TURN_TIMEOUT | 8 | failsafe forced wake |
| START_LUCIDITY / MAX | 3 / 5 | mind meter |
| TRIUMPH_THRESHOLD | 60 | progress needed at turn exhaustion |
| OMEN_TTL / MAX_LIVE | 40 / 2 | prophecy lifetime |
| HAUNTING_TTL | 25 | haunting lifetime |

## Console commands
- `REVERIE_TEST` — force a dream to begin immediately (bypasses roll/cooldown).
- `REVERIE_STATUS` — modal: phase, lucidity, progress, live omens, haunting, stats.
- `REVERIE_WAKE` — force wake (resolves by current progress).

## Cross-mod integration
- **Chronicle** (optional): dream material source via `chronicle.json` stubs; a dream is
  richer with Chronicle installed but works without it.
- **GenContext** (soft dep): all prompt injection. Without it the mod is inert by design
  (logged warning), like Chronicle.
- **WorldExpansion** (none in v1): a future version could queue omen fulfilment through
  `PendingWorldEvent`; v1 keeps omens self-contained.

## Save file
`{saveTopLvlDir}/{saveSubDirAsArg}/reverie.json` — whole `ReverieState`, written on game
save + on every phase transition (dream entry/wake), loaded on `ReadSaveFile`, reset on
`doNewGame`.

## Known limitations (v1)
- Rest detection is keyword-based on typed input; resting via pure dropdown actions
  won't trigger (open-ended text is how players rest in practice).
- If another mod force-kills the player mid-dream the HP restore happens at wake, not
  instantly; the anti-damage rule is a directive, and the HP snapshot is the safety net.
- Omens are made true purely by prompt pressure; there is no mechanical fulfilment check.
