using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;

namespace AIROG_Multiplayer.Network
{
    /// <summary>
    /// All message types that can be sent over the network.
    /// </summary>
    public enum PacketType
    {
        // Handshake
        Hello,       // Client -> Host: "I'm connecting, here's my character info"
        Welcome,     // Host -> Client: "Connected! Here's the current game state"
        Rejected,    // Host -> Client: "Can't connect right now"

        // Game State
        StoryTurn,      // Host -> All Clients: a new story log entry was added
        CharacterUpdate,// Host -> Client: that client's character stats changed
        PartyUpdate,    // Host -> All Clients: full party member list update
        LocationUpdate, // Host -> All Clients: host moved to a new location
        SaveData,       // Host -> Client: full save file (GZip-compressed) + world summary
        StoryImage,     // Host -> All Clients: AI-generated story turn image (PNG bytes)

        // Turn Gate (v2.0)
        TurnBegin,      // Host -> All Clients: "new turn started, submit your action now"
        TurnReady,      // Client -> Host: "I've submitted my action for this turn"
        WaitingForParty,// Host -> All Clients: "waiting for X/Y players to act"

        // Actions
        ActionRequest,  // Client -> Host: "I want to do this action"
        ActionQueued,   // Host -> Client: "Your action is queued for next turn"

        // Utility
        Chat,       // Bidirectional: out-of-character chat message
        Ping,       // Keepalive ping
        Pong,       // Keepalive response
        Disconnect,  // Graceful disconnect notification

        // Inventory
        InventorySync   // Host -> All Clients: full MPInventoryDatabase JSON
    }

    /// <summary>
    /// Represents a player's character as known to the network.
    /// Lightweight — only what other players and the AI context need.
    /// </summary>
    [Serializable]
    public class RemoteCharacterInfo
    {
        public string PlayerName { get; set; }      // Steam/display name
        public string CharacterName { get; set; }   // In-game name
        public string CharacterClass { get; set; }  // Class description
        public string CharacterBackground { get; set; } // Background/description
        public string Personality { get; set; }     // Personality traits, motivations, goals
        public string CharacterAppearance { get; set; } // Physical appearance / description
        public long Health { get; set; }
        public long MaxHealth { get; set; }
        public int Level { get; set; }
        public string CurrentLocation { get; set; }
    }

    /// <summary>
    /// A serialized story log entry to relay to clients.
    /// </summary>
    [Serializable]
    public class StoryEntry
    {
        public string Text { get; set; }
        public string AuthorName { get; set; } // "Host", "ClientPlayer", "Narrator" etc.
        public bool IsPlayerAction { get; set; }
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Base packet — all messages share this envelope.
    /// </summary>
    [Serializable]
    public class Packet
    {
        public PacketType Type { get; set; }
        public string Payload { get; set; } // JSON-serialized inner data

        // --- Helpers ---
        public T GetPayload<T>() => JsonConvert.DeserializeObject<T>(Payload ?? "{}");

        public static Packet Create<T>(PacketType type, T data)
        {
            return new Packet
            {
                Type = type,
                Payload = JsonConvert.SerializeObject(data)
            };
        }

        public static Packet Create(PacketType type)
        {
            return new Packet { Type = type, Payload = "{}" };
        }

        // --- Wire format: [int32 LE length][UTF-8 JSON body] ---
        public byte[] Serialize()
        {
            byte[] body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this));
            byte[] lengthPrefix = BitConverter.GetBytes(body.Length); // 4 bytes, little-endian
            byte[] result = new byte[4 + body.Length];
            Buffer.BlockCopy(lengthPrefix, 0, result, 0, 4);
            Buffer.BlockCopy(body, 0, result, 4, body.Length);
            return result;
        }

        /// <summary>
        /// Reads the next packet from a NetworkStream.
        /// Returns null if the stream is closed/EOF.
        /// </summary>
        public static Packet ReadFrom(NetworkStream stream)
        {
            byte[] lenBuf = ReadExact(stream, 4);
            if (lenBuf == null) return null;

            int length = BitConverter.ToInt32(lenBuf, 0);
            if (length <= 0 || length > 1024 * 1024 * 64) // Sanity: max 64 MB (compressed saves + images)
                throw new IOException($"Invalid packet length: {length}");

            byte[] body = ReadExact(stream, length);
            if (body == null) return null;

            string json = Encoding.UTF8.GetString(body);
            return JsonConvert.DeserializeObject<Packet>(json);
        }

