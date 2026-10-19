using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace DeadPines.Diagnostics
{
    /// <summary>
    /// Measures the running game and prints one report.
    ///
    /// Temporary tooling for M0, not part of the game: the object it lives on is added by
    /// 'Dead Pines/Measure — Add the Performance Probe' and removed again afterwards.
    ///
    /// It answers the four questions the build has to answer before the scene grows: how long a
    /// frame takes, how much of that is the main thread, how much is the GPU, and what the frame
    /// costs in draw calls and set-pass calls. Memory is sampled once a second for the whole run,
    /// so a leak shows up as a line that keeps climbing rather than as a single reading.
    ///
    /// GPU time comes from FrameTimingManager, which lags a few frames and returns zero where the
    /// platform cannot measure it. Availability depends on the graphics API and driver, not on
    /// whether this is the Editor: Direct3D 11 on the RX 580 reported timings here, while another
    /// driver may report none, and the report prints n/a rather than zeros as if they were fast.
    ///
    /// The frame time is the wall clock between Update calls, so it includes everything in the
    /// frame. The CPU and GPU timings are absent on platforms that do not fill them in; those
    /// rows print n/a.
    ///
    /// Implements OnDisable so a run stopped early still prints what it had.
    /// </summary>
    [DisallowMultipleComponent]
    public class PerformanceProbe : MonoBehaviour
    {
        [Header("Run")]
        // Three minutes rather than ten: long enough for the frame and draw-call numbers to
        // settle and for a first look at memory, short enough to run without ceremony. It is a
        // weaker leak check than a longer soak — a slow climb may not have shown up yet.
        [Tooltip("Seconds of sampling before the report, after the warm-up.")]
        [SerializeField] float sampleSeconds = 180f;

        [Tooltip("Seconds ignored at the start, while shaders compile and the terrain settles.")]
        [SerializeField] float warmupSeconds = 10f;

        [Tooltip("Seconds between one-line status prints.")]
        [SerializeField] float statusSeconds = 60f;

        // Histogram bins for percentiles. Every series gets its own width below: half a
        // millisecond is finer than anything the scene can act on for timings, but a count series
        // needs to be spread across the whole range — with millisecond bins every draw count
        // lands in the overflow bin and every percentile reports the maximum.
        const int BinCount = 256;

        // The Rendering Profiler module's counters. If a name ever changes, its recorder comes
        // back invalid and that row prints n/a instead of taking the probe down with it.
        const string DrawCallsCounter = "Draw Calls Count";
        const string SetPassCounter = "SetPass Calls Count";
        const string BatchesCounter = "Batches Count";
        const string TrianglesCounter = "Triangles Count";

        /// <summary>
        /// One accumulated series: count, mean, percentiles and extremes. A histogram rather than
        /// a list of every frame, so a run of several minutes at several hundred frames a second
        /// costs a kilobyte instead of tens of megabytes.
        /// </summary>
        sealed class Stat
        {
            readonly int[] bins = new int[BinCount];
            readonly float binWidth;

            public Stat(float binWidth)
            {
                this.binWidth = binWidth;
            }

            public long Count;
            public double Sum;
            public float Max;

            public void Add(float value)
            {
                if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
                    return;

                Count++;
                Sum += value;

                if (value > Max)
                    Max = value;

                int bin = (int)(value / binWidth);
                if (bin >= BinCount)
                    bin = BinCount - 1;

                bins[bin]++;
            }

            public float Mean
            {
                get { return Count > 0 ? (float)(Sum / Count) : 0f; }
            }

            /// <summary>
            /// Upper bound of the bin holding the requested percentile. Coarse to half a
            /// millisecond by construction; the overflow bin answers with the true maximum.
            /// </summary>
            public float Percentile(float percent)
            {
                if (Count == 0)
                    return 0f;

                float target = Count * Mathf.Clamp(percent, 0f, 100f) * 0.01f;
                float seen = 0f;

                for (int i = 0; i < BinCount; i++)
                {
                    seen += bins[i];

                    if (seen >= target)
                        return i == BinCount - 1 ? Max : (i + 1) * binWidth;
                }

                return Max;
            }
        }

        struct MemSample
        {
            public float at;
            public long gfx;
            public long total;
            public long mono;
        }

        readonly FrameTiming[] timings = new FrameTiming[1];
        readonly List<MemSample> memory = new List<MemSample>();

        readonly Stat frameMs = new Stat(0.5f);
        readonly Stat mainThreadMs = new Stat(0.5f);
        readonly Stat renderThreadMs = new Stat(0.5f);
        readonly Stat gpuMs = new Stat(0.5f);

        // Counts, not milliseconds: 256 bins of 128 draw calls cover 32,768 — past this scene's
        // 20,797 peak — and 256 bins of 100,000 triangles cover 25.6 million, past its 24.3.
        readonly Stat drawCalls = new Stat(128f);
        readonly Stat setPassCalls = new Stat(1f);
        readonly Stat batches = new Stat(128f);
        readonly Stat triangles = new Stat(100000f);

        ProfilerRecorder drawRecorder;
        ProfilerRecorder setPassRecorder;
        ProfilerRecorder batchRecorder;
        ProfilerRecorder triangleRecorder;

        float startedAt;
        float lastStatusAt;
        float lastMemoryAt;
        ulong lastTimestamp;
        bool haveTiming;
        bool finished;
        bool disposed;

        int shadowCasters;
        int rendererCount;
        long renderedFrames;

        void Start()
        {
            startedAt = Time.realtimeSinceStartup;
            lastStatusAt = startedAt;
            lastMemoryAt = startedAt - 1f;

            drawRecorder = ProfilerRecorder.StartNew(
                ProfilerCategory.Render, DrawCallsCounter, 1, ProfilerRecorderOptions.Default);
            setPassRecorder = ProfilerRecorder.StartNew(
                ProfilerCategory.Render, SetPassCounter, 1, ProfilerRecorderOptions.Default);
            batchRecorder = ProfilerRecorder.StartNew(
                ProfilerCategory.Render, BatchesCounter, 1, ProfilerRecorderOptions.Default);
            triangleRecorder = ProfilerRecorder.StartNew(
                ProfilerCategory.Render, TrianglesCounter, 1, ProfilerRecorderOptions.Default);

            ScanShadows();
            LogHeader();

            SampleMemory(0f);
        }

        void Update()
        {
            if (finished)
                return;

            float now = Time.realtimeSinceStartup;

            // Capture is cheap and the timings lag, so it runs from the first frame; only the
            // accumulation waits for the warm-up.
            FrameTimingManager.CaptureFrameTimings();

            if (now - startedAt < warmupSeconds)
                return;

            frameMs.Add(Time.unscaledDeltaTime * 1000f);

            // The newest timing can repeat across frames while the GPU catches up. Timestamping
            // it keeps one frame from counting four times in the mean.
            uint got = FrameTimingManager.GetLatestTimings(1, timings);

            if (got > 0 && (!haveTiming || timings[0].frameStartTimestamp != lastTimestamp))
            {
                haveTiming = true;
                lastTimestamp = timings[0].frameStartTimestamp;

                // Zero is how these fields report "no timing", not a frame that took no time at
                // all. Counting zeros would turn an unavailable GPU timer into a suspiciously
                // fast one, so each series only accepts a positive reading.
                if (timings[0].cpuMainThreadFrameTime > 0.0)
                    mainThreadMs.Add((float)timings[0].cpuMainThreadFrameTime);

                if (timings[0].cpuRenderThreadFrameTime > 0.0)
                    renderThreadMs.Add((float)timings[0].cpuRenderThreadFrameTime);

                if (timings[0].gpuFrameTime > 0.0)
                    gpuMs.Add((float)timings[0].gpuFrameTime);
            }

            // A zero here means the render profiler had no sample that frame, not a frame that
            // drew nothing. Counting zeros would halve the means whenever the Editor rendered
            // only one of its views, and leave the percentiles describing nothing.
            long draw = drawRecorder.Valid ? drawRecorder.LastValue : 0;

            if (draw > 0)
            {
                renderedFrames++;
                drawCalls.Add(draw);

                if (setPassRecorder.Valid) setPassCalls.Add(setPassRecorder.LastValue);
                if (batchRecorder.Valid) batches.Add(batchRecorder.LastValue);
                if (triangleRecorder.Valid) triangles.Add(triangleRecorder.LastValue);
            }

            if (now - lastMemoryAt >= 1f)
            {
                lastMemoryAt = now;
                SampleMemory(now - startedAt);
            }

            if (now - lastStatusAt >= statusSeconds)
            {
                lastStatusAt = now;
                LogStatus(now - startedAt);
            }

            if (now - startedAt >= warmupSeconds + sampleSeconds)
            {
                finished = true;
                LogReport(false);
            }
        }

        void OnDisable()
        {
            // A run stopped before its time still gets a report. Without this, closing the player
            // at minute nine would print nine status lines and no summary.
            if (!finished && startedAt > 0f && frameMs.Count > 0)
            {
                finished = true;
                LogReport(true);
            }

            if (disposed)
                return;

            disposed = true;

            if (drawRecorder.Valid) drawRecorder.Dispose();
            if (setPassRecorder.Valid) setPassRecorder.Dispose();
            if (batchRecorder.Valid) batchRecorder.Dispose();
            if (triangleRecorder.Valid) triangleRecorder.Dispose();
        }

        // ---------------------------------------------------------------------------------
        // Sampling
        // ---------------------------------------------------------------------------------

        void SampleMemory(float at)
        {
            memory.Add(new MemSample
            {
                at = at,
                gfx = Profiler.GetAllocatedMemoryForGraphicsDriver(),
                total = Profiler.GetTotalAllocatedMemoryLong(),
                mono = Profiler.GetMonoUsedSizeLong(),
            });
        }

        void ScanShadows()
        {
            // D35 asks for no shadows. Renderers are the visible half of that check; terrain
            // trees are not Renderers, so the active terrain's own mode is reported too.
            var renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            rendererCount = renderers.Length;

            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i].shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)
                    shadowCasters++;
            }
        }

        // ---------------------------------------------------------------------------------
        // Reporting
        // ---------------------------------------------------------------------------------

        void LogHeader()
        {
            var sb = new StringBuilder();

            sb.AppendLine("=== Dead Pines — performance probe ===");
            sb.AppendLine("  sampling      " + sampleSeconds.ToString("F0") + " s after a "
                          + warmupSeconds.ToString("F0") + " s warm-up");
            sb.AppendLine("  screen        " + Screen.width + " x " + Screen.height + ", "
                          + Screen.fullScreenMode);
            sb.AppendLine("  quality       " + QualityName()
                          + ", vSync " + QualitySettings.vSyncCount
                          + ", target " + Application.targetFrameRate + " fps");
            sb.AppendLine("  renderer      " + SystemInfo.graphicsDeviceType + ", "
                          + SystemInfo.graphicsDeviceName);
            sb.AppendLine("  device vram   " + SystemInfo.graphicsMemorySize + " MB");
            sb.AppendLine(CameraNote());
            sb.AppendLine("  shadows       " + QualitySettings.shadows + ", "
                          + shadowCasters + " of " + rendererCount + " renderer(s) casting"
                          + TerrainShadowNote());
            sb.AppendLine("  editor        " + Application.isEditor);
            Debug.Log(sb.ToString());
        }

        void LogStatus(float at)
        {
            var sb = new StringBuilder();

            sb.Append("[probe] ");
            sb.Append(Mathf.RoundToInt(at).ToString("000"));
            sb.Append(" s   frame ");
            sb.Append(frameMs.Mean.ToString("F1"));
            sb.Append(" ms (p95 ");
            sb.Append(frameMs.Percentile(95f).ToString("F1"));
            sb.Append(")");

            if (mainThreadMs.Count > 0)
            {
                sb.Append("   cpu ");
                sb.Append(mainThreadMs.Mean.ToString("F1"));
                sb.Append(" ms");
            }

            if (gpuMs.Count > 0)
            {
                sb.Append("   gpu ");
                sb.Append(gpuMs.Mean.ToString("F1"));
                sb.Append(" ms");
            }

            if (drawCalls.Count > 0)
            {
                sb.Append("   draw ");
                sb.Append(drawCalls.Mean.ToString("F0"));
            }

            if (setPassCalls.Count > 0)
            {
                sb.Append("   setpass ");
                sb.Append(setPassCalls.Mean.ToString("F0"));
            }

            if (memory.Count > 0)
            {
                sb.Append("   gfx ");
                sb.Append(Megabytes(memory[memory.Count - 1].gfx).ToString("F0"));
                sb.Append(" MB");
            }

            Debug.Log(sb.ToString());
        }

        void LogReport(bool partial)
        {
            var sb = new StringBuilder();

            sb.AppendLine();
            sb.AppendLine(partial
                ? "=== Dead Pines — performance report (partial, stopped early) ==="
                : "=== Dead Pines — performance report ===");

            sb.AppendLine("  frames        " + frameMs.Count + " sampled over "
                          + Mathf.Min(Time.realtimeSinceStartup - startedAt - warmupSeconds,
                                      sampleSeconds).ToString("F0") + " s");
            sb.AppendLine("  rendered      " + renderedFrames + " of " + frameMs.Count
                          + " frame(s) reported draw data"
                          + (frameMs.Count > 0
                              ? " (" + (100f * renderedFrames / frameMs.Count).ToString("F0") + "%)"
                              : ""));
            sb.AppendLine("  screen        " + Screen.width + " x " + Screen.height + ", "
                          + Screen.fullScreenMode);
            sb.AppendLine("  quality       " + QualityName()
                          + ", vSync " + QualitySettings.vSyncCount
                          + ", target " + Application.targetFrameRate + " fps");
            sb.AppendLine("  renderer      " + SystemInfo.graphicsDeviceType + ", "
                          + SystemInfo.graphicsDeviceName);
            sb.AppendLine("  device vram   " + SystemInfo.graphicsMemorySize + " MB");
            sb.AppendLine(CameraNote());
            sb.AppendLine("  shadows       " + QualitySettings.shadows + ", "
                          + shadowCasters + " of " + rendererCount + " renderer(s) casting"
                          + TerrainShadowNote());

            sb.AppendLine();
            sb.AppendLine("                mean      p50      p95      p99      max");
            Row(sb, "frame ms", frameMs);
            Row(sb, "cpu main", mainThreadMs);
            Row(sb, "cpu rndr", renderThreadMs);
            Row(sb, "gpu ms", gpuMs);

            sb.AppendLine();
            sb.AppendLine("                mean      p50      p95      p99      max");
            Row(sb, "draw calls", drawCalls);
            Row(sb, "set pass", setPassCalls);
            Row(sb, "batches", batches);
            Row(sb, "triangles", triangles);

            sb.AppendLine();
            sb.AppendLine("[memory]      gfx driver    total alloc     mono");
            MemoryRow(sb, "start", 0f);
            MemoryRow(sb, "1 min", 60f);

            // A three-minute run has no five-minute mark; the nearest sample would be the final
            // one, and the row would mislabel it.
            if (sampleSeconds >= 300f)
                MemoryRow(sb, "5 min", 300f);

            MemoryRow(sb, "end", sampleSeconds);

            if (gpuMs.Count == 0)
            {
                sb.AppendLine();
                sb.AppendLine("  gpu ms is n/a — the driver reported no GPU timing under "
                              + SystemInfo.graphicsDeviceType + ".");
            }
            else if (gpuMs.Count < frameMs.Count / 2)
            {
                sb.AppendLine();
                sb.AppendLine("  gpu ms covers only " + gpuMs.Count + " of " + frameMs.Count
                              + " frames — the driver did not return a timing every frame,");
                sb.AppendLine("  so treat the mean as a sample rather than a full picture.");
            }

            if (!Application.isEditor)
            {
                string folder = Path.GetDirectoryName(Application.dataPath);
                string path = folder == null
                    ? "PerformanceReport.txt"
                    : Path.Combine(folder, "PerformanceReport.txt");

                try
                {
                    File.WriteAllText(path, sb.ToString());
                    sb.AppendLine();
                    sb.AppendLine("  written to    " + path);
                }
                catch (Exception error)
                {
                    sb.AppendLine();
                    sb.AppendLine("  could not write " + path + ": " + error.Message);
                }
            }

            Debug.Log(sb.ToString());
        }

        static void Row(StringBuilder sb, string label, Stat stat)
        {
            if (stat.Count == 0)
            {
                sb.AppendLine("  " + label.PadRight(12) + "n/a");
                return;
            }

            sb.AppendLine("  " + label.PadRight(12)
                          + stat.Mean.ToString("F2").PadLeft(8)
                          + stat.Percentile(50f).ToString("F2").PadLeft(9)
                          + stat.Percentile(95f).ToString("F2").PadLeft(9)
                          + stat.Percentile(99f).ToString("F2").PadLeft(9)
                          + stat.Max.ToString("F2").PadLeft(9));
        }

        void MemoryRow(StringBuilder sb, string label, float at)
        {
            if (memory.Count == 0)
                return;

            MemSample best = memory[0];
            float bestDistance = Mathf.Abs(best.at - at);

            for (int i = 1; i < memory.Count; i++)
            {
                float distance = Mathf.Abs(memory[i].at - at);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = memory[i];
                }
            }

            sb.AppendLine("  " + label.PadRight(12)
                          + Megabytes(best.gfx).ToString("F0").PadLeft(9) + " MB"
                          + Megabytes(best.total).ToString("F0").PadLeft(12) + " MB"
                          + Megabytes(best.mono).ToString("F0").PadLeft(9) + " MB");
        }

        static float Megabytes(long bytes)
        {
            return bytes / (1024f * 1024f);
        }

        static string QualityName()
        {
            int level = QualitySettings.GetQualityLevel();

            return level >= 0 && level < QualitySettings.names.Length
                ? QualitySettings.names[level]
                : "level " + level;
        }

        static string CameraNote()
        {
            var camera = Camera.main;

            if (camera == null)
                return "  camera        no MainCamera";

            return "  camera        far clip " + camera.farClipPlane.ToString("F0") + " m"
                   + ", fov " + camera.fieldOfView.ToString("F0");
        }

        static string TerrainShadowNote()
        {
            var terrain = Terrain.activeTerrain;

            if (terrain == null)
                return "";

            return "; terrain " + terrain.shadowCastingMode;
        }
    }
}
