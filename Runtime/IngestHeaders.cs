namespace QamelCapture
{
    /// <summary>
    /// Shared Authorization / plugin identity headers for ingest POSTs
    /// (report, chunk, plugin-error). Kept tiny so tests can lock the wire
    /// format without spinning up UnityWebRequest.
    /// </summary>
    internal static class IngestHeaders
    {
        public const string Authorization = "Authorization";
        public const string Plugin = "X-Qamel-Plugin";

        public static string Bearer(string apiKey) =>
            "Bearer " + (apiKey ?? "").Trim();

        public static string PluginValue() =>
            "unity/" + QamelSettings.PluginVersion;
    }
}
