using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DeadPines.EditorTools
{
    /// <summary>
    /// Splits multi-step footstep recordings into one clip per step.
    ///
    /// A recording of ten footsteps is the right thing to download and the wrong thing to play:
    /// the footstep system fires on each real footfall, so it needs one clip per step. Splitting
    /// by hand in Audacity is ten exports per recording across three surfaces, and it is not
    /// reproducible — re-cut the file and you get slightly different slices.
    ///
    /// Onsets are found from a short-window RMS envelope with hysteresis, so a step is the moment
    /// the envelope crosses up through a threshold and stays up. The threshold is a fraction of
    /// the loudest window in the file rather than an absolute level, because these recordings come
    /// from different microphones at different gain and an absolute number would work on one and
    /// fail on the next.
    ///
    /// The raw recordings stay where they are. They are CC0, they compress well, and keeping them
    /// means the split can be re-derived — D33's argument, applied to audio: the source is the
    /// artefact and the slices are output.
    ///
    ///   Assets/Audio/CC0/Footsteps/Source/litter/*.wav
    ///     -> Assets/Audio/CC0/Footsteps/step_litter_01.wav, _02, _03 ...
    ///
    /// The surface is the folder name, so a new recording is dropped in and needs no naming.
    /// </summary>
    public static class SplitFootsteps
    {
        const string MenuPath = "Dead Pines/Split Footstep Recordings";

        const string Root = "Assets/Audio/CC0/Footsteps";
        const string SourceRoot = Root + "/Source";

        // Envelope window. 5 ms is short enough to catch the attack of a step (which is a few
        // milliseconds) and long enough that a single sample of noise cannot trip the threshold.
        const float WindowSeconds = 0.005f;

        // A step starts when the envelope crosses this fraction of the file's loudest window.
        // Low, because the quietest step in a take is the one worth keeping — a threshold tuned
        // to the loud ones silently drops it and the take comes back one short.
        const float OnsetFraction = 0.12f;

        // ...and the envelope has to fall back below this before another onset can be found, so a
        // step that rings does not register twice. The gap between the two is the hysteresis.
        const float ReleaseFraction = 0.04f;

        // Two onsets closer together than this are one footstep. Set from the recordings rather
        // than from the game: the first real run through the Nox_Sound takes found three pairs
        // 0.12-0.23 s apart against a background of ~1.0 s, which is a boot landing followed by
        // debris settling, not a second foot. 0.28 sits above those and below the 0.32 s floor of
        // the tightest genuine sequence in the same takes, so it separates them with margin on
        // both sides.
        const float MinGapSeconds = 0.28f;

        // How much of the run-up to keep. The transient's attack starts a hair before the
        // envelope notices it, and cutting exactly at the threshold clips the front off the step.
        const float PreRollSeconds = 0.012f;

        // Ceiling on a single step. Anything past this is decay or room, not footfall. The real
        // cut usually comes earlier — at the next onset, or where the envelope falls quiet.
        const float MaxStepSeconds = 0.60f;

        // Below this a "step" is a click or a mis-detection, and is reported rather than written.
        const float MinStepSeconds = 0.06f;

        // Silence kept after the envelope falls. Cuts the long dead tail that a loud take
        // otherwise carries into every footstep.
        const float TailSeconds = 0.04f;

        // Fades at both ends. Without them the slice boundaries are step discontinuities and
        // every footstep ends in a click.
        const float FadeSeconds = 0.002f;

        [MenuItem(MenuPath, priority = 51)]
        public static void Split()
        {
            var log = new StringBuilder();
            var problems = new List<string>();

            log.AppendLine("=== Dead Pines — split footstep recordings ===");

            if (!Directory.Exists(SourceRoot))
            {
                log.AppendLine();
                log.AppendLine($"  !! {SourceRoot} does not exist.");
                log.AppendLine("     Create it, then put each raw recording in a subfolder named after");
                log.AppendLine("     the surface it was recorded on:");
                log.AppendLine("       Source/litter/   Source/rock/   Source/mud/");
                Debug.LogWarning(log.ToString());
                return;
            }

            var surfaceDirs = Directory.GetDirectories(SourceRoot);
            if (surfaceDirs.Length == 0)
            {
                log.AppendLine();
                log.AppendLine($"  !! {SourceRoot} has no surface folders in it.");
                log.AppendLine("     Make one per surface — litter, rock, mud — and put the raw");
                log.AppendLine("     recordings inside. The folder name becomes the clip name.");
                Debug.LogWarning(log.ToString());
                return;
            }

            Directory.CreateDirectory(Root);

            int totalWritten = 0;

            foreach (var surfaceDir in surfaceDirs)
            {
                string surface = Path.GetFileName(surfaceDir);

                var sources = new List<string>(Directory.GetFiles(surfaceDir, "*.wav"));
                sources.Sort(System.StringComparer.OrdinalIgnoreCase);

                log.AppendLine();
                log.AppendLine($"[{surface}]  {sources.Count} recording(s)");

                if (sources.Count == 0)
                {
                    log.AppendLine("  (nothing to split)");
                    continue;
                }

                // Clear last run's clips for this surface. Without this, re-splitting after any
                // change to the detector leaves the tail of the previous, longer set behind —
                // 161 rock clips in, 120 out, and 41 files from the old run still sitting in the
                // folder looking exactly like the new ones. Deleting only the numbered files
                // means a surface and a surface with a suffix ("rock", "rock_walk") cannot
                // interfere, which the wildcard would otherwise allow.
                int stale = ClearPreviousSet(surface);
                if (stale > 0)
                    log.AppendLine($"  replaced {stale} clip(s) from the previous run");

                // Numbered across every recording in this surface, in filename order, rather than
                // restarting per file. Dropping a second take into the folder later extends the
                // set instead of overwriting the first take's clips.
                int number = 1;

                foreach (var source in sources)
                {
                    number = SplitOne(source, surface, number, log, problems, ref totalWritten);
                }
            }

            // Sweep before the refresh, so Unity never sees an orphan meta and never has to warn
            // about one. Doing it after would report every deleted clip first.
            int staleMetas = SweepOrphanMetas();

            AssetDatabase.Refresh();

            log.AppendLine();
            if (staleMetas > 0)
                log.AppendLine($"[Cleaned]  {staleMetas} stale .meta file(s) from the previous run");
            log.AppendLine($"[Written]  {totalWritten} clips into {Root}");

            if (problems.Count > 0)
            {
                log.AppendLine();
                log.AppendLine("--- PROBLEMS (paste these lines back to Claude) ---");
                foreach (var p in problems)
                    log.AppendLine("  !! " + p);
                Debug.LogWarning(log.ToString());
            }
            else
            {
                log.AppendLine();
                log.AppendLine("Check the onset list above for a gap that is much larger than the");
                log.AppendLine("rest — that is where the recordist stopped between takes, and it is");
                log.AppendLine("how we tell the walk steps from the run steps.");
                Debug.Log(log.ToString());
            }
        }

        /// <summary>
        /// Deletes the clips a previous run wrote for one surface, so the folder holds the current
        /// split and nothing else. Returns how many were removed.
        ///
        /// Only the .wav goes. Unity matches a .meta to its asset by filename, so leaving the meta
        /// means a clip that comes back under the same name keeps its GUID and anything already
        /// pointing at it keeps working; the metas that get left with no clip are cleared by
        /// SweepOrphanMetas before the refresh that would otherwise warn about each one.
        /// </summary>
        static int ClearPreviousSet(string surface)
        {
            string prefix = $"step_{surface}_";
            var existing = Directory.GetFiles(Root, prefix + "*.wav");
            int removed = 0;

            foreach (var path in existing)
            {
                // The wildcard's * matches "walk_07" as happily as "07", so check that what
                // follows the prefix really is this set's number and not another set's name.
                string tail = Path.GetFileNameWithoutExtension(path).Substring(prefix.Length);
                if (!IsAllDigits(tail))
                    continue;

                File.Delete(path);
                removed++;
            }

            return removed;
        }

        static bool IsAllDigits(string s)
        {
            if (s.Length == 0)
                return false;

            foreach (var c in s)
                if (c < '0' || c > '9')
                    return false;

            return true;
        }

        /// <summary>
        /// Removes .meta files whose .wav is gone, before the refresh that would report them.
        ///
        /// ClearPreviousSet deletes last run's clips with File.Delete — outside AssetDatabase,
        /// which leaves each meta behind. Unity then logs "a meta data file exists but its asset
        /// can't be found" once per deleted clip, and a re-split of a long take buries the actual
        /// report under those warnings. Sweeping first means the refresh never sees an orphan.
        ///
        /// Metas whose clip survived are left alone on purpose: Unity matches a meta to its asset
        /// by filename, so a clip rewritten under the same name keeps its GUID and anything that
        /// was already pointing at it — the scene's footstep pools, once Phase 6 has run — keeps
        /// working without being rewired.
        /// </summary>
        static int SweepOrphanMetas()
        {
            var metas = Directory.GetFiles(Root, "*.wav.meta");
            int removed = 0;

            foreach (var meta in metas)
            {
                string wav = meta.Substring(0, meta.Length - ".meta".Length);

                if (File.Exists(wav))
                    continue;

                File.Delete(meta);
                removed++;
            }

            return removed;
        }

        // ---------------------------------------------------------------------------------
        // One recording
        // ---------------------------------------------------------------------------------

        static int SplitOne(string sourcePath, string surface, int number,
                            StringBuilder log, List<string> problems, ref int totalWritten)
        {
            string name = Path.GetFileName(sourcePath);

            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(sourcePath);
            if (clip == null)
            {
                problems.Add($"{name} did not import as an AudioClip — is it a valid WAV?");
                return number;
            }

            // Make the clip's samples definitely resident. GetData works on a compressed clip in
            // the editor, but only if the data is loaded, and the default import settings do not
            // promise that. Setting it explicitly removes a failure that would otherwise show up
            // as an empty read on some files and not others.
            var importer = AssetImporter.GetAtPath(sourcePath) as AudioImporter;
            if (importer != null)
            {
                var settings = importer.defaultSampleSettings;
                if (settings.loadType != AudioClipLoadType.DecompressOnLoad)
                {
                    settings.loadType = AudioClipLoadType.DecompressOnLoad;

                    // On the settings struct, not on the importer. Unity 6 moved preload audio
                    // data to a per-platform local setting, and the old AudioImporter property is
                    // not merely deprecated — it is an error to touch it (CS0619), so the old
                    // form cannot be left in "just in case" either.
                    settings.preloadAudioData = true;

                    importer.defaultSampleSettings = settings;
                    importer.loadInBackground = false;
                    importer.SaveAndReimport();
                    clip = AssetDatabase.LoadAssetAtPath<AudioClip>(sourcePath);
                }
            }

            var samples = new float[clip.samples * clip.channels];
            if (!clip.GetData(samples, 0))
            {
                problems.Add($"{name}: AudioClip.GetData returned false — could not read samples");
                return number;
            }

            // Downmix. These should be mono anyway, but a stereo source silently read as if it
            // were mono would come back at double speed, which is a confusing way to find out.
            float[] mono;
            if (clip.channels == 1)
            {
                mono = samples;
            }
            else
            {
                mono = new float[clip.samples];
                for (int i = 0; i < clip.samples; i++)
                {
                    float sum = 0f;
                    for (int c = 0; c < clip.channels; c++)
                        sum += samples[i * clip.channels + c];
                    mono[i] = sum / clip.channels;
                }
            }

            int rate = clip.frequency;
            var steps = FindSteps(mono, rate);

            log.AppendLine();
            log.AppendLine($"  {name}");
            log.AppendLine($"    {clip.samples / (float)rate:F2} s, {rate} Hz, {clip.channels} ch" +
                           (clip.channels > 1 ? "  (downmixed to mono)" : ""));

            if (steps.Count == 0)
            {
                problems.Add($"{name}: no onsets found — the threshold may be wrong for this take");
                log.AppendLine("    !! no onsets found");
                return number;
            }

            log.AppendLine($"    {steps.Count} steps:");

            int ceiling = Mathf.RoundToInt(MaxStepSeconds * rate);
            int merged = 0;

            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                float at = step.x / (float)rate;
                float len = (step.y - step.x) / (float)rate;

                string gap = i + 1 < steps.Count
                    ? $"{(steps[i + 1].x - step.x) / (float)rate:F2} s to next"
                    : "last";

                // A slice that runs all the way to the ceiling means the envelope never fell far
                // enough between two footfalls for the detector to call them two. On a continuous
                // run or sprint sequence that is the expected failure, and it is worth saying
                // outright rather than leaving it to be inferred from a suspiciously round length.
                bool hitCeiling = step.y - step.x >= ceiling;
                if (hitCeiling)
                    merged++;

                // Concatenated rather than interpolated across lines: this project is C# 9, where
                // an interpolated string cannot span lines.
                log.AppendLine("      " + $"{i + 1,2}  at {at,6:F2} s   len {len:F2} s   {gap}"
                               + (hitCeiling ? "   <-- at the ceiling, likely two steps merged" : ""));
            }

            if (merged > 0)
            {
                log.AppendLine("    " + $"{merged} slice(s) ran to the {MaxStepSeconds} s ceiling.");
                log.AppendLine("    A continuous sequence needs either a tighter release threshold");
                log.AppendLine("    or a manual cut at the section boundary.");
            }

            string outName;
            for (int i = 0; i < steps.Count; i++)
            {
                outName = $"step_{surface}_{number:00}.wav";
                string outPath = $"{Root}/{outName}";

                WriteWav16(outPath, mono, steps[i].x, steps[i].y, rate);

                log.AppendLine($"      -> {outName}");

                number++;
                totalWritten++;
            }

            return number;
        }

        // ---------------------------------------------------------------------------------
        // Onset detection
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Finds each footstep and returns it as a (start, end) sample pair.
        ///
        /// Threshold-relative rather than absolute, and hysteretic rather than a single level:
        /// a threshold that a step has to cross downward again before the next one can be found
        /// is what stops a ringing step from being counted twice.
        /// </summary>
        static List<Vector2Int> FindSteps(float[] mono, int rate)
        {
            var steps = new List<Vector2Int>();

            int win = Mathf.Max(1, Mathf.RoundToInt(WindowSeconds * rate));
            int windows = mono.Length / win;
            if (windows < 3)
                return steps;

            var rms = new float[windows];
            float peak = 0f;

            for (int i = 0; i < windows; i++)
            {
                float sum = 0f;
                int start = i * win;

                for (int j = 0; j < win; j++)
                {
                    float s = mono[start + j];
                    sum += s * s;
                }

                rms[i] = Mathf.Sqrt(sum / win);
                if (rms[i] > peak)
                    peak = rms[i];
            }

            if (peak <= 0f)
                return steps;

            float onsetLevel = peak * OnsetFraction;
            float releaseLevel = peak * ReleaseFraction;

            int minGapWindows = Mathf.Max(1, Mathf.RoundToInt(MinGapSeconds * rate / (float)win));
            int preRoll = Mathf.RoundToInt(PreRollSeconds * rate);
            int maxStep = Mathf.RoundToInt(MaxStepSeconds * rate);
            int minStep = Mathf.RoundToInt(MinStepSeconds * rate);
            int tail = Mathf.RoundToInt((TailSeconds + WindowSeconds) * rate);
            int total = mono.Length;

            var onsets = new List<int>();
            bool armed = true;
            int lastOnsetWindow = -minGapWindows - 1;

            for (int i = 0; i < windows; i++)
            {
                if (armed && rms[i] >= onsetLevel && (i - lastOnsetWindow) >= minGapWindows)
                {
                    onsets.Add(i * win);
                    lastOnsetWindow = i;
                    armed = false;
                }
                else if (!armed && rms[i] < releaseLevel)
                {
                    armed = true;
                }
            }

            for (int i = 0; i < onsets.Count; i++)
            {
                int onset = onsets[i];
                int start = Mathf.Max(0, onset - preRoll);

                // The cut can come from three directions, whichever is nearest: the ceiling, the
                // start of the next step, or the end of the file.
                int end = Mathf.Min(onset + maxStep, total);
                if (i + 1 < onsets.Count)
                    end = Mathf.Min(end, onsets[i + 1] - preRoll);

                // ...and then back off to where the envelope went quiet, so a loud take does not
                // carry half a second of room tone into every footstep.
                int lastLoud = onset;
                for (int w = onset / win; w < windows && w * win < end; w++)
                    if (rms[w] >= releaseLevel)
                        lastLoud = w * win;

                end = Mathf.Min(end, lastLoud + tail);
                end = Mathf.Min(end, total);

                if (end - start < minStep)
                {
                    // Reported by omission: it never becomes a clip, and the count in the log will
                    // not match the onsets listed above it.
                    continue;
                }

                steps.Add(new Vector2Int(start, end));
            }

            return steps;
        }

        // ---------------------------------------------------------------------------------
        // WAV writing
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Writes 16-bit mono PCM.
        ///
        /// 16-bit rather than the source's 24: this is a footstep, it is one of six variations
        /// played at random, and the noise floor of the game's mix is nowhere near the 24th bit.
        /// Half the size, and the slices are what ship.
        /// </summary>
        static void WriteWav16(string path, float[] mono, int start, int end, int rate)
        {
            int count = end - start;
            int fade = Mathf.Min(Mathf.RoundToInt(FadeSeconds * rate), count / 2);

            var pcm = new byte[count * 2];

            for (int i = 0; i < count; i++)
            {
                float s = mono[start + i];

                if (i < fade)
                    s *= i / (float)fade;
                else if (i >= count - fade)
                    s *= (count - i) / (float)fade;

                int v = Mathf.RoundToInt(Mathf.Clamp(s, -1f, 1f) * 32767f);
                pcm[i * 2] = (byte)(v & 0xFF);
                pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }

            using (var stream = new FileStream(path, FileMode.Create))
            using (var w = new BinaryWriter(stream))
            {
                int dataBytes = pcm.Length;

                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + dataBytes);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));

                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);            // PCM header size
                w.Write((short)1);      // PCM
                w.Write((short)1);      // mono
                w.Write(rate);
                w.Write(rate * 2);      // byte rate = rate * channels * bytes per sample
                w.Write((short)2);      // block align
                w.Write((short)16);     // bits per sample

                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(dataBytes);
                w.Write(pcm);
            }
        }
    }
}
