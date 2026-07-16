using System;
using System.Collections.Generic;
using System.IO;
using AIROG_Multiplayer.Inventory;
using AIROG_Multiplayer.Network;

namespace AIROG_Multiplayer
{
    // Broadcast/state-snapshot helpers shared by the host-side event handlers.
    public partial class MultiplayerPlugin
    {
        public static RemoteCharacterInfo GetLocalCharacterInfo(GameplayManager manager)
        {
            var pc = manager?.playerCharacter?.pcGameEntity;
            return new RemoteCharacterInfo
            {
                PlayerName = "Host",
                CharacterName = pc?.name ?? "Host",
                CharacterClass = pc?.playerClass?.ToSingleLineStr() ?? "",
                CharacterBackground = pc?.GetPotentiallyNullDescription() ?? "",
                Health = pc?.health ?? 100,
                MaxHealth = pc?.maxHealth ?? 100,
                Level = 0,
                CurrentLocation = manager?.currentPlace?.name ?? ""
            };
        }

        /// <summary>
        /// Serializes the full MPInventoryDatabase and broadcasts it to all connected clients.
        /// Safe to call from the main thread (e.g. from WriteSaveFilePatch.Postfix).
        /// </summary>
        public static void BroadcastInventory()
        {
            if (!IsHost || Server == null) return;
            try
            {
                string json = MPInventoryManager.SerializeToJson();
                Server.BroadcastInventory(json);
                Instance?.Log.LogInfo($"[Host] BroadcastInventory: {json.Length} chars.");
            }
            catch (Exception ex)
            {
                Instance?.Log.LogError($"[Host] BroadcastInventory error: {ex.Message}");
            }
        }

