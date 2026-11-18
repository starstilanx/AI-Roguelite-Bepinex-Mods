using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace AIROG_DirectedUpdates
{
    /// <summary>
    /// GM-directed destinations for departing characters. The "destination" of an npcs_leave
    /// entry rides on the CHAR_LEFT icon (see DirectedUpdatesPlugin.Stash). That icon's callback
    /// calls AIAsker.FindOrMakeNewLocV2, which asks the model twice where the character went
    /// (coarse world/zone, then the location path) through prompts built by
    /// GetReplacedPromptForNewLocV2. The guidance is added to those prompts only, right after
    /// the game's own "infer where X may have moved to" paragraph; putting it in the story text
    /// would also feed it to the description generator of any location created on the way.
    /// </summary>
    internal static class Destinations
    {
        private const int SHARE_WAIT_MS = 120000;

        // Weak, so a finished pick (and the world it points into) is not kept alive by this list.
        private class RunningPick
        {
            public string key;
            public WeakReference<Task<AIAsker.LocInfo>> task;
        }

        // The LocInfoArgs instance is created per call and passed through both prompt steps.
        private static readonly ConditionalWeakTable<AIAsker.LocInfoArgs, string> guidanceByArgs =
            new ConditionalWeakTable<AIAsker.LocInfoArgs, string>();
        private static readonly List<RunningPick> runningPicks = new List<RunningPick>();
        private static bool reentering;

        /// <summary>
        /// Binds the calling icon's destination to this location pick. When a pick with the same
        /// destination text for the same turn is still running, the original is skipped and the
        /// caller is handed that pick's location instead (see FollowRunningPick); __result is
        /// always a real task in that case.
        /// </summary>
        public static bool FindOrMakeNewLocV2_Prefix(GameplayManager manager, string storyTxt, Place origLoc,
            AIAsker.LocInfoArgs locInfoArgs, string tryStr, ref Task<AIAsker.LocInfo> __result, out string __state)
        {
            __state = null;
            if (reentering) return true;
            AiDecisionIcon.SerializableCbParams cb = DirectedUpdatesPlugin.TakeResolvedCb(storyTxt);
            try
            {
                if (!DirectedUpdatesPlugin.IsOn(DirectedUpdatesPlugin.DestinationsEnabled) || cb == null) return true;
                if (locInfoArgs == null || locInfoArgs.type != AIAsker.LocInfoArgs.LocInfoType.ENTITY) return true;
                GameEntity who = locInfoArgs.entityWhoLeft;
                if (who == null || !DirectedUpdatesPlugin.IsSameEntity(cb.entityUuid, who)) return true;
                if (!DirectedUpdatesPlugin.TryReadStash(cb, out string kind, out string destination)) return true;
                if (kind != DirectedUpdatesPlugin.STASH_DESTINATION) return true;
                string template = DirectedUpdatesPlugin.GetDestinationInjectionTemplate();
                if (string.IsNullOrEmpty(template)) return true;

                string name = who.GetPrettyName();
                string block = template.Replace("{entity}", name)
                    .Replace("{destination}", DirectedUpdatesPlugin.ForPrompt(destination));
                guidanceByArgs.Remove(locInfoArgs);
                guidanceByArgs.Add(locInfoArgs, block);
                DirectedUpdatesPlugin.LogInfo($"Destination guidance for '{name}': {destination}");

                if (!DirectedUpdatesPlugin.ShareSameDestination.Value) return true;
                string key = ShareKey(destination, storyTxt, origLoc);
                Task<AIAsker.LocInfo> running = FindRunning(key);
                if (running == null)
                {
                    __state = key;
                    return true;
                }
                __result = FollowRunningPick(running,
                    () => Reenter(manager, storyTxt, origLoc, locInfoArgs, tryStr), key, name);
                return false;
            }
            catch (Exception ex)
            {
                DirectedUpdatesPlugin.LogWarn($"FindOrMakeNewLocV2 prefix failed: {ex}");
                __state = null;
                return true;
            }
        }

        /// <summary>Remembers a pick that just started so others of the same turn can follow it.</summary>
        public static void FindOrMakeNewLocV2_Postfix(Task<AIAsker.LocInfo> __result, string __state)
        {
            if (__state == null || __result == null || __result.IsCompleted) return;
            runningPicks.Add(new RunningPick { key = __state, task = new WeakReference<Task<AIAsker.LocInfo>>(__result) });
        }

        public static void GetReplacedPromptForNewLocV2_Prefix(ref string prompt, AIAsker.LocInfoArgs locInfoArgs)
        {
            try
            {
                if (prompt == null || locInfoArgs == null) return;
                if (!guidanceByArgs.TryGetValue(locInfoArgs, out string block)) return;
                prompt = InsertGuidance(prompt, block);
            }
            catch (Exception ex)
            {
                DirectedUpdatesPlugin.LogWarn($"GetReplacedPromptForNewLocV2 prefix failed: {ex}");
            }
        }

        internal static string InsertGuidance(string prompt, string block)
        {
            const string anchor = "${location_inst}";
            return prompt.Contains(anchor)
                ? prompt.Replace(anchor, anchor + "\n\n" + block)
                : prompt.TrimEnd() + "\n\n" + block;
        }

        // Scoped by story text and origin: both are identical for every character leaving in
        // one GM reply and differ for any later turn.
        internal static string ShareKey(string destination, string storyTxt, Place origLoc)
        {
            string norm = string.Join(" ", (destination ?? "").ToLowerInvariant()
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            return norm + "\n" + origLoc?.uuid + "\n" + storyTxt;
        }

        private static Task<AIAsker.LocInfo> FindRunning(string key)
        {
            Task<AIAsker.LocInfo> found = null;
            for (int i = runningPicks.Count - 1; i >= 0; i--)
            {
                if (!runningPicks[i].task.TryGetTarget(out Task<AIAsker.LocInfo> task) || task.IsCompleted)
                    runningPicks.RemoveAt(i);
                else if (runningPicks[i].key == key)
                    found = task;
            }
            return found;
        }

        private static Task<AIAsker.LocInfo> Reenter(GameplayManager manager, string storyTxt, Place origLoc,
            AIAsker.LocInfoArgs locInfoArgs, string tryStr)
        {
            reentering = true;
            try
            {
                return AIAsker.FindOrMakeNewLocV2(manager, storyTxt, origLoc, locInfoArgs, tryStr);
            }
            finally
            {
                reentering = false;
            }
        }

        /// <summary>
        /// The game starts the location picks of all characters leaving in one turn without
        /// awaiting them, so two characters sent to the same not-yet-existing place each create
        /// their own copy of it. Wait for the pick that is already running instead and reuse its
        /// location; if it failed, took too long, or its location is gone (undo/reload), pick
        /// normally.
        /// </summary>
        private static async Task<AIAsker.LocInfo> FollowRunningPick(Task<AIAsker.LocInfo> running,
            Func<Task<AIAsker.LocInfo>> ownPick, string key, string name)
        {
            while (running != null)
            {
                AIAsker.LocInfo info = null;
                bool finished = false;
                try
                {
                    using (CancellationTokenSource timeout = new CancellationTokenSource())
                    {
                        finished = await Task.WhenAny(running, Task.Delay(SHARE_WAIT_MS, timeout.Token)) == running;
                        timeout.Cancel();
                        if (finished) info = await running;
                    }
                }
                catch (Exception)
                {
                    // the running pick's own caller reports its failure
                }
                if (IsLivePlace(info))
                {
                    DirectedUpdatesPlugin.LogInfo($"'{name}' goes where the pick already running for the same destination went.");
                    return info;
                }
                // It failed: another follower may have started its own pick in the meantime.
                running = finished ? FindRunning(key) : null;
            }
            Task<AIAsker.LocInfo> mine = ownPick();
            if (!mine.IsCompleted) runningPicks.Add(new RunningPick { key = key, task = new WeakReference<Task<AIAsker.LocInfo>>(mine) });
            return await mine;
        }

        private static bool IsLivePlace(AIAsker.LocInfo info)
        {
            if (info == null || info.IsForVehicle() || info.pl == null) return false;
            return SS.I.uuidToGameEntityMap.TryGetValue(info.pl.uuid, out GameEntity live) && ReferenceEquals(live, info.pl);
        }
    }
}
