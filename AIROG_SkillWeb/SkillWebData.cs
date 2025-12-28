using System;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
using System.IO;
using Newtonsoft.Json.Linq;

namespace AIROG_SkillWeb
{
    [Serializable]
    public class SkillNode
    {
        public string id;
        public string name;
        public string description;
        public Vector2 position;
        public List<string> connectedIds = new List<string>();
        public Dictionary<string, float> statModifiers = new Dictionary<string, float>();
        public bool isUnlocked = false;
        public string imageUuid;
        public string treeId; // Which discipline/tree this node belongs to
        public List<string> narrativeTraits = new List<string>(); // Non-numerical capabilities

        public SkillNode() { }

        public SkillNode(string id, string name, string description, Vector2 position)
        {
            this.id = id;
            this.name = name;
            this.description = description;
            this.position = position;
        }
    }

    public class SkillWebData
    {
        public List<SkillNode> nodes = new List<SkillNode>();
        public List<SkillTree> trees = new List<SkillTree>();
        public float nodeRadius = 50f;

        public int skillPoints = 0;
        public int lastKnownLevel = 1;

        // Not serialized, rebuilt on load
        [JsonIgnore]
        public Dictionary<SS.PlayerAttribute, float> CachedStats = new Dictionary<SS.PlayerAttribute, float>();

        public static SkillWebData Load(string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    var settings = new JsonSerializerSettings();
                    settings.Converters.Add(new Vector2Converter());
                    return JsonConvert.DeserializeObject<SkillWebData>(json, settings);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SkillWeb] Error loading data: {ex.Message}");
                }
            }
            return new SkillWebData();
        }

        public void Save(string path)
        {
            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
            };
            settings.Converters.Add(new Vector2Converter());
            string json = JsonConvert.SerializeObject(this, settings);
            File.WriteAllText(path, json);
        }

        public bool CheckCollision(Vector2 pos, string excludeId = null)
        {
            foreach (var node in nodes)
            {
                if (node.id == excludeId) continue;
                if (Vector2.Distance(node.position, pos) < nodeRadius * 2)
                {
                    return true;
                }
            }
            return false;
        }

        public void AddNode(SkillNode node)
        {
            if (!CheckCollision(node.position))
            {
                nodes.Add(node);
            }
            else
            {
                Debug.LogWarning($"[SkillWeb] Node {node.id} collision detected at {node.position}");
            }
        }

        public void AddConnection(string id1, string id2)
        {
            var node1 = nodes.Find(n => n.id == id1);
            var node2 = nodes.Find(n => n.id == id2);

            if (node1 != null && node2 != null)
            {
                if (!node1.connectedIds.Contains(id2)) node1.connectedIds.Add(id2);
                if (!node2.connectedIds.Contains(id1)) node2.connectedIds.Add(id1);
            }
        }

        public void RecalculateStats()
        {
            if (CachedStats == null) CachedStats = new Dictionary<SS.PlayerAttribute, float>();
            CachedStats.Clear();
            foreach (var node in nodes)
            {
                if (node.isUnlocked && node.statModifiers != null)
                {
                    foreach (var kvp in node.statModifiers)
                    {
                        if (Enum.TryParse(kvp.Key, true, out SS.PlayerAttribute attr))
                        {
                            if (!CachedStats.ContainsKey(attr)) CachedStats[attr] = 0;
                            CachedStats[attr] += kvp.Value;
                        }
                    }
                }
            }
        }

        public SkillTree GetTree(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return trees.Find(t => t.id == id);
        }
    }

    [Serializable]
    public class SkillTree
    {
        public string id;
        public string name;
        public string purpose;
        public string colorHex = "#FFFFFF";

        public SkillTree() { }
        public SkillTree(string id, string name, string purpose)
        {
            this.id = id;
            this.name = name;
            this.purpose = purpose;
        }
    }

    public class Vector2Converter : JsonConverter
    {
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            Vector2 v = (Vector2)value;
            JObject jo = new JObject();
            jo.Add("x", v.x);
            jo.Add("y", v.y);
            jo.WriteTo(writer);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JObject jo = JObject.Load(reader);
            float x = jo["x"].Value<float>();
            float y = jo["y"].Value<float>();
            return new Vector2(x, y);
        }

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(Vector2);
        }
    }
}
