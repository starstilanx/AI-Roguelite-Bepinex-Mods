using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AIROG_RandomOrg
{
    /// <summary>
    /// Pre-fetches a buffer of true random integers from Random.org and dispenses
    /// them as 0..1 doubles to the Harmony dice patches.
    ///
    /// Two modes:
    ///   Anonymous — uses Random.org's plain-text HTTP interface (no key required,
    ///               ~1 million bits / day free tier).
    ///   API key   — uses the JSON-RPC 4 endpoint for higher quotas and usage tracking.
    /// </summary>
    public static class RandomOrgClient
    {
        private static readonly Queue<int> _buffer = new Queue<int>();
        private static readonly object _lock = new object();
        private static volatile bool _fetching = false;
        private static int _requestIdSeed = 0;

        // Random.org plain-text endpoint (anonymous, no API key)
        private const string BASIC_URL =
            "https://www.random.org/integers/?num={0}&min=1&max=1000000&col=1&base=10&format=plain&rnd=new";

        // Random.org JSON-RPC endpoint (requires API key)
        private const string RPC_URL = "https://api.random.org/json-rpc/4/invoke";

        // -----------------------------------------------------------------------

        public static int BufferCount
        {
            get { lock (_lock) { return _buffer.Count; } }
        }

        /// <summary>
        /// Try to dequeue one pre-fetched value as a double in [0, 1).
        /// Returns false if the buffer is empty — the caller should fall back to
        /// System.Random for that roll.  A background refetch is triggered
        /// automatically when the buffer runs low.
        /// </summary>
        public static bool TryGetRandom(out double value)
        {
            int raw;
            bool got;
            lock (_lock)
            {
                got = _buffer.Count > 0;
                raw = got ? _buffer.Dequeue() : 0;

                if (_buffer.Count < 200 && !_fetching)
                    ScheduleFetch(RandomOrgPlugin.BufferSize.Value);
            }

            // Map integers [1, 1000000] → doubles [0, 1)
            value = got ? (raw - 1) / 1_000_000.0 : 0.0;
            return got;
        }

        /// <summary>Kick off an initial fetch when the mod is first enabled.</summary>
        public static void PrimeFetch() => ScheduleFetch(RandomOrgPlugin.BufferSize.Value);

        // -----------------------------------------------------------------------

        private static void ScheduleFetch(int count)
        {
            _fetching = true;
            Task.Run(async () =>
            {
                try
                {
                    int[] numbers = await FetchAsync(count);
                    lock (_lock)
                    {
                        foreach (int n in numbers)
                            _buffer.Enqueue(n);
                    }
                    RandomOrgPlugin.Log.LogInfo(
                        $"[RandomOrg] Fetched {numbers.Length} numbers. Buffer: {BufferCount}");
                }
                catch (Exception ex)
                {
                    RandomOrgPlugin.Log.LogWarning(
                        $"[RandomOrg] Fetch failed — using System.Random as fallback: {ex.Message}");
                }
                finally
                {
                    _fetching = false;
                }
            });
        }

        private static async Task<int[]> FetchAsync(int count)
        {
            string apiKey = RandomOrgPlugin.ApiKey.Value?.Trim() ?? "";

            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(20);

                if (string.IsNullOrEmpty(apiKey))
                {
                    // ---- Anonymous mode: plain-text HTTP ----
                    string url = string.Format(BASIC_URL, count);
                    string body = await http.GetStringAsync(url);

                    var lines = body.Trim().Split(
                        new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                    var nums = new int[lines.Length];
                    for (int i = 0; i < lines.Length; i++)
                        nums[i] = int.Parse(lines[i].Trim());
                    return nums;
                }
                else
                {
                    // ---- API-key mode: JSON-RPC ----
                    var req = new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["method"]  = "generateIntegers",
                        ["params"]  = new JObject
                        {
                            ["apiKey"]      = apiKey,
                            ["n"]           = count,
                            ["min"]         = 1,
                            ["max"]         = 1_000_000,
                            ["replacement"] = true
                        },
                        ["id"] = Interlocked.Increment(ref _requestIdSeed)
                    };

                    var content  = new StringContent(req.ToString(), Encoding.UTF8, "application/json");
                    var response = await http.PostAsync(RPC_URL, content);
                    string json  = await response.Content.ReadAsStringAsync();

                    var jObj = JObject.Parse(json);
                    if (jObj["error"] != null)
                        throw new Exception($"API error: {jObj["error"]["message"]}");

                    return jObj["result"]["random"]["data"].ToObject<int[]>();
                }
            }
        }
    }
}