        private static byte[] ReadExact(NetworkStream stream, int count)
        {
            byte[] buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buf, read, count - read);
                if (n == 0) return null; // Connection closed
                read += n;
            }
            return buf;
        }
    }

    // --- Payload types for specific packet types ---

    [Serializable]
    public class HelloPayload
    {
        public string PluginVersion { get; set; }
        public RemoteCharacterInfo Character { get; set; }
    }

    [Serializable]
    public class WelcomePayload
    {
        public string AssignedPlayerId { get; set; }
        public string HostCharacterName { get; set; }
        public string CurrentLocation { get; set; }
        public string RecentStory { get; set; }    // Legacy: kept for compatibility
        public StoryEntry[] RecentTurns { get; set; } // Last N story turns as structured entries
    }

    [Serializable]
    public class ActionRequestPayload
    {
        public string PlayerId { get; set; }
        public string CharacterName { get; set; }
        public string ActionText { get; set; }
        public long Timestamp { get; set; }
    }

    [Serializable]
    public class ChatPayload
    {
        public string SenderName { get; set; }
        public string Message { get; set; }
        public bool IsSystem { get; set; }
    }

    [Serializable]
    public class PartyUpdatePayload
    {
        public RemoteCharacterInfo[] Members { get; set; }
    }

    [Serializable]
    public class LocationUpdatePayload
    {
        public string LocationName { get; set; }
        public string LocationDescription { get; set; }
    }

    [Serializable]
    public class SaveDataPayload
    {
        /// <summary>
        /// Full save file JSON, GZip-compressed and Base64-encoded.
        /// Clients should decompress this and write it to their save directory.
        /// </summary>
        public string SaveFileGzipB64 { get; set; }

        /// <summary>Legacy uncompressed JSON (null in new saves).</summary>
        public string SaveJson { get; set; }

        public string SaveName { get; set; }
        public long Timestamp { get; set; }

        /// <summary>
        /// The host's save subdirectory name (SS.I.saveSubDirAsArg).
        /// Clients replace this prefix in image paths so they point to mp_client/.
        /// </summary>
        public string HostSaveSubDir { get; set; }

        /// <summary>
        /// Voronoi world polygon files: key = UUID (filename without .txt), value = file content JSON.
        /// Written to mp_client/{uuid}.txt so the game can load world map polygons.
        /// </summary>
        public Dictionary<string, string> PolygonFiles { get; set; }

        // Parsed world state summary for the overlay (populated by host)
        public string CurrentPlaceName { get; set; }
        public string CurrentPlaceDescription { get; set; }
        public string[] VisibleNPCNames { get; set; }
        public string[] VisibleItemNames { get; set; }
    }

    /// <summary>
    /// AI-generated story image relayed from host to all clients.
    /// PngBase64 is the PNG file content encoded as Base64.
    /// FileName is the save-relative filename (e.g. "uuid.png") so the client
    /// can write it to the correct location for the game engine to find it.
    /// </summary>
    [Serializable]
    public class StoryImagePayload
    {
        public string PngBase64 { get; set; }
        public string StoryTurnText { get; set; }
        /// <summary>Save-relative image filename, e.g. "abc123.png".</summary>
        public string FileName { get; set; }
    }

    /// <summary>
    /// v2.0: Status of the party turn gate.
    /// </summary>
    [Serializable]
    public class WaitingForPartyPayload
    {
        public int ReadyCount { get; set; }
        public int TotalCount { get; set; }
    }

    /// <summary>
    /// Carries the full MPInventoryDatabase as compact JSON.
    /// Sent from host to all clients (or targeted on join) whenever inventory changes.
    /// </summary>
    [Serializable]
    public class InventorySyncPayload
    {
        /// <summary>Compact JSON of MPInventoryDatabase.</summary>
        public string InventoryJson { get; set; }
    }

    /// <summary>
    /// GZip compression helpers for large payloads (save files).
    /// Safe to call from background threads.
    /// </summary>
    public static class PacketUtils
    {
        public static string GzipCompress(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
                    gz.Write(bytes, 0, bytes.Length);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        public static string GzipDecompress(string base64)
        {
            byte[] compressed = Convert.FromBase64String(base64);
            using (var ms = new MemoryStream(compressed))
            using (var gz = new GZipStream(ms, CompressionMode.Decompress))
            using (var reader = new StreamReader(gz, Encoding.UTF8))
                return reader.ReadToEnd();
        }
    }
}
