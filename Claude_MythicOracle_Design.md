# AIROG_Mythic — Design Doc (07/15/2026)

> **STATUS: SHIPPED as v1.0.0 (07/15/2026)** — all five pillars implemented in
> `AIROG_Mythic/` and deployed; see its `PATCHNOTES.md`. Open questions §10 were
> resolved: quests bridged via reflection to `AIROG_NPCExpansion.QuestManager.AllQuests`;
> the in-combat/flee signal was dropped for v1 (no clean surface found — kills, HP,
> NPC deaths, and quest outcomes carry the control score); kill attribution gated on
> same-top-level-place like ALife; provider priority landed at 82. Not yet play-tested.

A Mythic GME–style **chaos director** for AI Roguelite. Source inspiration:
`Inspiration/mythic_gme_mechanics_complete.txt` + `mythic_gme_narrative.txt`.

**Elevator pitch:** the game gets a Chaos Factor — a 1–9 world-volatility stat that
rises when the player loses control and falls when they win it back. It gates how
often oracle-rolled random events disrupt play and changes the *prose register* the
narrative AI is told to write in. Plus a real player-facing yes/no oracle
(`MYTHIC ASK`). Zero extra AI calls — dice and tables simulate, GenContext narrates
(same doctrine as ALife).

---

## 1. What we adopt vs. skip

| Mythic system | Verdict | Why |
|---|---|---|
| Chaos Factor | **ADOPT — core** | Nothing in the ecosystem owns global volatility |
| Random Events (Focus + Meaning words) | **ADOPT — core** | The "director" — piggybacks on next AI generation |
| Fate Chart oracle (1e d100) | **ADOPT** | `MYTHIC ASK` console command; solo-RPG oracle in-game |
| Scene testing (Normal/Altered/Interrupted) | **ADOPT — v1.1, toggleable** | Maps cleanly onto `ApplyLocationChange` |
| Narrative register guidance (narrative doc §3, §36) | **ADOPT — distilled** | Injected as ≤120-token directive, never wholesale |
| Threads list | **SKIP — read, don't own** | NPCExpansion quests + Chronicle chapters ARE the threads |
| NPC disposition/behavior/stats | **SKIP** | NPCExpansion profiles own this |
| Features list | **SKIP** | WorldExpansion places/claims own this |
| Progress tracks, Adventure Crafter plotlines | **SKIP v1** | Chronicle chapters cover arc structure; maybe v2 Turning Points at chapter boundaries |
| 2e Fate Check (2d10) | **SKIP** | Doc's exceptional threshold (≥12) is broken — majority of favorable rolls would be Exceptional. 1e chart math is correct |
| Dreams/omens, nemesis, fear/awe | **SKIP** | Reverie / NPCExpansion / ALife Wake respectively |

### Deliberate fixes to the inspiration docs (see review, 07/15/2026)
1. **RE trigger is CF-gated** (published 1e rule): d100 doubles fire an event only if
   the doubles digit ≤ CF. The doc's flat 9% rate removes CF's main tooth.
2. **Scene test uses the published split**: d10 ≤ CF → scene modified; odd = Altered,
   even = Interrupted. The doc's "below CF = Interrupted" gives 70%+ interrupts at high CF.
3. **Exceptional results use 1e math**: EY if roll ≤ ⌊threshold/5⌋; EN in top 20% of NO range.
4. Meaning-table word lists are **reworded/original** (Word Mill IP stays out of a public release;
   mechanics/dice math are fine).

---

## 2. Architecture

```
AIROG_Mythic/
  MythicPlugin.cs        BaseModPlugin (AIROG_Core); patch registration, config
  MythicData.cs          save state via ModSaveFile: CF, scene ledger, event log, ask history
  ChaosFactor.cs         CF value + scene control-score accumulator + adjust rules
  OracleTables.cs        Fate Chart (11 odds × 9 CF), Focus table, Action/Descriptor word lists
  RandomEventEngine.cs   trigger rolls, focus resolution, target selection, pending-event queue
  SceneTest.cs           d10-vs-CF on location change (v1.1)
  MythicProvider.cs      IContextProvider → GenContext injection
  ConsoleCommands.cs     MYTHIC / MYTHIC ASK / MYTHIC CF
  PATCHNOTES.md
```

net472, references `Assembly-CSharp.dll` + Unity dlls from `AIRL/libs/`, hard
dependency on AIROG_GenContext (`ContextManager.RegisterProvider`), soft awareness of
NPCExpansion / WorldExpansion / ALife / Chronicle via reflection (never hard refs —
follow ALifeWorldBridge pattern).

---

## 3. Pillar 1 — Chaos Factor engine

- CF ∈ [1,9], starts at config `StartingCF` (default 5). Persisted per-save.
- **A "scene" = one stay at a top-level place.** Boundaries = `GameplayManager.ApplyLocationChange`
  (prefix, same patch point as ALifeMaterializer). Mythic adjusts CF once per scene end — this maps
  naturally and avoids per-turn CF thrash.
- During a scene, accumulate a signed **control score** from real outcomes:

| Signal | Hook | Score |
|---|---|---|
| Player kills a hostile | `GameCharacter.SetAsCorpse` postfix (ALifeLegend pattern; attribute via combat-target check) | +1 |
| Player character dies / near-death (HP < 25% first time in scene) | HP poll on `TurnHappenedEvent` (cheap; Multiplayer already reads HP) | −2 / −1 |
| Follower or friendly NPC dies | SetAsCorpse postfix, faction check | −1 |
| Quest completed | NPCExpansion quest state via reflection (verify API at impl time) | +2 |
| Player flees combat / scene ends mid-combat | combat-state check at location change | −1 |

