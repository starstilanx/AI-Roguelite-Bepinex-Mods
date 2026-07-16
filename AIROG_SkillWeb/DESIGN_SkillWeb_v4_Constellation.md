# SkillWeb v4.0 — "Constellation" Overhaul Design Document

**Status:** Draft for review — 07/06/2026
**Supersedes:** v3.x native-perk bridge (kept as a subsystem), v2.x standalone web (deprecated 06/08/2026, restored here in evolved form)
**Assets:** `Assets/SkillWeb_bkg.png`, `Assets/SkillRingBasic.png`, `Assets/PassiveSkillRingNotable.png`, `Assets/SkillRingKeystone.png`, `Assets/PassiveSkillRing.jpg`

---

## 1. Vision

Players loved the v2 skill web because it was *theirs* — an AI-grown, lore-soaked constellation of passives unique to each run. We deprecated it because it fought the game: after the 06/06 build shipped a native perk tree, v2's parallel point economy, parallel narrative injection, and parallel tree structure all became redundant or conflicting. v3 fixed the conflict by shrinking to an invisible stat bridge — architecturally correct, but it deleted the thing players actually cared about: **a big, beautiful, explorable web**.

v4 is neither a rollback nor a compromise. It is a **fusion**:

> The native perk tree keeps everything it does well (AI tree generation, learn/activate flow, respec, narrative prompt injection). The Constellation embeds those native perks as **Anchor Stars** and grows a vast mechanical web *around* them — hundreds of small passives, named Notables, run-warping Keystones — with its own earned currency, its own full-screen star-map UI, and deep hooks into the rest of the AIROG mod suite.

Design pillars:

