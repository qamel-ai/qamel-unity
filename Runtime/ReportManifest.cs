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
    }

    /// <summary>Builds manifest.json per the Qamel capture wire format.</summary>
    internal static class ReportManifest
    {
        /// <summary>
        /// Stable per-device id used server-side to group anonymous (keyless)
        /// uploads. Unity may return an unsupported sentinel on some platforms;
        /// map that to "" per spec.
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
            return new QamelJson().Begin()
                .Str("schema", ReportBundler.SchemaVersion)
                .Str("kind", "report")
                .Str("session_id", data.SessionId)
                .Str("report_id", data.ReportId)
                .Str("engine", "unity")
                .Str("engine_version", Application.unityVersion)
                .Str("plugin", "com.qamel.unity")
                .Str("plugin_version", QamelSettings.PluginVersion)
                .Str("game_name", Application.productName)
                .Str("game_version", Application.version)
                .Str("platform", Application.platform.ToString())
                .Str("os", SystemInfo.operatingSystem)
                .Str("device_id", DeviceId)
                .Str("device_model", SystemInfo.deviceModel)
                .Str("gpu", SystemInfo.graphicsDeviceName)
                .Int("system_memory_mb", SystemInfo.systemMemorySize)
                .Int("screen_width", Screen.width)
                .Int("screen_height", Screen.height)
                .Str("session_started_utc", data.SessionStartedUtc.ToString("o"))
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
            return new QamelJson().Begin()
                .Str("schema", ReportBundler.SchemaVersion)
                .Str("kind", "chunk")
                .Str("session_id", data.SessionId)
                .Int("chunk_index", data.ChunkIndex)
                .Str("engine", "unity")
                .Str("engine_version", Application.unityVersion)
                .Str("plugin", "com.qamel.unity")
                .Str("plugin_version", QamelSettings.PluginVersion)
                .Str("game_name", Application.productName)
                .Str("game_version", Application.version)
                .Str("platform", Application.platform.ToString())
                .Str("os", SystemInfo.operatingSystem)
                .Str("device_id", DeviceId)
                .Str("session_started_utc", data.SessionStartedUtc.ToString("o"))
                .Num("chunk_start_t", data.ChunkStartT)
                .Num("chunk_end_t", data.ChunkEndT)
                .Num("capture_fps", data.CaptureFps)
                .Int("frame_width", data.FrameWidth)
                .Int("frame_height", data.FrameHeight)
                .Int("event_count", data.EventCount)
                .Int("frame_count", data.FrameCount)
                .End();
        }
    }
}
