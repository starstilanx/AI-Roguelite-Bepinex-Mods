using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIROG_Core;
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

        // After a failed fetch no new one is started until this time. Without it every roll made
        // while offline or over quota fired another request the moment the last one failed, and
        // Random.org bans clients that keep asking once their allowance is spent.
        // Both guarded by _lock.
        private static DateTime _retryNotBeforeUtc = DateTime.MinValue;
        private static int _consecutiveFailures = 0;

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

                if (_buffer.Count < 200 && CanStartFetch())
                    ScheduleFetch(RandomOrgPlugin.BufferSize.Value);
            }

            // Map integers [1, 1000000] → doubles [0, 1)
            value = got ? (raw - 1) / 1_000_000.0 : 0.0;
            return got;
        }

        /// <summary>Kick off an initial fetch when the mod is first enabled.</summary>
        public static void PrimeFetch()
        {
            // The Mods menu calls this on every Confirm while the buffer is empty, so it must not
            // start a second request on top of one already in flight.
            lock (_lock)
            {
                if (CanStartFetch())
                    ScheduleFetch(RandomOrgPlugin.BufferSize.Value);
            }
        }

        // -----------------------------------------------------------------------

        /// <summary>Caller holds _lock.</summary>
        private static bool CanStartFetch() => !_fetching && DateTime.UtcNow >= _retryNotBeforeUtc;

        /// <summary>Caller holds _lock.</summary>
        private static void ScheduleFetch(int count)
        {
            // Both Random.org interfaces reject n outside 1..10000, which would fail every fetch.
            count = Math.Max(1, Math.Min(10000, count));

            _fetching = true;
            Task.Run(async () =>
            {
                try
                {
                    int[] numbers = await FetchAsync(count);
                    if (numbers == null || numbers.Length == 0)
                        throw new Exception("Random.org returned no numbers.");
                    lock (_lock)
                    {
                        foreach (int n in numbers)
                            _buffer.Enqueue(n);
                        _consecutiveFailures = 0;
                    }
                    RandomOrgPlugin.Log.LogInfo(
                        $"[RandomOrg] Fetched {numbers.Length} numbers. Buffer: {BufferCount}");
                }
                catch (Exception ex)
                {
                    int waitSeconds;
                    lock (_lock)
                    {
                        // 1, 2, 4, 8, then 10 minutes.
                        waitSeconds = Math.Min(600, 60 << Math.Min(_consecutiveFailures, 4));
                        _consecutiveFailures++;
                        _retryNotBeforeUtc = DateTime.UtcNow.AddSeconds(waitSeconds);
                    }
                    RandomOrgPlugin.Log.LogWarning(
                        $"[RandomOrg] Fetch failed — using System.Random as fallback, retrying in {waitSeconds}s: {ex.Message}");
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
            HttpClient http = SingletonHttpClient.Instance;

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                if (string.IsNullOrEmpty(apiKey))
                {
                    // ---- Anonymous mode: plain-text HTTP ----
                    string url = string.Format(BASIC_URL, count);
                    var getResponse = await http.GetAsync(url, cts.Token);
                    getResponse.EnsureSuccessStatusCode();
                    string body = await getResponse.Content.ReadAsStringAsync();

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
                    var response = await http.PostAsync(RPC_URL, content, cts.Token);
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
