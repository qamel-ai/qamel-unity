using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace QamelCapture.Benchmark
{
    /// <summary>
    /// Measures the performance impact of Qamel Capture on a synthetic but
    /// GPU+CPU-loaded scene. Runs a scenario matrix:
    ///
    ///   baseline (no Qamel)  x1
    ///   buffer mode          x (frameWidths x captureFpsValues)
    ///   streaming mode       x (frameWidths x captureFpsValues)
    ///
    /// and reports frame-time statistics (mean / p50 / p95 / p99), fps, GC
    /// collections and estimated capture bandwidth per scenario, plus overhead
    /// relative to baseline. Results go to the console and a CSV file.
    ///
    /// Usage: import this sample, drop the QamelBenchmark component into an empty
    /// scene, press play. For meaningful absolute numbers, run a standalone
    /// build (the editor adds noise); run several times and compare medians.
    /// The scene is generated procedurally, so no scene assets are needed.
    /// </summary>
    public sealed class QamelBenchmark : MonoBehaviour
    {
        [Header("Scene load (synthetic game)")]
        [Tooltip("Rotating lit cubes; raise until baseline fps is in your target range (e.g. 60-200).")]
        public int cubeCount = 500;
        [Tooltip("Milliseconds of busy CPU work per frame, simulating game logic.")]
        [Range(0f, 8f)]
        public float cpuLoadMsPerFrame = 2f;

        [Header("Timing")]
        public float warmupSeconds = 5f;
        public float measureSeconds = 20f;

        [Header("Capture matrix")]
        public int[] frameWidths = { 320, 640, 960, 1280 };
        public float[] captureFpsValues = { 5f, 10f, 15f };
        public int jpegQuality = 60;
        public bool testBufferMode = true;
        public bool testStreamingMode = true;
        [Tooltip("Chunk length for streaming scenarios.")]
        public int streamChunkSeconds = 10;

        [Header("Streaming upload (optional)")]
        [Tooltip("Leave empty to measure on-device cost only (chunks are built, then discarded). Set to a real endpoint + key to include actual network uploads.")]
        public string endpoint = "";
        public string apiKey = "";

        struct Scenario
        {
            public string Mode; // baseline | buffer | streaming
            public int FrameWidth;
            public float CaptureFps;
        }

        sealed class Result
        {
            public Scenario Scenario;
            public int FrameCount;
            public double MeanMs, P50Ms, P95Ms, P99Ms, Fps;
            public int Gc0Collections;
            public long CaptureBytes;
        }

        readonly List<Result> _results = new List<Result>();
        Transform[] _cubes;
        long _capturedBytes;

        IEnumerator Start()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            BuildScene();

            var scenarios = BuildScenarioList();
            Debug.Log("[QamelBenchmark] Running " + scenarios.Count + " scenarios, ~" +
                      Mathf.RoundToInt(scenarios.Count * (warmupSeconds + measureSeconds)) + "s total.");

            foreach (var scenario in scenarios)
            {
                yield return RunScenario(scenario);
            }

            Report();

            // In a standalone build there is nothing left to do; exit so CLI runs
            // terminate cleanly. In the editor, just stop play mode noise-free.
            if (!Application.isEditor) Application.Quit();
        }

        List<Scenario> BuildScenarioList()
        {
            var list = new List<Scenario> { new Scenario { Mode = "baseline" } };
            foreach (string mode in new[] { "buffer", "streaming" })
            {
                if (mode == "buffer" && !testBufferMode) continue;
                if (mode == "streaming" && !testStreamingMode) continue;
                foreach (int width in frameWidths)
                foreach (float fps in captureFpsValues)
                    list.Add(new Scenario { Mode = mode, FrameWidth = width, CaptureFps = fps });
            }
            return list;
        }

        IEnumerator RunScenario(Scenario scenario)
        {
            // Per-scenario capture stack, mirroring what QamelRunner assembles.
            QamelSettings settings = null;
            SessionBuffer buffer = null;
            FrameRecorder frameRecorder = null;
            LogRecorder logRecorder = null;
            InputRecorder inputRecorder = null;
            ChunkStreamer streamer = null;
            Uploader uploader = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Func<double> now = () => clock.Elapsed.TotalSeconds;
            _capturedBytes = 0;

            if (scenario.Mode != "baseline")
            {
                settings = ScriptableObject.CreateInstance<QamelSettings>();
                settings.frameWidth = scenario.FrameWidth;
                settings.captureFps = scenario.CaptureFps;
                settings.jpegQuality = jpegQuality;
                settings.streamChunkSeconds = streamChunkSeconds;
                settings.endpoint = endpoint;
                settings.apiKey = apiKey;

                int maxFrames = Mathf.CeilToInt(settings.bufferSeconds * scenario.CaptureFps) + 8;
                buffer = new SessionBuffer(settings.bufferSeconds, maxFrames);
                logRecorder = new LogRecorder(buffer, now);
                inputRecorder = new InputRecorder(settings, buffer, now);
                frameRecorder = new FrameRecorder(settings, buffer, now);
                StartCoroutine(frameRecorder.CaptureLoop());

                if (scenario.Mode == "streaming")
                {
                    bool upload = !string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(apiKey);
                    if (upload) uploader = new Uploader(settings, this);
                    var pendingUploads = new Queue<Action>();
                    streamer = new ChunkStreamer(settings, buffer, now,
                        Guid.NewGuid().ToString("N"), DateTime.UtcNow,
                        (manifest, bytes, fileName) =>
                        {
                            System.Threading.Interlocked.Add(ref _capturedBytes, bytes.Length);
                            if (upload)
                                lock (pendingUploads)
                                    pendingUploads.Enqueue(() => uploader.Enqueue(manifest, bytes, fileName, true));
                            // else: discard — measures on-device cost without network
                        });
                    StartCoroutine(streamer.StreamLoop());
                    StartCoroutine(DrainPendingUploads(pendingUploads));
                }
            }

            yield return MeasureLoop(scenario, logRecorder, inputRecorder, buffer);

            // Teardown between scenarios.
            streamer?.Stop();
            frameRecorder?.Dispose();
            logRecorder?.Dispose();
            if (settings != null) Destroy(settings);
            // Let in-flight readbacks/encodes finish so they don't bleed into the next scenario.
            yield return new WaitForSecondsRealtime(1f);
        }

        IEnumerator DrainPendingUploads(Queue<Action> pending)
        {
            while (true)
            {
                lock (pending)
                    while (pending.Count > 0) pending.Dequeue()();
                yield return null;
            }
        }

        IEnumerator MeasureLoop(Scenario scenario, LogRecorder logRecorder, InputRecorder inputRecorder, SessionBuffer buffer)
        {
            float warmupUntil = Time.realtimeSinceStartup + warmupSeconds;
            while (Time.realtimeSinceStartup < warmupUntil)
            {
                SimulateGame(logRecorder, inputRecorder);
                yield return null;
            }

            var frameMs = new List<double>(Mathf.CeilToInt(measureSeconds * 500));
            int gc0Before = GC.CollectionCount(0);
            float measureUntil = Time.realtimeSinceStartup + measureSeconds;
            while (Time.realtimeSinceStartup < measureUntil)
            {
                SimulateGame(logRecorder, inputRecorder);
                yield return null;
                frameMs.Add(Time.unscaledDeltaTime * 1000.0);
            }

            // In buffer mode, count buffered capture bytes as bandwidth-equivalent.
            long captureBytes = System.Threading.Interlocked.Read(ref _capturedBytes);
            if (scenario.Mode == "buffer" && buffer != null)
            {
                var frames = new List<CapturedFrame>();
                buffer.Snapshot(new List<string>(), frames);
                foreach (var f in frames) captureBytes += f.Jpg.Length;
            }

            frameMs.Sort();
            var result = new Result
            {
                Scenario = scenario,
                FrameCount = frameMs.Count,
                MeanMs = Mean(frameMs),
                P50Ms = Percentile(frameMs, 0.50),
                P95Ms = Percentile(frameMs, 0.95),
                P99Ms = Percentile(frameMs, 0.99),
                Gc0Collections = GC.CollectionCount(0) - gc0Before,
                CaptureBytes = captureBytes,
            };
            result.Fps = result.MeanMs > 0 ? 1000.0 / result.MeanMs : 0;
            _results.Add(result);

            Debug.Log("[QamelBenchmark] " + Label(scenario) + ": mean " +
                      result.MeanMs.ToString("0.000", CultureInfo.InvariantCulture) + " ms, p99 " +
                      result.P99Ms.ToString("0.000", CultureInfo.InvariantCulture) + " ms, " +
                      result.Fps.ToString("0.0", CultureInfo.InvariantCulture) + " fps");
        }

        void SimulateGame(LogRecorder logRecorder, InputRecorder inputRecorder)
        {
            // Rotate the cubes (animation load).
            float dt = Time.deltaTime;
            for (int i = 0; i < _cubes.Length; i++)
                _cubes[i].Rotate(37f * dt, 53f * dt, 11f * dt);

            // Busy CPU work simulating game logic.
            if (cpuLoadMsPerFrame > 0f)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                double x = 1.0001;
                while (sw.Elapsed.TotalMilliseconds < cpuLoadMsPerFrame) x = Math.Sqrt(x + 1.0);
            }

            // Tick recorders exactly like QamelRunner does.
            logRecorder?.Tick();
            inputRecorder?.Tick();
        }

        void BuildScene()
        {
            if (Camera.main == null)
            {
                var camObject = new GameObject("BenchmarkCamera");
                camObject.tag = "MainCamera";
                camObject.AddComponent<Camera>();
            }
            Camera.main.transform.position = new Vector3(0, 6, -22);
            Camera.main.transform.LookAt(Vector3.zero);

            var lightObject = new GameObject("BenchmarkLight");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            lightObject.transform.rotation = Quaternion.Euler(55f, -30f, 0f);

            var random = new System.Random(12345);
            _cubes = new Transform[cubeCount];
            for (int i = 0; i < cubeCount; i++)
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube).transform;
                cube.position = new Vector3(
                    (float)(random.NextDouble() * 24 - 12),
                    (float)(random.NextDouble() * 12 - 6),
                    (float)(random.NextDouble() * 20 - 4));
                float scale = 0.3f + (float)random.NextDouble() * 0.8f;
                cube.localScale = Vector3.one * scale;
                _cubes[i] = cube;
            }
        }

        void Report()
        {
            Result baseline = _results.Find(r => r.Scenario.Mode == "baseline");
            var csv = new StringBuilder();
            csv.AppendLine("mode,frame_width,capture_fps,frames_measured,mean_ms,p50_ms,p95_ms,p99_ms,fps,overhead_pct_vs_baseline,gc0_collections,capture_kb,capture_kb_per_s");

            var summary = new StringBuilder("[QamelBenchmark] RESULTS\n");
            summary.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-32} {1,9} {2,9} {3,9} {4,8} {5,10} {6,6} {7,10}",
                "scenario", "mean ms", "p95 ms", "p99 ms", "fps", "overhead", "GC0", "capture/s"));

            foreach (var r in _results)
            {
                double overheadPct = baseline != null && baseline.MeanMs > 0
                    ? (r.MeanMs - baseline.MeanMs) / baseline.MeanMs * 100.0
                    : 0;
                double kbPerS = r.CaptureBytes / 1024.0 / measureSeconds;

                csv.AppendLine(string.Join(",",
                    r.Scenario.Mode,
                    r.Scenario.FrameWidth.ToString(CultureInfo.InvariantCulture),
                    r.Scenario.CaptureFps.ToString(CultureInfo.InvariantCulture),
                    r.FrameCount.ToString(CultureInfo.InvariantCulture),
                    r.MeanMs.ToString("0.0000", CultureInfo.InvariantCulture),
                    r.P50Ms.ToString("0.0000", CultureInfo.InvariantCulture),
                    r.P95Ms.ToString("0.0000", CultureInfo.InvariantCulture),
                    r.P99Ms.ToString("0.0000", CultureInfo.InvariantCulture),
                    r.Fps.ToString("0.00", CultureInfo.InvariantCulture),
                    overheadPct.ToString("0.000", CultureInfo.InvariantCulture),
                    r.Gc0Collections.ToString(CultureInfo.InvariantCulture),
                    (r.CaptureBytes / 1024).ToString(CultureInfo.InvariantCulture),
                    kbPerS.ToString("0.0", CultureInfo.InvariantCulture)));

                summary.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-32} {1,9:0.000} {2,9:0.000} {3,9:0.000} {4,8:0.0} {5,9:+0.00;-0.00}% {6,6} {7,8:0.0}KB",
                    Label(r.Scenario), r.MeanMs, r.P95Ms, r.P99Ms, r.Fps, overheadPct, r.Gc0Collections, kbPerS));
            }

            string csvPath = Path.Combine(Application.persistentDataPath,
                "qamel_benchmark_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + ".csv");
            File.WriteAllText(csvPath, csv.ToString());
            summary.AppendLine("CSV written to: " + csvPath);
            Debug.Log(summary.ToString());
        }

        static string Label(Scenario s) =>
            s.Mode == "baseline" ? "baseline" : s.Mode + " " + s.FrameWidth + "px@" + s.CaptureFps + "fps";

        static double Mean(List<double> sorted)
        {
            if (sorted.Count == 0) return 0;
            double sum = 0;
            foreach (var v in sorted) sum += v;
            return sum / sorted.Count;
        }

        static double Percentile(List<double> sorted, double p)
        {
            if (sorted.Count == 0) return 0;
            double rank = p * (sorted.Count - 1);
            int low = (int)rank;
            int high = Math.Min(low + 1, sorted.Count - 1);
            double frac = rank - low;
            return sorted[low] * (1 - frac) + sorted[high] * frac;
        }
    }
}
