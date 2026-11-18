using System;
using System.Collections.Generic;
using System.Linq;
using AIROG_Core;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace AIROG_DirectedUpdates
{
    /// <summary>
    /// Lets the first-pass GM attach a per-entity "instruction" to update_entities entries,
    /// and forwards that instruction into the follow-up update_entity_state prompt. Vanilla
    /// re-derives WHAT changed from raw story text with no guidance, which is why status
    /// updates often do nothing or the wrong thing. Hooks:
    ///   1. BuildApiDocsFromManifest prefix — documents the optional fields to the GM.
    ///   2. ProcessApiAction postfix — captures them off the resolved entries (vanilla parser
    ///      reads entry["name"] and ignores unknown fields, so plain strings and field-less
    ///      objects keep working on models that don't emit them).
    ///   3. AddAiDecisionIcon prefix — moves the captured text onto the icon the game raises
    ///      for that entry (see Stash) and shows it on the icon tooltip, so a bad instruction
    ///      is visible in-game instead of only in the log.
    ///   4. ResolveStoryForDesc postfix + GetEntityStateChanges prefix — when that icon runs,
    ///      appends its instruction to the story text the updater model sees.
    /// The same pass-through carries a "destination" from npcs_leave entries to the step that
    /// picks where a departed character went (Destinations.cs), and PlayerInstruction.cs lets
    /// the player issue an instruction directly from the entity right-click menu.
    /// All injected prompt texts live in editable .txt files under
    /// BepInEx/config/AIROG_DirectedUpdates/ (see PromptFiles).
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class DirectedUpdatesPlugin : BaseModPlugin
    {
        public const string PLUGIN_GUID = "com.airog.directedupdates";
        public const string PLUGIN_NAME = "Directed Updates";
        public const string PLUGIN_VERSION = "1.2.0";

        private const string API_KEY = "api_update_entities";
        private const string API_ACTION = "update_entities";
        private const string LEAVE_API_KEY = "api_npcs_leave";
        private const string LEAVE_ACTION = "npcs_leave";

        private const string STASH_MARKER = "AIROG_DirectedUpdates";
        internal const string STASH_GM = "gm";
        internal const string STASH_PLAYER = "player";
        internal const string STASH_DESTINATION = "destination";

        public static DirectedUpdatesPlugin Instance { get; private set; }

        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> PersistPending;
        public static ConfigEntry<int> ExpiryMinutes;
        public static ConfigEntry<bool> UsePromptFiles;
        public static ConfigEntry<string> DocAddendum;
        public static ConfigEntry<string> InjectionTemplate;
        public static ConfigEntry<bool> ShowInstructionInTooltip;
        public static ConfigEntry<string> TooltipTemplate;
        public static ConfigEntry<int> TooltipMaxChars;
        public static ConfigEntry<bool> PlayerInstructionEnabled;
        public static ConfigEntry<PlayerInstruction.QuickModifierKey> PlayerInstructionQuickModifier;
        public static ConfigEntry<bool> DestinationsEnabled;
        public static ConfigEntry<bool> ShareSameDestination;

        /// <summary>
        /// Default contents of doc_addendum.txt. Deliberately harder on "terse directive, not
        /// narration" than the v1.0.0 config default below: models that wrote the instruction as
        /// a story beat ("she unbuckled her armor and left it on the table") caused the updater
        /// to rewrite descriptions as narration.
        /// </summary>
        private const string DEFAULT_DOC_ADDENDUM =
            "An entry may instead be an object: {\"name\": \"[entity]\", \"instruction\": \"[directive]\"}. " +
            "The instruction is a terse imperative directive describing ONLY the change to make to that entity's " +
            "record — e.g. 'add a burn status effect', 'escalate the tiredness status', 'replace the plate armor " +
            "in the description with the linen shirt they changed into', 'rename to Sir Aldric'. It is NOT " +
            "narration: do not retell the story, describe the scene, or explain why. Keep it under about 15 " +
            "words and name the specific detail or status to change. Include \"instruction\" whenever the story " +
            "or the narrative rules imply a specific required change — it is passed verbatim to the update step.";

        /// <summary>Default contents of injection_template.txt. Placeholders: {entity}, {instruction}.</summary>
        private const string DEFAULT_INJECTION_TEMPLATE =
            "GM INSTRUCTION for this update of {entity}: {instruction}\n" +
            "Apply this instruction to {entity}'s record. Treat it as a directive about what to change, not as " +
            "text to paraphrase into the description. Reflect it in the updated description and/or status effect " +
            "commands, change only what it calls for, and keep every other existing detail (appearance, gear, " +
            "traits) intact — do not drop details the instruction does not explicitly replace.";

        /// <summary>
        /// Default contents of player_injection_template.txt. Unlike a GM instruction, the story
        /// text usually says nothing about the change, and the stock prompt tells the updater to
        /// leave everything UNCHANGED in that case, so this has to say outright that it wins.
        /// </summary>
        private const string DEFAULT_PLAYER_INJECTION_TEMPLATE =
            "PLAYER INSTRUCTION for this update of {entity}: {instruction}\n" +
            "This is a direct out-of-character instruction from the player. Apply it to {entity}'s record the " +
            "same way as a GM instruction, even if the story events above do not mention it; it takes precedence " +
            "over them. Treat it as a directive about what to change, not as text to paraphrase into the " +
            "description. Carry it out in the updated name, description, type and/or status effect commands as " +
            "it calls for. A status effect it asks for must be added or removed exactly as asked. Change only " +
            "what it calls for, and keep every other existing detail (appearance, gear, traits) intact. Do not " +
            "drop details the instruction does not explicitly replace.";

        /// <summary>
        /// Default contents of destination_doc_addendum.txt (GM-facing, npcs_leave). Kept short:
        /// it is sent with every unified story prompt.
        /// </summary>
        private const string DEFAULT_DESTINATION_DOC_ADDENDUM =
            "An entry may instead be an object: {\"name\": \"[entity]\", \"destination\": \"[place]\"}. " +
            "The destination is a brief name or few-word description of where the player would next encounter " +
            "that character (an existing location's name when possible), NOT a full path. Omit it by default; " +
            "include it only when the story or the narrative rules say where they go, they arranged to meet " +
            "the player somewhere, or several characters leave together (give each the identical text).";

        /// <summary>Default contents of destination_injection_template.txt. Placeholders: {entity}, {destination}.</summary>
        private const string DEFAULT_DESTINATION_INJECTION_TEMPLATE =
            "GM DESTINATION for {entity}: {destination}\n" +
            "The GM who wrote the passage states that this is where {entity} went, that is, the place where the " +
            "player would next encounter them. Follow it over your own inference from the passage. If it names " +
            "or clearly describes a location that already exists in the information above, output the path to " +
            "that existing location and use its exact existing name. Otherwise add a new location with a short " +
            "fitting name where this guidance and the passage imply.";

        // v1.0.0 config defaults. Kept verbatim as the ConfigEntry defaults so that on upgrade we
        // can tell "player never touched this" (→ seed the .txt with the improved default above)
        // from "player customised it" (→ seed the .txt with their text).
        private const string LEGACY_DOC_ADDENDUM =
            "An entry may instead be an object {\"name\": \"[entity]\", \"instruction\": \"[short imperative directive " +
            "for HOW to update, e.g. 'add a burn status effect', 'escalate the tiredness status', 'update outfit in " +
            "description', 'rename to X']\"}; include \"instruction\" whenever the story or the narrative rules imply " +
            "a specific required change — it is passed verbatim to the update step.";

        private const string LEGACY_INJECTION_TEMPLATE =
            "GM INSTRUCTION for this update of {entity}: {instruction}\\n" +
            "Follow this instruction faithfully, reflecting it in the updated description and/or status effect commands.";

        protected override void Awake()
        {
            base.Awake();
            Instance = this;

            Enabled = Config.Bind("General", "Enabled", true,
                "Master switch. When false, all hooks pass through to vanilla behavior.");
            PersistPending = Config.Bind("General", "PersistPendingInstructions", true,
                "Also write captured GM instructions/destinations that have no icon yet to the save directory. " +
                "Once the game shows the icon the text is stored on the icon itself (and so inside the save), " +
                "which is what keeps it across a save/quit while a 'confirm details updated' icon is pending.");
            ExpiryMinutes = Config.Bind("General", "InstructionExpiryMinutes", 240,
                "Discard captured instructions/destinations that never got an icon (the game drops an update " +
                "when the entity also died in the same reply) after this many minutes (0 = never expire).");

            UsePromptFiles = Config.Bind("Prompts", "UsePromptFiles", true,
                "Read the injected prompt text from the editable .txt files in " +
                "BepInEx/config/AIROG_DirectedUpdates/ (recommended — multi-line, re-read on save, no restart). " +
                "When false, the DocAddendum / InjectionTemplate values below are used instead.");
            DocAddendum = Config.Bind("Prompts", "DocAddendum", LEGACY_DOC_ADDENDUM,
                "Fallback for doc_addendum.txt (used when UsePromptFiles is false). Edit " +
                "BepInEx/config/AIROG_DirectedUpdates/doc_addendum.txt instead.");
            InjectionTemplate = Config.Bind("Prompts", "InjectionTemplate", LEGACY_INJECTION_TEMPLATE,
                "Fallback for injection_template.txt (used when UsePromptFiles is false). Placeholders: " +
                "{entity}, {instruction}; literal \\n becomes a newline. Edit " +
                "BepInEx/config/AIROG_DirectedUpdates/injection_template.txt instead.");

            ShowInstructionInTooltip = Config.Bind("Tooltip", "ShowInstructionInTooltip", true,
                "Append the GM's instruction to the 'Details updated' icon tooltip, so you can see what the AI " +
                "asked for without opening the log (most useful with 'confirm details updated' turned on). " +
                "Also shows a GM destination on the 'left' icon and your own instruction on the icon it raises.");
            TooltipTemplate = Config.Bind("Tooltip", "TooltipTemplate", "{tooltip} ({instruction})",
                "Format of that tooltip. Placeholders: {tooltip} (the vanilla text, trailing '.' trimmed), " +
                "{tooltip_raw} (untrimmed), {instruction}, {entity}. TextMeshPro rich text (e.g. <i>...</i>) works.");
            TooltipMaxChars = Config.Bind("Tooltip", "TooltipMaxChars", 160,
                "Truncate the instruction in the tooltip past this many characters (0 = no limit).");

            PlayerInstructionEnabled = Config.Bind("PlayerInstruction", "Enabled", true,
                "Add 'Instruct update...' to the right-click menu of entities (characters, objects, items, " +
                "locations, yourself). It asks for an instruction and runs the game's 'details updated' step for " +
                "that entity with the most recent story turn as context, without spending a story turn. " +
                "Host/single-player only.");
            PlayerInstructionQuickModifier = Config.Bind("PlayerInstruction", "QuickModifier",
                PlayerInstruction.QuickModifierKey.None,
                "Optional shortcut: hold this key while right-clicking an entity to open the instruction box " +
                "directly instead of the menu. None = menu entry only.");

            DestinationsEnabled = Config.Bind("Destinations", "Enabled", true,
                "Let the GM attach a \"destination\" to npcs_leave entries and pass it to the step that picks " +
                "where the departed character went.");
            ShareSameDestination = Config.Bind("Destinations", "ShareSameDestination", true,
                "Characters that leave in the same turn with the same destination text are put in the location " +
                "picked for the first of them, instead of each getting its own separate pick (the game runs " +
                "those picks at the same time, so a destination that does not exist yet would otherwise be " +
                "created once per character).");

            SafeRun("seed prompt files", () =>
            {
                PromptFiles.EnsureSeeded(PromptFiles.DOC_ADDENDUM_FILE, SeedFor(DocAddendum, DEFAULT_DOC_ADDENDUM));
                PromptFiles.EnsureSeeded(PromptFiles.INJECTION_TEMPLATE_FILE, SeedFor(InjectionTemplate, DEFAULT_INJECTION_TEMPLATE));
                PromptFiles.EnsureSeeded(PromptFiles.PLAYER_INJECTION_TEMPLATE_FILE, DEFAULT_PLAYER_INJECTION_TEMPLATE);
                PromptFiles.EnsureSeeded(PromptFiles.DESTINATION_DOC_ADDENDUM_FILE, DEFAULT_DESTINATION_DOC_ADDENDUM);
                PromptFiles.EnsureSeeded(PromptFiles.DESTINATION_INJECTION_TEMPLATE_FILE, DEFAULT_DESTINATION_INJECTION_TEMPLATE);
            });

            SafePatch(typeof(UnifiedPromptBuilder), "BuildApiDocsFromManifest",
                prefix: new HarmonyMethod(typeof(DirectedUpdatesPlugin), nameof(BuildApiDocs_Prefix)));
            SafePatch(typeof(UnifiedResponseParser), "ProcessApiAction",
                postfix: new HarmonyMethod(typeof(DirectedUpdatesPlugin), nameof(ProcessApiAction_Postfix)));
            SafePatch(typeof(AIAsker), "GetEntityStateChanges",
                prefix: new HarmonyMethod(typeof(DirectedUpdatesPlugin), nameof(GetEntityStateChanges_Prefix)));
            SafePatch(typeof(GameLogViewObj), "AddAiDecisionIcon",
                prefix: new HarmonyMethod(typeof(DirectedUpdatesPlugin), nameof(AddAiDecisionIcon_Prefix)),
                argTypes: new[]
                {
                    typeof(AiDecisionIcon.DecisionType),
                    typeof(string),
                    typeof(AiDecisionIcon.SerializableCbParams)
                });
            if (!SafePatch(typeof(AiDecisionIcon.SerializableCbParams), "ResolveStoryForDesc",
                postfix: new HarmonyMethod(typeof(DirectedUpdatesPlugin), nameof(ResolveStoryForDesc_Postfix))))
            {
                Logger.LogError("ResolveStoryForDesc could not be patched: instructions and destinations will be " +
                    "captured and shown on tooltips but will NOT reach the follow-up prompts.");
            }

            SafePatch(typeof(AIAsker), "FindOrMakeNewLocV2",
                prefix: new HarmonyMethod(typeof(Destinations), nameof(Destinations.FindOrMakeNewLocV2_Prefix)),
                postfix: new HarmonyMethod(typeof(Destinations), nameof(Destinations.FindOrMakeNewLocV2_Postfix)));
            SafePatch(typeof(AIAsker), "GetReplacedPromptForNewLocV2",
                prefix: new HarmonyMethod(typeof(Destinations), nameof(Destinations.GetReplacedPromptForNewLocV2_Prefix)));

            SafePatch(typeof(MmHelper), "GetCommonEditMenuRows",
                postfix: new HarmonyMethod(typeof(PlayerInstruction), nameof(PlayerInstruction.GetCommonEditMenuRows_Postfix)));
            SafePatch(typeof(GameplayManager), "ShowEditDetailsOrRedoImageMenu",
                prefix: new HarmonyMethod(typeof(PlayerInstruction), nameof(PlayerInstruction.ShowEditDetailsOrRedoImageMenu_Prefix)));

            Logger.LogInfo($"{PLUGIN_NAME} {PLUGIN_VERSION} loaded.");
        }

        // BaseUnityPlugin.Logger is protected, so sibling types in this assembly log through here.
        internal static void LogInfo(string msg) => Instance?.Logger.LogInfo(msg);

        internal static void LogWarn(string msg) => Instance?.Logger.LogWarning(msg);

        internal static bool IsOn(ConfigEntry<bool> feature = null) =>
            Enabled != null && Enabled.Value && (feature == null || feature.Value);

        /// <summary>
        /// A player who customised the v1.0.0 config keeps their text when the .txt file is first
        /// created; everyone else gets the improved default.
        /// </summary>
        private static string SeedFor(ConfigEntry<string> entry, string improvedDefault)
        {
            string current = entry?.Value;
            string original = entry?.DefaultValue as string;
            return string.IsNullOrEmpty(current) || current == original ? improvedDefault : current;
        }

        private static string GetDocAddendum() =>
            PromptFiles.Read(PromptFiles.DOC_ADDENDUM_FILE, DocAddendum.Value)?.Trim();

        private static string GetInjectionTemplate() =>
            (PromptFiles.Read(PromptFiles.INJECTION_TEMPLATE_FILE, InjectionTemplate.Value) ?? "").Replace("\\n", "\n").Trim();

        private static string GetPlayerInjectionTemplate() =>
            (PromptFiles.Read(PromptFiles.PLAYER_INJECTION_TEMPLATE_FILE, DEFAULT_PLAYER_INJECTION_TEMPLATE) ?? "").Trim();

        private static string GetDestinationDocAddendum() =>
            PromptFiles.Read(PromptFiles.DESTINATION_DOC_ADDENDUM_FILE, DEFAULT_DESTINATION_DOC_ADDENDUM)?.Trim();

        internal static string GetDestinationInjectionTemplate() =>
            (PromptFiles.Read(PromptFiles.DESTINATION_INJECTION_TEMPLATE_FILE, DEFAULT_DESTINATION_INJECTION_TEMPLATE) ?? "").Trim();

        // The game refuses to send any prompt that still contains "${" or "{$"
        // (UnreplacedVariableException), so free text must not smuggle one in.
        internal static string ForPrompt(string text) => (text ?? "").Replace("${", "$ {").Replace("{$", "{ $");

        private class AmendedDoc
        {
            public string vanilla;
            public string amended;
        }

        private static readonly Dictionary<string, AmendedDoc> amendedDocs = new Dictionary<string, AmendedDoc>();

        /// <summary>
        /// Amends the GM-facing action docs (SS.I.unifiedApiActionsDict, passed in as dict) to
        /// advertise the optional "instruction" / "destination" fields. Runs every build because
        /// prompt reloads replace the dict contents.
        /// </summary>
        public static void BuildApiDocs_Prefix(Dictionary<string, string> dict)
        {
            if (Enabled == null || dict == null) return;
            try
            {
                ApplyDocAddendum(dict, API_KEY, IsOn() ? GetDocAddendum() : null);
                ApplyDocAddendum(dict, LEAVE_API_KEY, IsOn(DestinationsEnabled) ? GetDestinationDocAddendum() : null);
            }
            catch (Exception ex)
            {
                Instance?.Logger.LogWarning($"BuildApiDocs prefix failed: {ex}");
            }
        }

        /// <summary>
        /// The dict is the game's own, so whatever is written here stays until the prompts are
        /// reloaded. Always rebuild from the remembered vanilla text: appending to the current
        /// value left the previous addendum in place after doc_addendum.txt was edited (the GM
        /// then saw both versions) and after the mod was switched off.
        /// </summary>
        private static void ApplyDocAddendum(Dictionary<string, string> dict, string key, string addendum)
        {
            if (!dict.TryGetValue(key, out string doc) || string.IsNullOrEmpty(doc)) return;
            string vanilla = amendedDocs.TryGetValue(key, out AmendedDoc last) && last.amended == doc ? last.vanilla : doc;
            string wanted = string.IsNullOrEmpty(addendum) || vanilla.Contains(addendum)
                ? vanilla
                : vanilla.TrimEnd() + " " + addendum;
            if (wanted != doc) dict[key] = wanted;
            amendedDocs[key] = new AmendedDoc { vanilla = vanilla, amended = wanted };
        }

        /// <summary>
        /// After the vanilla parser resolves update_entities / npcs_leave entries (tagged with
        /// their entry index), captures each entry's extra field keyed by the resolved entity's
        /// uuid.
        /// </summary>
        public static void ProcessApiAction_Postfix(string action, JArray entries, GameEventResult result)
        {
            if (!IsOn() || entries == null || result == null) return;
            try
            {
                if (action == API_ACTION)
                    Capture(entries, result.entitiesWStateChange, action, "instruction", InstructionStore.KIND_INSTRUCTION);
                else if (action == LEAVE_ACTION && DestinationsEnabled.Value)
                    Capture(entries, result.entitiesDeparted, action, "destination", InstructionStore.KIND_DESTINATION);
            }
            catch (Exception ex)
            {
                Instance?.Logger.LogWarning($"ProcessApiAction postfix failed: {ex}");
            }
        }

        private static string NameOf(JToken entry) =>
            entry == null ? null : entry.Type == JTokenType.String ? entry.ToString() : (entry as JObject)?["name"]?.ToString();

        private static string FieldOf(JToken entry, string field) => (entry as JObject)?[field]?.ToString()?.Trim();

        /// <summary>
        /// The field of the entry at <paramref name="index"/>. With <paramref name="sameName"/>
        /// it also folds in later entries that name the same entity: the game keeps one update
        /// per entity (its result set compares by entity), so such an entry gets no follow-up of
        /// its own and its instruction would otherwise be dropped.
        /// </summary>
        internal static string FieldWithDuplicates(JArray entries, int index, HashSet<int> resolvedIndices, string field,
            Func<string, string, bool> sameName)
        {
            string text = FieldOf(entries[index], field);
            string entryName = NameOf(entries[index]);
            if (sameName == null || string.IsNullOrEmpty(entryName)) return text;
            for (int j = index + 1; j < entries.Count; j++)
            {
                if (resolvedIndices.Contains(j)) continue;
                string extra = FieldOf(entries[j], field);
                if (string.IsNullOrEmpty(extra) || !sameName(NameOf(entries[j]), entryName)) continue;
                text = string.IsNullOrEmpty(text) ? extra : text + "; " + extra;
            }
            return text;
        }

        private static void Capture(JArray entries, HashSet<ApiTagged<GameEntity>> resolved, string action, string field, string kind)
        {
            HashSet<int> resolvedIndices = new HashSet<int>();
            foreach (ApiTagged<GameEntity> tagged in resolved)
            {
                if (tagged != null && tagged.apiAction == action) resolvedIndices.Add(tagged.entryIndex);
            }

            foreach (ApiTagged<GameEntity> tagged in resolved)
            {
                if (tagged?.apiVal == null || tagged.apiAction != action) continue;
                if (tagged.entryIndex < 0 || tagged.entryIndex >= entries.Count) continue;
                string text = FieldWithDuplicates(entries, tagged.entryIndex, resolvedIndices, field,
                    kind == InstructionStore.KIND_INSTRUCTION ? Utils.StringsAreVerySimilar : (Func<string, string, bool>)null);

                if (string.IsNullOrEmpty(text))
                {
                    InstructionStore.Remove(kind, tagged.apiVal.uuid);
                    continue;
                }
                InstructionStore.Put(kind, tagged.apiVal.uuid, tagged.apiVal.GetPrettyName(), text);
                Instance?.Logger.LogInfo($"Captured {field} for '{tagged.apiVal.GetPrettyName()}': {text}");
            }
        }

        /// <summary>
        /// Stores the text on the icon's own callback params, in strList (which neither the
        /// state-change nor the char-left callback reads). The params are saved, undone, reloaded
        /// and synced to co-op clients together with the icon, so the text stays with exactly the
        /// icon it belongs to for as long as that icon can be clicked. Keying a side table by
        /// entity uuid instead let an instruction outlive a dismissed icon and attach to the next
        /// update of the same entity, and lost it when the AI call failed or the turn was undone.
        /// </summary>
        internal static void Stash(AiDecisionIcon.SerializableCbParams cbParams, string kind, string text)
        {
            cbParams.strList = new List<string> { STASH_MARKER, kind, text };
        }

        internal static bool TryReadStash(AiDecisionIcon.SerializableCbParams cbParams, out string kind, out string text)
        {
            kind = null;
            text = null;
            List<string> list = cbParams?.strList;
            if (list == null || list.Count != 3 || list[0] != STASH_MARKER || string.IsNullOrEmpty(list[2])) return false;
            kind = list[1];
            text = list[2];
            return true;
        }

        private static bool HasApiOrigin(AiDecisionIcon.SerializableCbParams cbParams, string action) =>
            cbParams.unifiedApiInfos != null && cbParams.unifiedApiInfos.Any(info => info.Item1 == action);

        /// <summary>
        /// Runs when the game raises an icon. For the icon of an update_entities / npcs_leave
        /// entry this takes the captured text out of the store and puts it on the icon, then
        /// (also for an icon PlayerInstruction built with the text already on it) appends it to
        /// the tooltip: "[Entity]: Details updated (add a cowering in fear status effect)".
        /// Patched at icon creation rather than on AiDecisionIcon.Init, so reloading a save (which
        /// re-inits icons from the already-amended stored tooltip) can't append a second copy.
        /// </summary>
        public static void AddAiDecisionIcon_Prefix(AiDecisionIcon.DecisionType decisionType, ref string tooltipTxt,
            AiDecisionIcon.SerializableCbParams cbParams)
        {
            if (!IsOn() || cbParams == null) return;
            bool isLeave = decisionType == AiDecisionIcon.DecisionType.CHAR_LEFT;
            if (!isLeave && decisionType != AiDecisionIcon.DecisionType.LEARNED_NAME) return;
            try
            {
                if (!TryReadStash(cbParams, out string kind, out string instruction))
                {
                    // Only the icon the game raised for that API entry may claim it: the same
                    // icon type is also used for name changes and by the specialized pipeline.
                    if (cbParams.strList != null || !HasApiOrigin(cbParams, isLeave ? LEAVE_ACTION : API_ACTION)) return;
                    if (isLeave && !DestinationsEnabled.Value) return;
                    string storeKind = isLeave ? InstructionStore.KIND_DESTINATION : InstructionStore.KIND_INSTRUCTION;
                    if (!InstructionStore.TryTake(storeKind, cbParams.entityUuid, out instruction)) return;
                    kind = isLeave ? STASH_DESTINATION : STASH_GM;
                    Stash(cbParams, kind, instruction);
                }
                if (ShowInstructionInTooltip == null || !ShowInstructionInTooltip.Value) return;

                instruction = instruction.Replace("\r", " ").Replace("\n", " ").Trim();
                int max = TooltipMaxChars.Value;
                if (max > 0 && instruction.Length > max)
                {
                    instruction = instruction.Substring(0, max).TrimEnd() + "...";
                }
                if (kind == STASH_DESTINATION) instruction = "destination: " + instruction;

                string raw = tooltipTxt ?? "";
                string trimmed = raw.TrimEnd().TrimEnd('.');
                string template = TooltipTemplate.Value;
                if (string.IsNullOrEmpty(template)) return;

                if (template.Contains("{entity}")) template = template.Replace("{entity}", EntityName(cbParams.entityUuid));
                tooltipTxt = template
                    .Replace("{tooltip_raw}", raw)
                    .Replace("{tooltip}", trimmed)
                    .Replace("{instruction}", instruction);
            }
            catch (Exception ex)
            {
                Instance?.Logger.LogWarning($"AddAiDecisionIcon prefix failed: {ex}");
            }
        }

        private static string EntityName(string uuid)
        {
            if (string.IsNullOrEmpty(uuid) || SS.I == null) return "";
            return SS.I.uuidToGameEntityMap.TryGetValue(uuid, out GameEntity ge) && ge != null ? ge.GetPrettyName() : "";
        }

        private static AiDecisionIcon.SerializableCbParams lastResolvedCb;
        private static string lastResolvedStory;

        /// <summary>
        /// The follow-up AI calls are static and are handed the story text but not the icon. In
        /// both icon callbacks that text is the value of cbParams.ResolveStoryForDesc(manager),
        /// evaluated as an argument of the call itself, so remembering which params produced it
        /// tells the prefix running immediately afterwards which icon is calling.
        /// </summary>
        public static void ResolveStoryForDesc_Postfix(AiDecisionIcon.SerializableCbParams __instance, string __result)
        {
            bool ours = TryReadStash(__instance, out string _, out string _);
            lastResolvedCb = ours ? __instance : null;
            lastResolvedStory = ours ? __result : null;
        }

        /// <summary>The params whose ResolveStoryForDesc just produced this exact story text, if they carry our text.</summary>
        internal static AiDecisionIcon.SerializableCbParams TakeResolvedCb(string storyTxt)
        {
            AiDecisionIcon.SerializableCbParams cb = lastResolvedCb;
            string story = lastResolvedStory;
            lastResolvedCb = null;
            lastResolvedStory = null;
            return cb != null && story == storyTxt ? cb : null;
        }

        /// <summary>
        /// True when the icon's entity is the one the follow-up runs for. Compared through the
        /// owning InGameEntity because the state-change callback swaps an inactive component for
        /// the active one (the entity changed type since the icon was raised), which has another uuid.
        /// </summary>
        internal static bool IsSameEntity(string uuid, GameEntity ge)
        {
            if (string.IsNullOrEmpty(uuid) || ge == null) return false;
            if (uuid == ge.uuid) return true;
            return SS.I.uuidToGameEntityMap.TryGetValue(uuid, out GameEntity other) && other != null
                && other.AsNonCpnGe() == ge.AsNonCpnGe();
        }

        /// <summary>
        /// Injects the calling icon's instruction (if it carries one) into the story text used to
        /// build the update_entity_state prompt for this entity. Nothing is consumed: the icon
        /// keeps its instruction, so clicking it again after a failed AI call still applies it.
        /// </summary>
        public static void GetEntityStateChanges_Prefix(GameEntity ge, ref string storyTxt)
        {
            AiDecisionIcon.SerializableCbParams cb = TakeResolvedCb(storyTxt);
            if (!IsOn() || ge == null || cb == null) return;
            try
            {
                if (!TryReadStash(cb, out string kind, out string instruction)) return;
                if (kind != STASH_GM && kind != STASH_PLAYER) return;
                if (!IsSameEntity(cb.entityUuid, ge)) return;
                string template = kind == STASH_PLAYER ? GetPlayerInjectionTemplate() : GetInjectionTemplate();
                if (string.IsNullOrEmpty(template)) return;
                string block = template.Replace("{entity}", ge.GetPrettyName()).Replace("{instruction}", ForPrompt(instruction));
                storyTxt = (storyTxt ?? "").TrimEnd() + "\n\n" + block;
                Instance?.Logger.LogInfo($"Injected {kind} instruction into state update for '{ge.GetPrettyName()}': {instruction}");
            }
            catch (Exception ex)
            {
                Instance?.Logger.LogWarning($"GetEntityStateChanges prefix failed: {ex}");
            }
        }
    }
}
