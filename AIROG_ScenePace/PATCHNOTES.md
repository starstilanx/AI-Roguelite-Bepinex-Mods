# AIROG_ScenePace — Patch Notes

## v1.0.0

First release. Keeps the GM from racing past the moment.

### The problem

Reported by Furoggu: you type an action with a goal in it — *"cook a meal that [character]
would like"* — roll a success, and instead of showing the cooking going well, the GM narrates
the entire arc in one turn: preparation, every step of cooking, plating, serving, them
deciding to try it, and finally them liking it. That single turn eats every choice, reaction
and further roll you would have made along the way. In Furoggu's own save this steamrollered
a character who *"cannot understand me well and is set in their ways"* into simply accepting
food they had previously refused, because the success roll was read as *the whole clause
succeeded*.

Three prompt-level causes, all confirmed in the shipped prompts:

1. **Success is scoped to the ambition, not the attempt.** The specialized pipeline says so
   outright — `chatgpt_story_from_open_ended_major_success.txt` reads *"Generate the next
   ${LENGTH_STR} of the story in which ${actor_name} does the following action, resulting in a
   success: ${try_str}"*. If `try_str` contains a goal clause, "resulting in a success"
   licenses resolving the goal. The unified pipeline has the same hole from the other end:
   `json_substr_unified_story_desc_input.txt` defines `story` as *"Prose narrating the
   [player]'s attempted action and its outcome"*, and "its outcome" is unbounded.

2. **Nothing sets a scene boundary.** `default_narrative_rules.txt` has one adjacent line —
   *"Don't describe the player doing or saying additional things beyond what was specified"* —
   but nothing stops the GM resolving **another character's** decision, or skipping time.

3. **`story_length` is the wrong lever** and gets blown past anyway; a four-sentence summary of
   an hour still skips the hour.

### The fix

Two rules, injected at three points:

1. **A roll resolves the attempt, not the ambition.** Success means the cooking went well — not
   that they liked it. `critical_success` means the attempt could not have been performed
   better, not that the player got what they wanted.
2. **The story ends at the player's next real decision point** — normally the instant another
   character would have to make up their mind. Show their first visible reaction; stop short of
   their verdict.

### Hooks

| Hook | Target | Reach |
|---|---|---|
| `GameplayManager.GetActionSuffix` postfix | the instruction suffix vanilla splices next to the action | **Both pipelines.** Vanilla substitutes it into `${PLAYER_ACTION_INST_SUFFIX}`, which appears in all 23 specialized outcome prompts *inside* the bracketed instruction, and appends it to `player_input` in `BuildUnifiedUserPrompt`. |
| `UnifiedPromptBuilder.GetStoryOrUnifiedPreambleStrInJarrayForm` prefix | `json_substr_unified_story_desc_input.txt` | Unified system prompt: where the scene ends. |
| same prefix | `json_substr_unified_roll_guidance.txt` | Unified system prompt, roll turns only: what a successful roll covers. |

The action suffix **composes with** the one you may have set in Options rather than replacing
it. Both preamble keys are only consumed on turns this mod cares about (the story-field
description is skipped for `generation_instruction` turns, the roll guidance only appears when
the resolution mode is `NORMAL`), so no extra gating is needed. The preamble injection is also
what covers **multiplayer**, where vanilla skips the action suffix entirely.

### Deliberately not touched

- **The specialized preamble.** Its only always-present hook
  (`AUTO_JSON_SUBSTR_story_preamble_common_part2.txt`) is global, and would impose scene
  discipline on travel and system narration where fast-forward is the *desirable* behavior. The
  action suffix already reaches that pipeline where it matters.
- **Per-save `narrativeRules`.** The player edits those in the Journal; a mod writing there
  would fight those edits and only take effect on new games.

### Configuration

`BepInEx/config/com.airog.scenepace.cfg`

- `Strength` — `Off` / `Gentle` / `Firm` (default) / `Strict`. This is the
  hard-rule-versus-"prefer" question directly: Furoggu wanted to try softening it to *"prefer"*
  or *"where appropriate"*, B0ODO's counter was that it then becomes arbitrary whether the model
  obeys. `Gentle` is the former, `Firm` and `Strict` the latter — no need to guess which wins.
- `InjectActionSuffix`, `InjectIntoPreamble` — per-site toggles.
- `UsePromptFiles` — read the text from editable `.txt` files instead (default on).
- `LogInjections` — verbose per-turn logging.

Toggling `Enabled`, `Strength` or `InjectIntoPreamble` restores the vanilla preamble text on the
next turn, so changes take effect without a restart.

### Editable prompt files

`BepInEx/config/AIROG_ScenePace/` — `story_scope.txt`, `roll_scope.txt`, `action_suffix.txt`,
plus a README. Edits apply on the next AI call with no restart; `#` lines are comments.

Changing `Strength` rewrites any file you have **not** edited by hand. Once you edit one, it is
never overwritten — delete it to opt back in. The mod tracks the vanilla text of each preamble
key it amends, so editing a file mid-session replaces the previous injection instead of stacking
a second copy onto it.

### Status

Compiles clean; all four patch targets and both dictionary keys verified by reflection against
the shipped 08/12/2026 `Assembly-CSharp.dll`. **Not yet play-tested.**
