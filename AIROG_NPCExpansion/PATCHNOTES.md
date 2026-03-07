# NPC Expansion — Patch Notes

---

## v4.0.0 — *The Living World Update*

> *"They were never just set dressing."*

This is the largest update to NPC Expansion since its creation. Eight interconnected systems have been added on top of the existing lore generation, autonomy engine, and nemesis framework. NPCs now remember, react, gossip, grieve, speak unprompted, earn reputations, and give quests that the AI itself resolves.

---

### ⚔️ Faction-Sentiment Bridge
Your relationship with an NPC is no longer just a number in a log. Every affinity change — gifts, attacks, conversations — is now synchronized into the game's native `sentimentV2` system, directly affecting combat behavior, merchant pricing, and faction standing. NPCs you've befriended fight differently. NPCs you've wronged remember.

---

### 🌊 Social Ripple Effects
Actions have witnesses. When you help or harm an NPC, nearby characters who care about that person will react. A bystander who adores your target will warm to you. One who hates them might quietly approve. The ripple scales with bond strength — close allies react strongly, loose acquaintances barely at all. Watch the game log.

---

### 🧠 NPC Memory Synthesis
Every 10 turns, the AI distills recent story events into concrete memories for nearby NPCs. These aren't generic observations — they're drawn directly from `storyTurnHistoryV2`, the actual turns of your run. Over time, an NPC's long-term memory becomes a compressed journal of what they've actually witnessed. Examine them to see it grow.

---

### 👁️ Reputation System
NPCs earn reputation tags through behavior — not assignment. An NPC who auto-equips for combat might become *"battle-hardened."* One who sold off surplus goods earns *"shrewd trader."* A Nemesis who killed you is permanently marked. Up to five tags accumulate per NPC and are injected into every AI prompt, shaping how they speak and act going forward.

---

### 💀 Death Tracking & Hall of the Fallen
Named, lore-generated NPCs no longer disappear when killed — they're remembered. Their death is recorded with cause, last known goal, and an AI-generated epitaph. Nearby NPCs with bonds to the fallen will grieve or celebrate accordingly, with affinity changes and new memories. A **Hall of the Fallen** panel (accessible from any NPC action menu) memorializes every lost character across the run.

---

### 📜 Rumor Network
Information spreads. Every 3 turns, NPCs in the same location share facts with one another — scenario updates, things the player told them, events they witnessed. A piece of news seeded with one NPC can reach others organically without any player involvement. Ask an NPC what they know; the answer may surprise you.

---

### 🗣️ NPC Barks
Every 5 turns, nearby NPCs have a 40% chance to mutter something aloud — fully AI-generated, character-specific ambient dialogue drawn from their personality, current situation, current goal, and relationship status. Nemeses are especially vocal. Barks appear in the game log and respect an 8-turn cooldown so no one becomes a chatterbox.

---

### 📋 AI Quest System
NPCs with generated lore can now give quests. Select **Accept Quest** from any NPC's action menu and the AI will generate a complete quest on the spot — an objective, a specific completion condition, a narrative reward, and a gold amount — all derived from that NPC's personality and current situation.

After every story turn, the AI checks whether recent events fulfilled any active quest's condition. Completion is detected automatically; no button to press. Rewards include gold, an affinity boost with the giver, and a new memory entry for them.

Quests fail automatically if their giver is killed or if a deadline (when set) passes. Open the **Quest Log** from any NPC action menu to track everything.

---

### 🪨 Lore Button Asset
The bottom-bar lore button is now a hand-crafted stone tablet pulled from `StreamingAssets/NPCExpansion/LoreButton.png`. It sits quietly at the edge of the NPC dropdown — neutral when no lore exists, cyan-tinted when lore is present and ready to edit, gold-pulsing while generating.

---

### Under the Hood
- All new systems are driven by `ScenarioUpdater`'s global turn counter — no new hooks required
- Rumor propagation, bark ticks, and memory synthesis each run on independent intervals (3 / 5 / 10 turns)
- Quest data, memorial records, and rumor facts are all persisted to the save directory alongside existing lore files
- `NPCProvider` in GenContext now injects reputation tags, known facts, and active quest context into every relevant AI prompt at a cost of ~35–70 tokens overhead
- Full fallback path for all asset loading; missing assets degrade gracefully

---

## v1.x — Legacy

Earlier versions established the core systems this update builds on:
- AI lore generation (personality, scenario, skills, abilities, attributes)
- NPC autonomy (auto-equip, self-preservation, economic activity)
- Nemesis system (promotion on player death, persistent threat escalation)
- Affinity & relationship tracking
- NPC Examine UI, Equipment UI, and lore editor
- Faction bridge (sentimentV2 sync)
- Gear system (armor damage reduction, weapon damage scaling)
