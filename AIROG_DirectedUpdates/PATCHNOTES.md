# AIROG_DirectedUpdates — Patch Notes

## v1.1.0 (08/15/2026)

Both changes come from Furoggu's feedback on v1.0.0.

**Prompts are now editable text files.** On first launch the mod creates
`BepInEx/config/AIROG_DirectedUpdates/` containing:
- `doc_addendum.txt` — what teaches the first-pass GM *how* to write instructions.
- `injection_template.txt` — what is handed to the second-pass updater
  (`{entity}`, `{instruction}` placeholders).
- `README.txt` — explains both files.

Edit them in any text editor: real multi-line text, no `\n` escaping, and lines
starting with `#` are comments stripped before the text reaches the model. Files are
re-read whenever their timestamp changes, so a tweak applies on the **next AI call —
no game restart**. Delete a file to restore its default. `UsePromptFiles = false` in
the .cfg falls back to the old config-string behaviour.

*Upgrade note:* if you had customised `DocAddendum` / `InjectionTemplate` in the .cfg,
your text is what seeds the new .txt files; otherwise you get the improved defaults
below. The .cfg keys remain as the fallback values.

**Better default prompts**, targeting the two v1.0.0 complaints:
- Instructions came out as *narration* ("she unbuckled her armour and left it on the
  table"). The doc addendum now demands a terse imperative directive naming the
  specific detail or status to change, under ~15 words, explicitly *not* a retelling
  of the story.
- The updater followed instructions too literally and threw away description detail.
  The injection now says to treat the instruction as a directive rather than text to
  paraphrase, to change only what it calls for, and to keep every other existing
  detail (appearance, gear, traits) intact.

**Instruction shown on the icon tooltip.** With "confirm details updated" turned on,
hovering the icon now reads `Goblin scavenger: Details updated (add a cowering in fear
status effect)` — so a dumb instruction is visible before you click it, without
digging through the log.
- New hook: `GameLogViewObj.AddAiDecisionIcon` (prefix) — appends the pending
  instruction to the tooltip at icon creation. Peeked, not consumed, so the update
  still receives it; patched at creation rather than `AiDecisionIcon.Init` so a save
  reload can't append a second copy.
- Config `[Tooltip]`: `ShowInstructionInTooltip` (default true), `TooltipTemplate`
  (default `{tooltip} ({instruction})`; also `{tooltip_raw}`, `{entity}`; TMP rich
  text works), `TooltipMaxChars` (default 160, 0 = no limit).

## v1.0.0 (07/16/2026)

Initial release. Lets the GM (first-pass unified model) attach a short imperative
instruction to each `update_entities` entry, and forwards that instruction into the
follow-up per-entity state-update prompt.

**Why:** vanilla flags an entity for update but the second model re-derives *what*
changed from raw story text with no guidance, so status updates often do nothing or
the wrong thing. With this mod the GM can say `{"name": "Kara", "instruction":
"escalate the tiredness status"}` and the updater is told to follow it. Because the
GM sees the player's narrative rules, scenario rules like "after X happens, do Y to
the player's status" now propagate to the actual update step.

**Hooks (all isolated via AIROG_Core SafePatch):**
- `UnifiedPromptBuilder.BuildApiDocsFromManifest` (prefix) — appends documentation of
  the optional `"instruction"` field to the `api_update_entities` doc string
  (idempotent; survives prompt reloads).
- `UnifiedResponseParser.ProcessApiAction` (postfix) — after vanilla resolves
  `update_entities` entries, captures `entry["instruction"]` keyed by resolved entity
  uuid. Plain-string entries and instruction-less objects behave exactly as vanilla.
- `AIAsker.GetEntityStateChanges` (prefix) — appends
  `GM INSTRUCTION for this update of {entity}: {instruction}` to the story text used
  in the `update_entity_state3` prompt. Instructions are consumed on use.

**Persistence:** pending instructions are saved to `directed_updates_pending.json` in
the active save directory (covers save/quit while a "confirm details updated" icon is
pending). Entries expire after 240 minutes by default.

**Config** (`BepInEx/config/com.airog.directedupdates.cfg`):
- `Enabled` — master switch (default true)
- `PersistPendingInstructions` (default true)
- `InstructionExpiryMinutes` (default 240; 0 = never)
- `DocAddendum` — text appended to the GM-facing API doc
- `InjectionTemplate` — text appended to the updater prompt (`{entity}`,
  `{instruction}` placeholders; `\n` for newlines)
