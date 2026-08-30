using System;
using System.Collections.Generic;
using AIROG_Core;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace AIROG_DirectedUpdates
{
    /// <summary>
    /// Lets the first-pass GM attach a per-entity "instruction" to update_entities entries,
    /// and forwards that instruction into the follow-up update_entity_state3 prompt. Vanilla
    /// re-derives WHAT changed from raw story text with no guidance, which is why status
    /// updates often do nothing or the wrong thing. Four hooks:
    ///   1. BuildApiDocsFromManifest prefix — documents the optional "instruction" field to the GM.
    ///   2. ProcessApiAction postfix — captures instructions off resolved update_entities entries
    ///      (vanilla parser reads entry["name"] and ignores unknown fields, so plain strings and
    ///      instruction-less objects keep working on models that don't emit the field).
    ///   3. GetEntityStateChanges prefix — appends the captured instruction to the story text the
    ///      updater model sees for that entity.
    ///   4. AddAiDecisionIcon prefix — shows the instruction on the "Details updated" icon tooltip,
    ///      so a bad instruction is visible in-game instead of only in the log.
    /// Both injected prompt texts live in editable .txt files under
    /// BepInEx/config/AIROG_DirectedUpdates/ (see PromptFiles).
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class DirectedUpdatesPlugin : BaseModPlugin
    {
        public const string PLUGIN_GUID = "com.airog.directedupdates";
        public const string PLUGIN_NAME = "Directed Updates";
        public const string PLUGIN_VERSION = "1.1.0";

        private const string API_KEY = "api_update_entities";
        private const string API_ACTION = "update_entities";

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
                "Persist not-yet-consumed instructions to the save directory so they survive a save/quit " +
                "while a 'confirm details updated' icon is still pending.");
            ExpiryMinutes = Config.Bind("General", "InstructionExpiryMinutes", 240,
                "Discard captured instructions older than this many minutes (0 = never expire).");

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
                "asked for without opening the log (most useful with 'confirm details updated' turned on).");
            TooltipTemplate = Config.Bind("Tooltip", "TooltipTemplate", "{tooltip} ({instruction})",
                "Format of that tooltip. Placeholders: {tooltip} (the vanilla text, trailing '.' trimmed), " +
                "{tooltip_raw} (untrimmed), {instruction}, {entity}. TextMeshPro rich text (e.g. <i>...</i>) works.");
            TooltipMaxChars = Config.Bind("Tooltip", "TooltipMaxChars", 160,
                "Truncate the instruction in the tooltip past this many characters (0 = no limit).");

            SafeRun("seed prompt files", () =>
            {
                PromptFiles.EnsureSeeded(PromptFiles.DOC_ADDENDUM_FILE, SeedFor(DocAddendum, DEFAULT_DOC_ADDENDUM));
                PromptFiles.EnsureSeeded(PromptFiles.INJECTION_TEMPLATE_FILE, SeedFor(InjectionTemplate, DEFAULT_INJECTION_TEMPLATE));
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

            Logger.LogInfo($"{PLUGIN_NAME} {PLUGIN_VERSION} loaded.");
        }

        // BaseUnityPlugin.Logger is protected, so sibling types in this assembly log through here.
        internal static void LogInfo(string msg) => Instance?.Logger.LogInfo(msg);

        internal static void LogWarn(string msg) => Instance?.Logger.LogWarning(msg);

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

        /// <summary>
        /// Amends the GM-facing update_entities doc (SS.I.unifiedApiActionsDict, passed in as
        /// dict) to advertise the optional "instruction" field. Runs every build because prompt
        /// reloads replace the dict contents; the Contains check keeps it idempotent.
        /// </summary>
        public static void BuildApiDocs_Prefix(Dictionary<string, string> dict)
        {
            if (Enabled == null || !Enabled.Value || dict == null) return;
            string addendum = GetDocAddendum();
            if (string.IsNullOrEmpty(addendum)) return;
            if (!dict.TryGetValue(API_KEY, out string doc) || string.IsNullOrEmpty(doc)) return;
            if (doc.Contains(addendum)) return;
            dict[API_KEY] = doc.TrimEnd() + " " + addendum;
        }

        /// <summary>
        /// After the vanilla parser resolves update_entities entries into entitiesWStateChange
        /// (tagged with their entry index), captures each entry's "instruction" field keyed by
        /// the resolved entity's uuid.
        /// </summary>
        public static void ProcessApiAction_Postfix(string action, JArray entries, GameEventResult result)
        {
            if (Enabled == null || !Enabled.Value || action != API_ACTION || entries == null || result == null) return;
            try
            {
                foreach (ApiTagged<GameEntity> tagged in result.entitiesWStateChange)
                {
                    if (tagged?.apiVal == null || tagged.apiAction != API_ACTION) continue;
                    if (tagged.entryIndex < 0 || tagged.entryIndex >= entries.Count) continue;
                    if (!(entries[tagged.entryIndex] is JObject obj)) continue;
                    string instruction = obj["instruction"]?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(instruction)) continue;
                    InstructionStore.Put(tagged.apiVal.uuid, tagged.apiVal.GetPrettyName(), instruction);
                    Instance?.Logger.LogInfo($"Captured instruction for '{tagged.apiVal.GetPrettyName()}': {instruction}");
                }
            }
            catch (Exception ex)
            {
                Instance?.Logger.LogWarning($"ProcessApiAction postfix failed: {ex}");
            }
        }

        /// <summary>
        /// Injects the pending instruction (if any) into the story text used to build the
        /// update_entity_state3 prompt for this entity. Instructions are consumed on use.
        /// </summary>
        public static void GetEntityStateChanges_Prefix(GameEntity ge, ref string storyTxt)
        {
            if (Enabled == null || !Enabled.Value || ge == null) return;
            try
            {
                if (!InstructionStore.TryTake(ge.uuid, out string instruction)) return;
                string template = GetInjectionTemplate();
                if (string.IsNullOrEmpty(template)) return;
                string block = template.Replace("{entity}", ge.GetPrettyName()).Replace("{instruction}", instruction);
                storyTxt = (storyTxt ?? "").TrimEnd() + "\n\n" + block;
                Instance?.Logger.LogInfo($"Injected instruction into state update for '{ge.GetPrettyName()}': {instruction}");
            }
            catch (Exception ex)
            {
                Instance?.Logger.LogWarning($"GetEntityStateChanges prefix failed: {ex}");
            }
        }

        /// <summary>
        /// Appends the pending instruction to the tooltip of the icon the game raises for a state
        /// change ("[Entity]: Details updated."), turning it into
        /// "[Entity]: Details updated (add a cowering in fear status effect)". Patched at icon
        /// creation rather than on AiDecisionIcon.Init, so reloading a save (which re-inits icons
        /// from the already-amended stored tooltip) can't append a second copy. The instruction is
        /// peeked, not consumed — the state-update call still needs it.
        /// </summary>
        public static void AddAiDecisionIcon_Prefix(AiDecisionIcon.DecisionType decisionType, ref string tooltipTxt,
            AiDecisionIcon.SerializableCbParams cbParams)
        {
            if (Enabled == null || !Enabled.Value || ShowInstructionInTooltip == null || !ShowInstructionInTooltip.Value) return;
            if (decisionType != AiDecisionIcon.DecisionType.LEARNED_NAME || cbParams == null) return;
            try
            {
                if (!InstructionStore.TryPeek(cbParams.entityUuid, out string instruction, out string storedName)) return;

                instruction = instruction.Replace("\r", " ").Replace("\n", " ").Trim();
                int max = TooltipMaxChars.Value;
                if (max > 0 && instruction.Length > max)
                {
                    instruction = instruction.Substring(0, max).TrimEnd() + "...";
                }

                string raw = tooltipTxt ?? "";
                string trimmed = raw.TrimEnd().TrimEnd('.');
                string template = TooltipTemplate.Value;
                if (string.IsNullOrEmpty(template)) return;

                tooltipTxt = template
                    .Replace("{tooltip_raw}", raw)
                    .Replace("{tooltip}", trimmed)
                    .Replace("{instruction}", instruction)
                    .Replace("{entity}", storedName ?? "");
            }
            catch (Exception ex)
            {
                Instance?.Logger.LogWarning($"AddAiDecisionIcon prefix failed: {ex}");
            }
        }
    }
}