- Scene end: score > 0 → CF−1; score < 0 → CF+1; 0 → unchanged. Exactly ±1 (Mythic rule).
- Console override `MYTHIC CF <n>` for players who want manual Mythic-style control.

## 4. Pillar 2 — Random events (the director)

- **Trigger:** on each `TurnHappenedEvent`, roll d100; if doubles (11..99) AND digit ≤ CF → queue event.
  Config `EventCooldownTurns` (default ~8) + `MaxEventsPerScene` (default 2) prevent spam at high CF.
- **Focus (d100, adapted from Mythic's table, targets mapped to real game state):**

| Range | Focus | Target source |
|---|---|---|
| 1–7 | Remote Event | Hand a templated event to WorldExpansion's pending queue if present; else inject as rumor directive |
| 8–15 | NPC Action | Random known NPC (NPCExpansion roster → fallback: NPCs at current place via `SS.I.uuidToGameEntityMap`) |
| 16–20 | New NPC arrives | Directive only — the game/AI already handles spawning narratively |
| 21–45 | Quest/Thread moves (toward / away / closes) | Active quest via NPCExpansion; fallback: "the player's current goal" |
| 46–55 | PC Negative / PC Positive | Player directly |
| 56–67 | Ambiguous Event | No target — the signature Mythic move; directive explicitly says "do not explain it" |
| 68–83 | NPC Negative / Positive | As NPC Action |
| 84–100 | Major thread advance / important new NPC | As above, "significant" flag |

- **Meaning:** roll Action + Descriptor from original ~100-word lists (genre-neutral verbs/domains,
  spiritually Mythic but reworded). Stored on the pending event.
- **Consumption:** the pending event is injected into the next 1–2 AI generations via MythicProvider,
  then marked consumed. No AI call is ever made *for* the event — pure piggyback.

## 5. Pillar 3 — GenContext injection (MythicProvider)

Registers via `ContextManager.RegisterProvider`. Priority: below NPCProvider, above flavor
providers (exact number tuned against existing registry at impl time). Budget target ≤ 200 tokens
worst case.

Always injected (≈60–100 tokens): CF register directive, one of three tiers distilled from
narrative doc §3 + §36 — e.g. high chaos:

> `[WORLD VOLATILITY: HIGH] Events outpace their causes. NPCs act before being prompted;
> problems compound; timing feels wrong. Keep prose sharp and fractured. Never mention
> chaos levels, dice, or oracles.`

When an event is pending (≈80–120 tokens):

> `[DIRECTOR EVENT — weave into this scene as something ALREADY IN MOTION, not an announcement]
> Focus: a known character acts on their own initiative → Kessa the smuggler.
> Inspiration words: "Betray + Leadership" — interpret in context, never state the words.
> Connect to established facts. Do not resolve what it means yet.`

`MYTHIC ASK` rulings inject once as `[ORACLE RULING (established fact): "<question>" → NO, and worse:
add one connected complication.]`

## 6. Pillar 4 — Scene testing (v1.1, off by default)

On `ApplyLocationChange` to a top-level place: d10 ≤ CF → modified scene (odd Altered / even
Interrupted). Altered → directive "one significant element at this place differs from what the
player expects" + meaning words. Interrupted → roll a Chaos-Event-style directive (new threat /
NPC turns / environmental) that takes over the arrival narration. Off by default because AIRL
travel is frequent; `EnableSceneTest` + `SceneTestOnlyNewPlaces` config.

## 7. Pillar 5 — Console commands

- `MYTHIC` — CF, register tier, pending/recent events, config summary.
- `MYTHIC ASK <odds> <question>` — full 1e Fate Chart roll at current CF (odds: impossible,
  noway, veryunlikely, unlikely, 5050, somewhat, likely, verylikely, nearsure, sure, hasto).
  Prints roll/threshold/result, checks Exceptional, checks RE trigger (doubles ≤ CF), and injects
  the ruling. This is the feature that makes it a real solo-RPG oracle, not just a director.
- `MYTHIC CF <1-9>` — manual set. `MYTHIC EVENT` — force-fire one (testing/fun).
- Patch point: `GameplayManager.ProcessConsoleCommand` prefix (ALifePlugin pattern).

**RandomOrg synergy:** if AIROG_RandomOrg is installed and patches the PRNG globally, oracle rolls
get true atmospheric randomness for free — verify at impl time whether its patch surface covers
our `System.Random`/UnityEngine.Random usage, and if not, call its client via reflection.

## 8. Config (BepInEx)

`StartingCF=5`, `EnableRandomEvents=true`, `EventCooldownTurns=8`, `MaxEventsPerScene=2`,
`EnableSceneTest=false`, `SceneTestOnlyNewPlaces=true`, `ChaosVariation=Standard|Low|None`
(Low = flatten CF's effect on the Fate Chart, None = CF tracked but oracle ignores it — both
straight from the docs and cheap to support), `InjectRegisterDirective=true`.

## 9. Phasing

- **v0.1** — ChaosFactor engine + MythicProvider register injection + `MYTHIC` status command.
- **v0.2** — RandomEventEngine + focus targeting + pending-event injection.
- **v0.3** — `MYTHIC ASK` oracle + ruling injection + RandomOrg routing.
- **v0.4** — Scene testing.
- **v1.0** — polish, PATCHNOTES, catalogue entry.

## 10. Open questions (resolve during implementation)

1. Exact NPCExpansion quest-list API for thread targeting (reflection surface + null-safety).
2. Reliable "in combat" check for flee/scene-end-mid-combat signal (see how the game gates
   `combatTracks` playback / what MusicExpansion keys off).
3. Kill attribution in SetAsCorpse postfix — ALife noted offline deaths aren't attributable;
   we only want *player-witnessed* kills, so gate on current-place membership like ALife does.
4. Provider priority slot — read the live registry ordering before picking a number.