1. **The web is the spectacle.** Full-screen, pannable, zoomable star map. Every unlock should feel like lighting a star.
2. **Never fight the native game.** Native perks are imported, never duplicated. No competing narrative injection — all narrative flows through AIROG_GenContext. No XP double-dipping — Resonance (the web currency) comes from sources the native economy doesn't touch.
3. **Grown, not authored.** The web is AI-generated from the run's universe, world background, player background, and (via Chronicle) the story so far. Two runs never produce the same web.
4. **Mechanics with honest surface area.** The one proven mechanical hook is attribute injection (`GetAttributeValAfterItemBonuses`). Everything mechanical reduces to attributes plus GenContext narrative directives — the design commits to that surface rather than promising effects we can't deliver.
5. **Cheap by default.** Generation is lazy, batched, cached, and always has a deterministic offline fallback (v3's heuristic deriver, promoted to a full node generator).

---

## 2. History — what each version got right and wrong

| | v2 (standalone web) | v3 (native bridge) | v4 (Constellation) |
|---|---|---|---|
| Visual web UI | ✅ PoE-style renderer, the beloved part | ❌ removed | ✅ restored & rebuilt (§8) |
| Point economy | ⚠️ `GainXp` patch double-dipped native perk points | ❌ none | ✅ Resonance, disjoint sources (§6) |
| Narrative injection | ⚠️ raw prompt patches, clashed with native perk injection | ❌ none | ✅ via GenContext `IContextProvider` (§7.2) |
| Native perk relationship | ❌ ignored it (predates it) | ✅ bridged stats onto it | ✅ imports perks as Anchor Stars (§5.3) |
| Stat injection | ✅ | ✅ (kept verbatim) | ✅ (kept verbatim) |
| AI stat derivation | ✅ per-node gen | ✅ heuristic + refine queue | ✅ both, extended (§5.5, §9) |
| Cross-mod hooks | ❌ | ❌ | ✅ Chronicle/Reverie/Insight/Settlement (§10) |

Everything below that touches native internals (perk point formula `1 + level/2 - learnedPerkCount`, `ViewPerksModal.RefreshView` as the sync signal, `PerkNode.isLearned/isActivated`, attribute enum `SS.PlayerAttribute` = Strength/Dexterity/Intellect/Cunning/Charisma) is verified against the current 06/15 build via the v3 code that already ships.

---

## 3. Architecture overview

Three layers, strictly separated:

```
┌─────────────────────────────────────────────────────────────┐
│  PRESENTATION   ConstellationUI (star map), tooltips,       │
│                 path preview, unlock FX, discipline legend  │
├─────────────────────────────────────────────────────────────┤
│  STRUCTURE      WebGraph: Sectors (disciplines), Rings,     │
│                 Nodes (Basic/Notable/Keystone/Anchor/       │
│                 Confluence), Edges, deterministic layout    │
├─────────────────────────────────────────────────────────────┤
│  EFFECTS        Attribute bonuses → GetAttributeVal patch   │
│                 Traits & Keystone rules → GenContext        │
│                 Resonance economy & event hooks             │
└─────────────────────────────────────────────────────────────┘
```

New/changed source layout (partial-class pattern per the 07/04 god-object guidelines — check external refs before splitting):

```
AIROG_SkillWeb/
  SkillWebPlugin.cs                 (thin: Awake, patches, lifecycle — extends AIROG_Core.BaseModPlugin)
  SkillWebPlugin.Economy.cs         (Resonance sources & spend/refund)
  SkillWebPlugin.Sync.cs            (anchor import, native-tree sync — evolved v3 SyncBonuses)
  Web/WebGraph.cs                   (nodes, edges, sectors, rings; query & pathing)
  Web/WebLayout.cs                  (deterministic polar layout, seeded per save)
  Web/WebGrower.cs                  (frontier expansion orchestration; evolved SkillWebGenerator)
  Web/NodeEffects.cs                (stat aggregation, trait registry, keystone rule table)
  Gen/NodeGenAI.cs                  (prompt builders + JSON schema parsing; evolved PerkStatDeriver.ViaAI)
  Gen/NodeGenOffline.cs             (deterministic fallback generator; evolved Heuristic)
  Gen/ThemeLexicon.cs               (genre-agnostic naming, pattern borrowed from GrandStrategy)
  UI/ConstellationUI.cs             (window shell, input, pan/zoom)
  UI/ConstellationUI.Render.cs      (node/edge drawing, sprites, LOD)
  UI/ConstellationUI.Panels.cs      (side panel, tooltip, legend, search)
  SkillWebData.cs                   (v4 schema + v3 migration)
  SkillWebConfig.cs
  ContextProvider.cs                (GenContext IContextProvider)
```

---

## 4. Data model

```csharp
enum WebNodeType { Basic, Notable, Keystone, Anchor, Confluence }

class WebNode {
    string id;                       // GUID; for Anchors: "anchor:" + native perk uuid
    WebNodeType type;
    string name;                     // AI or lexicon generated ("Iron Vigil", "Whisperstep")
    string description;              // 1–2 lore-grounded sentences
    string sectorId;                 // owning discipline (null for the Origin & inter-sector Confluences)
    int ring;                        // 0 = Origin, 1..N outward
    float angle;                     // polar position within layout (persisted so layout never reflows)
    List<string> edges;              // adjacent node ids (undirected)
    Dictionary<string, float> stats; // attr name → bonus (Basic: 1 attr; Notable: ≤2; Keystone: trade)
    List<string> traits;             // narrative trait strings (Notable/Keystone only)
    string keystoneRule;             // one-line rule directive for GenContext (Keystone only)
    bool unlocked;
    int tier;                        // 0 locked; 1..3 mastery (Basic/Notable only, §5.6)
    bool aiRefined;                  // false = offline-generated, queued for AI upgrade
    string originHook;               // provenance: "chronicle:<beatId>", "reverie:<dreamId>", "insight:<npcUuid>", null
}

class WebSector {                    // a "discipline" — one radial slice of the web
    string id, name, purpose;
    string colorHex;                 // constellation line & glow tint
    float angleCenter, angleSpan;    // slice of the circle it owns
    int deepestGeneratedRing;        // lazy growth frontier (§5.4)
    string anchorPerkTreeUuid;       // native PerkTree this sector grew from, if any
}

class SkillWebDataV4 {
    int schemaVersion = 4;
    long layoutSeed;                 // stable per save; drives all layout determinism
    List<WebNode> nodes;
    List<WebSector> sectors;
    int resonance;                   // unspent currency
    int resonanceEarnedTotal;
    Dictionary<string, long> economyLedger;   // source key → total granted (idempotency, §6.3)
    Dictionary<string, PerkBonus> perkBonuses; // v3 carry-over: anchor stat bridge (unchanged)
    [JsonIgnore] Dictionary<SS.PlayerAttribute, float> CachedStats; // rebuilt on sync
}
```

Persistence stays a per-save sidecar at `SS.I.saveTopLvlDir/saveSubDirAsArg/SkillWeb.json` via `AIROG_Core.ModSaveFile`. **Migration:** a v3 file (no `schemaVersion`) loads its `perkBonuses` intact; first sync then runs *Anchor import* (§5.3) and seeds the initial web around them, so existing saves upgrade in place with zero loss — their learned-perk bonuses become their starting Anchor Stars.

---

## 5. The web structure

### 5.1 Geometry — sectors and rings

The web is a polar star map centered on the **Origin** (the player). Each **Sector** is a discipline occupying an angular slice; each **Ring** is a distance band from the Origin. Power and cost scale with ring; identity comes from sector.

- **Ring 0** — the Origin node (always unlocked; shows player name/level; purely cosmetic hub).
- **Rings 1–2** — Basic nodes, dense (5–7 per sector per ring). Small single-attribute bonuses (+2..+4).
- **Ring 3** — first Notables (1–2 per sector) plus Basics.
- **Rings 4–5** — Basics thin out, Notables regular, first **Keystone** per sector at ring 5.
- **Ring 6+** — frontier territory, generated on demand forever (§5.4). Keystones every ~3 rings per sector.
- **Sector borders** — where two sectors meet, occasional **Confluence** nodes blend both themes (§5.7).

Layout is computed by `WebLayout` **deterministically from `layoutSeed`**: nodes within a sector-ring cell get jittered polar positions (golden-angle offset + seeded jitter, min-distance enforced) so the web looks organic but never reflows between sessions. Positions are persisted anyway (belt and braces — a layout-algorithm tweak in v4.1 must not scramble existing saves).

Edges: every node connects to 1–2 nodes in the ring beneath it (nearest-angle within sector) and 0–2 lateral neighbors in the same ring. Keystones always have exactly one inbound edge — a deliberate chokepoint so reaching one is a visible journey across the map.

### 5.2 Node taxonomy

| Type | Sprite | Cost | Effect budget | Count |
|---|---|---|---|---|
| **Basic** | `SkillRingBasic.png` | 1 ⟡ | 1 attribute, +2..+4 | ~60% of web |
| **Notable** | `PassiveSkillRingNotable.png` | 2 ⟡ | ≤2 attributes totaling +6..+10, +1 narrative trait | ~25% |
| **Keystone** | `SkillRingKeystone.png` | 3 ⟡ | attribute *trade* (+10..+14 / −4..−6) + rule directive + trait | ~5%, gated |
| **Anchor** | `PassiveSkillRing.jpg` (gold tint) | — (mirrors native) | v3 `perkBonuses` bridge, unchanged | 1 per learned native perk |
| **Confluence** | Notable sprite, dual-color glow | 2 ⟡ | 2 attributes (one from each adjoining sector), fusion trait | rare, sector borders |

⟡ = Resonance. Unlock rule: a node is purchasable iff at least one edge-adjacent node is unlocked (Anchors count as unlocked the moment the native perk is learned — see next section for why that matters).

### 5.3 Anchor Stars — the native-perk fusion

This is the load-bearing idea of v4. On every sync (same triggers as v3: `AfterLoadOrNewGame` postfix, `ViewPerksModal.RefreshView` postfix):

1. For each native `PerkTree` on `playerCharacter.GetCurrentActor().playableData.perkTrees`, ensure a **Sector** exists themed on that tree (name/purpose derived from the tree's root perk; sector created on first sight of the tree). Trees the player never touches still get sectors — the web previews where they'd bloom.
2. For each **learned** native `PerkNode`, ensure an **Anchor node** exists in that sector, placed at a ring proportional to its depth in the native tree. Anchors are born unlocked and keep their v3 `PerkBonus` stat bridge (heuristic → AI refine queue, verbatim from v3.1 including the `_refineQueue` drain fix).
3. Anchors are **free adjacency seeds**: learning a native perk instantly opens the surrounding web territory for Resonance spending. Native progression and web progression feed each other without sharing a currency.
4. Native *active* perks (`isActivated`, max `maxActivePerks`) make their Anchor **radiate**: adjacent unlocked web nodes get the `ActiveBonusMultiplier` (v3's ×1.5, now positional instead of global). This makes the native activate/deactivate choice visible on the map — swap your active perks and watch different neighborhoods of the web glow.

Respec: native respec (essence_of_rewinding) un-learns perks → their Anchors dim to "dormant" (bonus off, adjacency revoked); web nodes already bought stay bought but may become disconnected islands, rendered desaturated and inert until reconnected. No refund-cascade complexity; dormancy is simple and legible.

### 5.4 Frontier growth — the web that never ends

The web is generated lazily, sector by sector, ring by ring:

- **Seeding (new game / migration):** rings 1–3 of every sector are generated in one batched AI call per sector (~8–10 nodes each). Offline generator fills instantly first; AI results overwrite names/descriptions/stats as they arrive (same "heuristic now, refine later" pattern players already accept from v3).
- **Growth trigger:** when the player unlocks any node in a sector's outermost generated ring, `WebGrower` generates that sector's next ring (one batched call). The frontier always stays exactly one ring ahead of the player — visible on the map as faint "unformed stars" at the edge (unnamed placeholder nodes at their real positions), which is both a cost control and a striking visual promise that the web is infinite.
- **Growth context:** each batch prompt includes world/universe lore, player background, the sector's purpose, names of the 5 nearest existing nodes (continuity), and — when AIROG_Chronicle is present — the current chapter summary, so a web grown during a war chapter sprouts war-scarred passives.

### 5.5 Generation pipeline

Two generators behind one interface; every node is playable the instant it exists:

- **`NodeGenOffline`** (always runs first, instant, deterministic from `layoutSeed`): composes names from `ThemeLexicon` (sector-keyword × ring-power tables, genre-resolved like GrandStrategy's lexicon), assigns stats from the sector's attribute affinity with seeded variance, and writes a serviceable one-line description. Marks `aiRefined = false`.
- **`NodeGenAI`** (async, batched, queued): one prompt per ring-batch returning a JSON array `[{name, description, stats:{}, traits:[], keystoneRule?}]`, parsed with the same brace-slice + tolerant number parsing as v3's `PerkStatDeriver`. Validation clamps every value to the type budgets in §5.2 — the AI proposes, the budget table disposes. Any failure leaves the offline node in place; the batch requeues once, then gives up quietly.
- Uses `AIAsker.GenerateTxtNoTryStrStyle(GENERAL_QUESTION_ANSWERER, …, GOOD_FOR_CORRECTNESS)` exactly as v3 does; single in-flight pass with a drain queue (v3.1's `_refineQueue` pattern generalized to batches).

### 5.6 Mastery tiers (Basic/Notable only)

Re-buying an unlocked node raises its tier (max 3): tier 2 costs 1 ⟡ (+50% stats), tier 3 costs 2 ⟡ (+100% stats, Notables gain a second trait roll). Rendered as concentric glow rings. This is v2's beloved `tier` field back, now with a real sink for late-game Resonance so the currency never goes dead even when a player stops exploring outward.

### 5.7 Keystones & Confluences — the build-defining picks

Keystones are the showpiece. Each is an attribute **trade** plus a **rule directive** — one sentence of always-on narrative law injected via GenContext, e.g.:

> **Oathbound Colossus** — +12 Strength, −5 Cunning. *Rule: {player} cannot retreat from combat once engaged, and NPCs know this about them.*

The rule directive rides GenContext's directive channel (same mechanism AIROG_Insight uses), so it shapes every AI adjudication without any extra patch surface. Keystone rules are the honest version of "mechanical effects we can't hard-code": the AI GM enforces them, which in this game *is* the mechanics. Cap: `MaxActiveKeystones` (default 3) — matching the native active-perk cap keeps the mental model consistent.

Confluences are the exploration reward for walking sector borders: a Blade-sector/Shadow-sector Confluence rolls one stat from each and a fusion trait ("Duelist of the Unseen Angle"). They exist to make the map's geography — not just its nodes — meaningful.

---

## 6. The Resonance economy

### 6.1 Design constraint

v2's fatal flaw was patching `PlayerCharacter.GainXp` and shadowing the native perk-point formula. v4's rule: **Resonance sources must be events the native economy ignores.**

### 6.2 Sources

| Source | Amount | Hook |
|---|---|---|
| Level up | 2 ⟡ | `GameplayManager.TurnHappenedEvent` + level delta check (no GainXp patch; poll `playerLevel` on turn tick) |
| Every 25 turns survived | 1 ⟡ | `TurnHappenedEvent` counter |
| Chronicle chapter completed | 3 ⟡ | AIROG_Chronicle event via UnifiedBridge (soft dependency) |
| NPCExpansion quest completed | 2 ⟡ | UnifiedBridge event (soft) |
| Reverie dream survived | 1–2 ⟡ | UnifiedBridge event (soft) |
| First visit to a new Place | 1 ⟡ | place-change detection on turn tick |
| Anchor import (each native perk learned) | 1 ⟡ | sync — learning native perks *feeds* the web |

Baseline math: a player at level 10, turn 300, with modest questing holds ~35–45 ⟡ earned — enough for rings 1–3 of two sectors plus one Keystone push. Tunable via a single `ResonanceMultiplier` config knob.

### 6.3 Ledger idempotency

Every grant writes to `economyLedger` under a stable key (`"level:12"`, `"chapter:3"`, `"place:<uuid>"`). Grants are skipped if the key exists — save-reload, multiplayer re-sync, and event replays can never double-pay. (Lesson imported from the Settlement turn-economy work.)

### 6.4 Respec

Web respec is deliberately cheaper than native respec: refund any *leaf* unlocked node (no unlocked neighbors depending on it for connectivity) for full cost, with a flat 1 ⟡ fee per session-batch of refunds. Keystones refund at half. This encourages experimentation — the web is where you play with builds; the native tree is where you make commitments.

---

## 7. Effects layer

### 7.1 Attribute injection (unchanged, proven)

`GetAttributeValAfterItemBonuses` postfix adds `CachedStats[attr]`, recomputed on every sync/unlock from: unlocked web nodes (× tier multiplier) + Anchor bridge bonuses (× radiance multiplier where applicable), clamped per-attribute by `MaxBonusPerAttribute` (raised default: 50). One patch, one number, easy to reason about, easy for players to verify on their character sheet.

### 7.2 Narrative via GenContext (new, replaces v2's raw patches)

`SkillWebContextProvider : IContextProvider` registered with AIROG_GenContext supplies:

- **Traits line:** "Web traits: {comma-joined traits of unlocked Notables/Confluences}" — capped at 12 traits by recency of unlock, so prompt cost stays bounded.
- **Keystone rules block:** each active Keystone's rule directive, verbatim.
- **Milestone color:** one line naming the player's deepest sector ("They are furthest advanced in the discipline of the Hollow Choir") — cheap flavor with outsized effect on generated prose.

If GenContext isn't installed, the provider no-ops; stats still work standalone. (Soft-dependency discipline per the mod-suite conventions.)

---

## 8. Constellation UI

Full rebuild on the v2 renderer's bones (pan/zoom/tooltip/side-panel code recovered from git `5668aa6`), opened from the existing "✦ Skill Web" EquipmentPanel button *and* a new button inside `ViewPerksModal` ("View Constellation" — the native tree and the web cross-link both ways).

- **Backdrop:** `SkillWeb_bkg.png` tiled with slow parallax drift; sector slices tinted with `colorHex` at 6% alpha so the map reads as territories from max zoom-out.
- **Nodes:** the four ring sprites by type; unlocked = full color + additive glow pulse; purchasable = rim-lit breathing highlight; locked = 40% gray; frontier "unformed stars" = 15% alpha dots; dormant Anchors = desaturated gold.
- **Edges:** 2px lines, sector-tinted when both ends unlocked ("lit constellation lines"), dim gray otherwise. Anchor radiance renders as a soft halo bleeding onto neighbors.
- **Path preview (new):** hovering any locked node draws the cheapest unlock path from your lit web (BFS over edges, cost-weighted) and shows total ⟡ — the single biggest UX upgrade over v2, turns the map into a planning tool.
- **Side panel:** node name/description/stats/traits/rule, unlock & upgrade buttons with cost, provenance line for hooked nodes ("Crystallized from Chapter 3: The Burning of Vhal").
- **Header HUD:** Resonance count, ledger tooltip (where every point came from), search box (name/trait/stat filter dims non-matches), legend.
- **Unlock moment:** 0.4s star-ignition flash + edge-light ripple to neighbors. Cheap (UI tween, no particles) but this is the "show off" beat — it must feel good.
- **LOD:** below 40% zoom, hide Basic node labels and collapse tooltips; the map stays smooth even at 500+ nodes (v2 got sluggish past ~120; node widgets pool and cull off-screen).
- **Input:** drag-pan, wheel-zoom to cursor (0.25×–4×), ESC closes (InputLegacyModule ref already present), double-click centers on Origin, `F` frames your lit web.

---

## 9. Cost & performance budget

- **AI calls:** seeding = 1 call/sector (~5 sectors) at new game, spread over the first minutes via the drain queue; growth = 1 call per ring per sector, player-paced; anchor refinement = v3's existing per-perk queue. Worst-case steady state is well under one call per 10 player turns. `UseAIGeneration=false` runs the entire mod on the offline generator — fully playable, zero tokens.
- **Prompt size:** batch prompts ≤ ~700 tokens in, responses capped by asking for ≤10 nodes; GenContext contribution capped (§7.2).
- **Runtime:** sync is O(nodes) dictionary work, no allocation-heavy LINQ in per-frame paths; UI pools widgets; no per-frame raycasting beyond Unity's event system.

---

## 10. Cross-mod integration matrix (all soft dependencies via UnifiedBridge)

| Mod | Gives SkillWeb | Gets from SkillWeb |
|---|---|---|
| **Chronicle** | chapter-complete ⟡; chapter summaries as growth context; landmark beats can crystallize a commemorative Notable at the frontier (`originHook`) | deepest-sector line for chapter prose |
| **Reverie** | dream-survived ⟡; a nightmare can seed a hidden "Dream-Touched" node visible only after the dream (marked with dream provenance) | active Keystone rules can color dream generation |
| **Insight** | a deepened NPC insight can gift a taught Notable in the sector matching the NPC's expertise | — |
| **NPCExpansion** | quest-complete ⟡ | web traits visible to NPC gossip system |
| **Settlement** | a Library/Academy-class building grants +1 ⟡ per economy cycle | — |
| **Multiplayer** | — | `SkillWeb.json` syncs like other sidecars; CachedStats recompute client-side after sync (needs a sync-manifest entry, flagged for the MP maintainer) |
| **GenContext** | — | trait line + keystone rules + milestone color (§7.2) |

Every integration is: *if the other mod's event exists on the bridge, subscribe; otherwise silently skip.* No hard references.

---

## 11. Config surface (`SkillWebConfig.json`, defaults shown)

```jsonc
{
  "AllowStatBonuses": true,
  "UseAIGeneration": true,          // false = offline lexicon generator only
  "ResonanceMultiplier": 1.0,       // scales all §6.2 sources
  "MaxBonusPerAttribute": 50.0,
  "ActiveBonusMultiplier": 1.5,     // anchor radiance (kept from v3)
  "MaxActiveKeystones": 3,
  "SectorsAtStart": 5,              // clamped to native perk-tree count when higher
  "SeedRings": 3,
  "MasteryTiersEnabled": true,
  "CrossModHooks": true,            // master switch for §10
  "FrontierPreview": true,          // unformed-star rendering
  "HeuristicBudget": 6.0            // offline stat budget (kept from v3)
}
```

---

## 12. Implementation phases

**Phase 1 — Skeleton (v4.0.0-alpha):** data model + migration, layout engine, offline generator, Resonance economy with level/turn sources, stat injection, minimal map UI (pan/zoom/unlock, no FX). *Playable end-to-end with zero AI calls.* — the critical de-risk milestone.

**Phase 2 — The Fusion (v4.0.0):** Anchor import, sector-from-perk-tree theming, dormancy, radiance; AI generation pipeline (seed + frontier batches + refine queue); GenContext provider; ViewPerksModal cross-link button.

**Phase 3 — The Spectacle (v4.1.0):** unlock FX, path preview, search, legend, LOD/pooling, frontier unformed-stars, ledger tooltip; mastery tiers; respec.

**Phase 4 — The Suite (v4.2.0):** UnifiedBridge hooks (Chronicle/Reverie/Insight/NPCExpansion/Settlement), Confluence nodes, provenance rendering, multiplayer sync manifest entry.

Each phase ships independently; the config's soft switches mean a phase-2 build with phase-4 flags off is a complete product.

---

## 13. Risks & open questions

1. **Native tree regeneration** (`ViewPerksModal.OnRegen` / new-game `NgPerks`) can replace perk trees wholesale → orphaned sectors/anchors. Mitigation: sectors keyed by `anchorPerkTreeUuid`; on sync, sectors whose tree vanished go dormant (like anchors) rather than deleting player-purchased nodes. Needs an in-game test against OnRegen.
2. **Attribute cap interactions:** +50 per attribute on top of items may trivialize checks at high level. The clamp is config-exposed; playtest at levels 15–20 before choosing the shipped default.
3. **Prompt-budget creep:** GenContext line + keystone rules could bloat every AI call. Hard caps in §7.2; GCHelper already trims DMNotes, and we should verify combined budget with Insight + Chronicle + WorldExpansion all active.
4. **v3-only users:** anyone who liked the invisible bridge can set `SectorsAtStart: 0` + `UseAIGeneration: false`… but that's a degenerate config; simpler to accept the map is now core. Decision needed: keep a "bridge-only mode" flag or not (recommend **not** — one product, one identity).
5. **Save-file growth:** 500 nodes ≈ ~250 KB JSON — fine, but the ledger grows unboundedly; prune ledger keys older than the newest 500 on save.
6. **Turn-tick level polling** (§6.2) assumes `TurnHappenedEvent` fires reliably in all modes — confirmed for normal play in the multiplayer work, but verify during combat-heavy sessions.

---

## 14. The pitch (player-facing summary, for the announcement post)

> **The Skill Web returns — bigger than it ever was.** Your character's perks are now stars in a living constellation that grows out of *your* story. Every discipline your world dreams up becomes a territory of the night sky; every perk you learn ignites an Anchor Star with a whole neighborhood of passives around it; every chapter of your saga, every quest, every dream you survive feeds Resonance to spend lighting your way outward — toward the Keystones: build-defining oaths that your world's AI game-master will hold you to. The web never ends. No two players will ever grow the same one.

---

## 15. Usable Keystone Abilities (v4.1.0 — implemented 2026-07-07)

Keystone (and optionally Confluence) unlocks now grant a **usable active ability**, castable on any object/creature/place through the game's native interaction pipeline.

- **Source:** `NodeGrantsAbility(node)` = unlocked `Keystone`, or unlocked `Confluence` when `SkillConfig.AbilitiesFromConfluences`.
- **Bridge:** each qualifying node mints a native `GameAbility(AbilityType.LEARNED)` (`SkillWebPlugin.Abilities.cs`). Minted with `skipAddingToUuidMap:true`, then re-keyed to a stable `WebNode.grantedAbilityUuid` and registered in `uuidToGameEntityMap` — so its on-disk art cache (keyed by uuid) survives across sessions. **Not** added to `abilityPool`: stays out of the native level-gated learned slots and out of the native save. SkillWeb re-mints each session and owns them entirely.
- **Reconciliation:** `SyncAbilities()` runs at the tail of `SyncBonuses()` (load + every unlock/refund) — mints for newly-qualifying nodes, tears down (`GameAbility.TearDown()`) refunded/dequalified ones.
- **Description:** AI-generated once via `AIAsker.GetAbilityDescFromStory(manager, name, seed+recentStory)`, cached on `WebNode.grantedAbilityDesc` (no repeat API cost).
- **Cooldown:** handled natively — `GameEntity` self-subscribes `TurnHappened` (ticks `cooldownTurnsRemaining` down), and the resolution path calls `ability.SetAsUsed()` at `GameplayManager.cs:10946`. `PersistAbilityCooldowns()` (in `OnTurnHappened`) mirrors the live value onto `WebNode.abilityCooldownRemaining` so it survives save/load; restored on mint.
- **UI:** `SkillAbilityBar` (own screen-overlay canvas, sortingOrder 450) — a compact list opened by the "✦ Abilities" button next to "✦ Skill Web" on the EquipmentPanel. Row click → `PrePrepareToReceive(ability)` + `PrepareToReceive(ability)` (same handoff as the inventory ability picker). Respects `PlayerInteractionsDisabled()`, cooldown, and `MaybeShowModalForNotEnoughSurvivalBarForAbil`.
- **Verified (by source trace, not runtime):** the click→resolution path (`SelectableThingy.PrepareToReceiveAbility` → `InteractionLogic.PlayerInvokeAbilWithShiftLogic` → `HandleInteracteeLogicForAbility`) uses the ability object directly with **no pool/loadout membership check**, so a pool-less minted ability resolves cleanly (roll modifier +5 for LEARNED, `SetAsUsed` cooldown, AI narrative all apply).
- **Config:** `GrantUsableAbilities` (master), `AbilitiesFromConfluences`.
- **Still needs in-game playtest:** button placement/visuals and end-to-end cast flow (can't be driven headlessly).
