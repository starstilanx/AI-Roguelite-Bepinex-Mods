Audio & TTS

Deepgram & Gemini TTS: These plugins replace the game's default TTS with high-quality alternatives. They map in-game speaker types (Player, NPC, Monster) to specific AI voices listed by their respective APIs. (Will work on sapphire.)

Music Expansion: A simple plugin that allows users to place .mp3 or .wav files in specific folders to have them automatically shuffled into the game's ambient and combat playlists. (Will work on sapphire)

Visuals & UI

Font Plugins: These handle the complex task of replacing fonts in Unity's TextMeshPro system. It allows real-time switching via an in-game dropdown. (Will work on sapphire)

Nano Banana: The image generation plugin. It bypasses the game's default image generation to use Google's Gemini Imagen models, which offer specific features like automatic background removal for characters. (Will work in conjunction with sapphire)

Gameplay Mechanics

NPC Expansion: Probably the most complex plugin. It makes NPCs "real" by giving them their own agendas, inventories, and equipment. They can now take turns simultaneously with the player, behaving more like party members or living world inhabitants. (It takes a lot of tokens to run, so it's probably not currently viable on Sapphire)

Skill Web: Adds a new layer of progression. It creates a massive, procedurally generated tree of upgrades that players can navigate as they level up, offering both combat stats and narrative flair. (Almost certain that it will work on Sapphire)

Settlement: Transforms the game from a personal adventure into a management sim. Players can found towns, build structures, and manage resources, all with AI-supported descriptions and events. (Will work on Sapphire)

World & Lore

World Expansion: Introduces a "background tick" system. Even if the player is just standing still, the world's economy fluctuates, and events (wars, natural disasters, etc.) happen and are logged in a special tab within the journal. (Relatively lightweight at the start; might need trimming as world events continue to occur. Context might be lost, but the latest events will still be recorded. Will work on Sapphire)

History Tab: Solves the "AI amnesia" problem by creating a concise, persistent history of world events that is automatically squeezed into every AI prompt, ensuring the AI "remembers" the journey. (Extremely heavy on token counts and needs revision)

Utilities & Fixes

Loop Be Gone: A vital utility for long-form play. It uses mathematical similarity checks (N-grams and Levenshtein distance) to catch when the AI starts repeating itself, preserving immersion.

Token Modifiers: Allow advanced users to control "how much" the AI talks. Higher token limits mean longer, more detailed descriptions but also higher API costs. (Definitely doesn't work on sapphire)

Preset Exporter: A streamlined way for scenario creators to share their "world rules" with others. (Works regardless of any mode)
