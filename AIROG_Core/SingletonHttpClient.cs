using System.Net.Http;

namespace AIROG_Core
{
    /// <summary>
    /// Shared HttpClient instance for all AIROG mods. HttpClient is meant to be created
    /// once and reused for the app's lifetime — creating one per request (the pattern
    /// several mods used) exhausts sockets under bursty load. Don't mutate
    /// Instance.Timeout from a call site; use a per-request CancellationToken instead,
    /// since the instance is shared across mods that may be calling concurrently.
    /// </summary>
    public static class SingletonHttpClient
    {
        public static readonly HttpClient Instance = new HttpClient();
    }
}
