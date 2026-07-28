using System;
using UnityEngine;

namespace QamelCapture
{
    /// <summary>Inputs for <see cref="ReportManifest.Build"/> (kept separate from Unity APIs for testing).</summary>
    internal sealed class ReportManifestData
    {
        public string SessionId;
        public string ReportId;
        public double ReportT;
        public string UserText;
        public int EventCount;
        public int FrameCount;
        public int FrameWidth;
        public int FrameHeight;
        public DateTime SessionStartedUtc;
        public int BufferSeconds;
        public float CaptureFps;
        public IdentitySnapshot Identity;
        public string BuildId;
    }

    /// <summary>Inputs for <see cref="ReportManifest.BuildChunk"/>.</summary>
    internal sealed class ChunkManifestData
    {
        public string SessionId;
        public int ChunkIndex;
        public double ChunkStartT;
        public double ChunkEndT;
        public int EventCount;
        public int FrameCount;
        public int FrameWidth;
        public int FrameHeight;
        public DateTime SessionStartedUtc;
        public float CaptureFps;
        public IdentitySnapshot Identity;
        public string BuildId;
    }

    /// <summary>Builds manifest.json per the Qamel capture wire format.</summary>
    internal static class ReportManifest
    {
        /// <summary>
        /// Engine-native device signal retained for diagnostics. Participant
        /// grouping uses installation/external player identity instead.
        /// </summary>
        internal static string DeviceId
        {
            get
            {
                string id = SystemInfo.deviceUniqueIdentifier;
                return id == SystemInfo.unsupportedIdentifier ? "" : id;
            }
        }

        public static string Build(ReportManifestData data)
        {
            var json = new QamelJson().Begin()
                .Str("schema", ReportBundler.SchemaVersion)
                .Str("kind", "report")
                .Str("session_id", data.SessionId)
                .Str("report_id", data.ReportId)
                .Str("session_started_utc", data.SessionStartedUtc.ToString("o"));
            AppendContext(json, data.Identity, data.BuildId);
            return json
                .Num("report_t", data.ReportT)
                .Int("buffer_seconds", data.BufferSeconds)
                .Num("capture_fps", data.CaptureFps)
                .Int("frame_width", data.FrameWidth)
                .Int("frame_height", data.FrameHeight)
                .Int("event_count", data.EventCount)
                .Int("frame_count", data.FrameCount)
                .Str("user_text", data.UserText ?? "")
                .End();
        }

        public static string BuildChunk(ChunkManifestData data)
        {
            var json = new QamelJson().Begin()
                .Str("schema", ReportBundler.SchemaVersion)
                .Str("kind", "chunk")
                .Str("session_id", data.SessionId)
                .Int("chunk_index", data.ChunkIndex)
                .Str("session_started_utc", data.SessionStartedUtc.ToString("o"));
            AppendContext(json, data.Identity, data.BuildId);
            return json
                .Num("chunk_start_t", data.ChunkStartT)
                .Num("chunk_end_t", data.ChunkEndT)
                .Num("capture_fps", data.CaptureFps)
                .Int("frame_width", data.FrameWidth)
                .Int("frame_height", data.FrameHeight)
                .Int("event_count", data.EventCount)
                .Int("frame_count", data.FrameCount)
                .End();
        }

        internal static QamelJson AppendContext(
            QamelJson json,
            IdentitySnapshot identity,
            string buildId)
        {
            return json
                .Str("engine", "unity")
                .Str("engine_version", Application.unityVersion)
                .Str("plugin", "com.qamel.unity")
                .Str("plugin_version", QamelSettings.PluginVersion)
                .Str("game_name", Application.productName)
                .Str("game_version", Application.version)
                .Str("build_id", buildId ?? "")
                .Str("platform", Application.platform.ToString())
                .Str("os", SystemInfo.operatingSystem)
                .Str("run_environment", Application.isEditor ? "editor" : "player")
                .Str("build_configuration", Debug.isDebugBuild ? "development" : "release")
                .Str("participant_kind", identity.ParticipantKind ?? "unknown")
                .Str("installation_id", identity.InstallationId ?? "")
                .Str("external_player_id", identity.ExternalPlayerId ?? "")
                .Str("device_id", DeviceId)
                .Str("device_model", SystemInfo.deviceModel)
                .Str("cpu_architecture", CpuArchitecture())
                .Str("cpu_model", SystemInfo.processorType)
                .Str("gpu", SystemInfo.graphicsDeviceName)
                .Str("graphics_api", SystemInfo.graphicsDeviceType.ToString())
                .Int("system_memory_mb", SystemInfo.systemMemorySize)
                .Int("screen_width", Screen.width)
                .Int("screen_height", Screen.height)
                .Int("display_refresh_hz", Screen.currentResolution.refreshRate)
                .Str("system_language", Application.systemLanguage.ToString())
                .Str("quality_preset", QualityPreset());
        }

        static string CpuArchitecture()
        {
            string processor = (SystemInfo.processorType ?? "").ToLowerInvariant();
            bool is64 = IntPtr.Size == 8;
            if (processor.Contains("arm") || processor.Contains("apple"))
                return is64 ? "arm64" : "arm";
            if (processor.Contains("intel") || processor.Contains("amd") ||
                processor.Contains("x86") || processor.Contains("x64"))
                return is64 ? "x86_64" : "x86";
            return is64 ? "64-bit" : "32-bit";
        }

        static string QualityPreset()
        {
            int level = QualitySettings.GetQualityLevel();
            string[] names = QualitySettings.names;
            return names != null && level >= 0 && level < names.Length
                ? names[level]
                : level.ToString();
        }
    }
}