        /// <summary>
        /// Extracts quest info from the game's QuestLogV4 into lightweight MPQuestInfo[] for network sync.
        /// </summary>
        public static MPQuestInfo[] ExtractQuestState(GameplayManager manager)
        {
            var quests = new List<MPQuestInfo>();
            try
            {
                var questLog = manager?.playerCharacter?.pcGameEntity?.questLogV4;
                if (questLog == null) return quests.ToArray();

                // Main quest chain
                if (questLog.mainQuestChain?.questEles != null)
                {
                    foreach (var ele in questLog.mainQuestChain.questEles)
                    {
                        quests.Add(new MPQuestInfo
                        {
                            Id = ele.uuid ?? "",
                            Title = ele.questTitle ?? "Main Quest",
                            Objective = (ele as QuestEleV4)?.objectiveStr ?? "",
                            Status = "Active",
                            QuestType = "Main"
                        });
                    }
                }

                // Active side quests
                if (questLog.sideQuestChains != null)
                {
                    foreach (var chain in questLog.sideQuestChains)
                    {
                        if (chain?.questEles == null) continue;
                        foreach (var ele in chain.questEles)
                        {
                            quests.Add(new MPQuestInfo
                            {
                                Id = ele.uuid ?? "",
                                Title = ele.questTitle ?? "Side Quest",
                                Objective = (ele as QuestEleV4)?.objectiveStr ?? "",
                                Status = "Active",
                                QuestType = "Side"
                            });
                        }
                    }
                }

                // Completed quests
                if (questLog.completedQuests != null)
                {
                    foreach (var chain in questLog.completedQuests)
                    {
                        if (chain?.questEles == null) continue;
                        foreach (var ele in chain.questEles)
                        {
                            quests.Add(new MPQuestInfo
                            {
                                Id = ele.uuid ?? "",
                                Title = ele.questTitle ?? "Quest",
                                Objective = (ele as QuestEleV4)?.objectiveStr ?? "",
                                Status = "Completed",
                                QuestType = "Side"
                            });
                        }
                    }
                }

                // Failed quests
                if (questLog.failedQuests != null)
                {
                    foreach (var chain in questLog.failedQuests)
                    {
                        if (chain?.questEles == null) continue;
                        foreach (var ele in chain.questEles)
                        {
                            quests.Add(new MPQuestInfo
                            {
                                Id = ele.uuid ?? "",
                                Title = ele.questTitle ?? "Quest",
                                Objective = (ele as QuestEleV4)?.objectiveStr ?? "",
                                Status = "Failed",
                                QuestType = "Side"
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Instance?.Log.LogError($"[Host] ExtractQuestState error: {ex.Message}");
            }
            return quests.ToArray();
        }

        /// <summary>
        /// Broadcasts current quest state to all connected clients.
        /// </summary>
        public static void BroadcastQuestSync()
        {
            if (!IsHost || Server == null) return;
            try
            {
                var manager = FindObjectOfType<GameplayManager>();
                var quests = ExtractQuestState(manager);
                Server.BroadcastQuestSync(quests);
                Instance?.Log.LogInfo($"[Host] BroadcastQuestSync: {quests.Length} quest(s).");
            }
            catch (Exception ex)
            {
                Instance?.Log.LogError($"[Host] BroadcastQuestSync error: {ex.Message}");
            }
        }

        public static void BroadcastLocation(string locationName, string locationDescription = "")
        {
            if (!IsHost) return;
            Server?.BroadcastAll(Packet.Create(PacketType.LocationUpdate, new LocationUpdatePayload
            {
                LocationName = locationName,
                LocationDescription = locationDescription
            }));
            Instance?.Log.LogInfo($"[Host] Broadcast location update: {locationName}");
        }

        /// <summary>
        /// Broadcasts a full LocationUpdatePayload with extended map data.
        /// </summary>
        public static void BroadcastLocationPayload(LocationUpdatePayload payload)
        {
            if (!IsHost) return;
            Server?.BroadcastAll(Packet.Create(PacketType.LocationUpdate, payload));
            Instance?.Log.LogInfo($"[Host] Broadcast location: {payload.LocationName} (NPCs: {payload.NPCNames?.Length ?? 0}, Enemies: {payload.EnemyNames?.Length ?? 0})");
        }

        private static void SendSaveSnapshot(ConnectedClient client, GameplayManager manager)
        {
            try
            {
                if (manager == null) return;
                string hostSaveSubDir = SS.I.saveSubDirAsArg;
                string savePath = Path.Combine(SS.I.saveTopLvlDir, hostSaveSubDir, "my_save.txt");
                if (!File.Exists(savePath))
                {
                    Instance.Log.LogWarning("[Host] Save file not found, skipping snapshot.");
                    return;
                }

                string saveDir = Path.Combine(SS.I.saveTopLvlDir, hostSaveSubDir);
                string saveJson = File.ReadAllText(savePath);
                string placeName = manager.currentPlace?.name ?? "";
                string placeDesc = manager.currentPlace?.description ?? "";
                var polygonFiles = ReadPolygonFiles(saveDir);

                Server.SendSaveFileTo(client, saveJson, placeName, placeDesc, hostSaveSubDir, polygonFiles);
            }
            catch (Exception ex)
            {
                Instance.Log.LogError($"[Host] Failed to send save snapshot: {ex.Message}");
            }
        }

        private static StoryEntry[] GetRecentTurns(GameplayManager manager, int count)
        {
            try
            {
                var storyChain = manager?.playerCharacter?.pcGameEntity?.storyChain;
                if (storyChain == null) return new StoryEntry[0];

                var turns = storyChain.storyTurns;
                int skip = Math.Max(0, turns.Count - count);
                var entries = new List<StoryEntry>();
                for (int i = skip; i < turns.Count; i++)
                {
                    entries.Add(new StoryEntry
                    {
                        Text = turns[i].getCombinedStr()?.Trim() ?? "",
                        AuthorName = "Narrator",
                        IsPlayerAction = false,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                }
                return entries.ToArray();
            }
            catch (Exception ex)
            {
                Instance.Log.LogError($"[Host] GetRecentTurns error: {ex.Message}");
                return new StoryEntry[0];
            }
        }

        private static void SendPartyUpdate(GameplayManager manager)
        {
            if (Server == null) return;

            var members = new List<RemoteCharacterInfo>();
            if (manager != null) members.Add(GetLocalCharacterInfo(manager));

            foreach (var c in Server.GetClients())
                if (c.CharacterInfo != null) members.Add(c.CharacterInfo);

            var payload = new PartyUpdatePayload { Members = members.ToArray() };
            Server.BroadcastPartyUpdate(payload);

            // Also update the host's own overlay (it doesn't receive broadcast packets)
            CoopStatusOverlay.Instance?.UpdateParty(payload.Members);
        }

        private static string TruncateForToast(string s, int maxLen = 60)
        {
            if (s == null) return "";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...";
        }
    }
}
