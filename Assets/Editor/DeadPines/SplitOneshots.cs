using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DeadPines.EditorTools
{
    /// <summary>
    /// Splits a recording that holds several calls into one clip per call, and optionally into
    /// clips of several calls together.
    ///
    /// Deliberately a separate script from SplitFootsteps rather than a shared one with a
    /// parameter table. A footstep is an impact: it is over well inside a second and everything
    /// past that is decay, so that splitter caps a slice at 0.6 s. A bird call is a phrase with a
    /// ringing tail, and running one through those numbers truncates it at 0.6 s — silently, and
    /// in the direction that still sounds plausible, which is the worst way for a bug to fail.
    /// The two also disagree about what else to emit: consecutive calls pair up into something
    /// that reads as a bird rather than a sample, and footsteps never do. The duplication buys
    /// the certainty that editing one cannot change the other's output.
    ///
    ///   Assets/Audio/CC0/Oneshot/Source/crow/*.wav
    ///     -> Assets/Audio/CC0/Oneshot/crow_01.wav, _02, _03 ...
    ///     -> Assets/Audio/CC0/Oneshot/crow_pair_01.wav ...    (consecutive calls, pause and all)
    ///
    /// The category is the folder name, so a new recording is dropped in and needs no naming.
    /// The raw recordings stay where they are — they are CC0, they compress, and keeping them
    /// means the split can be re-derived. The source is the artefact; the slices are output.
    /// </summary>
    public static class SplitOneshots
    {
        const string MenuPath = "Dead Pines/Split One-shot Recordings";

        const string Root = "Assets/Audio/CC0/Oneshot";
        const string SourceRoot = Root + "/Source";

        // Envelope window. Short enough to catch the attack of a call — the attack is the part
        // that tells the ear where the bird is — and long enough that one noisy sample cannot
        // trip the threshold.
        const float WindowSeconds = 0.005f;

        // A call starts when the envelope crosses this fraction of the file's loudest window.
        // Low on purpose: in a take of four caws the quiet one is usually the distant one, and a
        // distant call is worth keeping. A threshold tuned to the loud ones drops it and the file
        // comes back one short, with nothing to say that it did.
        const float OnsetFraction = 0.12f;

        // ...and the envelope has to fall back below this before another onset can be found, so a
        // call that rings does not register twice. The distance between the two is the hysteresis.
        const float ReleaseFraction = 0.04f;

        // Two onsets closer together than this are one call. Slightly wider than the footstep
        // splitter's: a crow's "caw-caw" is one call, not two, and birds are slower than feet.
        const float MinGapSeconds = 0.30f;

        // Kept before the onset. Longer than the footstep version on purpose — a call's attack is
        // the whole cue, and cutting it flush removes the transient that places it in space.
        const float PreRollSeconds = 0.020f;

        // Ceiling on one call, against the footstep splitter's 0.60. A crow call with its tail is
        // a phrase. Anything that reaches this is reported rather than silently written.
        const float MaxCueSeconds = 4.0f;

        const float MinCueSeconds = 0.06f;

        // Silence kept after the envelope falls. Shorter than the footstep version: a one-shot is
        // placed in space, and whatever room tone rides along with it gets placed there too.
        const float TailSeconds = 0.10f;

        const float FadeSeconds = 0.002f;

        // Consecutive calls are emitted N at a time as well as singly. Two caws with the natural
        // pause between them read as a bird; one caw on its own reads as a sample. Set to 1 to
        // emit singles only, which is what a long file of many calls would want.
        const int PairSize = 2;

        [MenuItem(MenuPath, priority = 52)]
        public static void Split()
        {
            var log = new StringBuilder();
            var problems = new List<string>();

            log.AppendLine("=== Dead Pines — split one-shot recordings ===");

            if (!Directory.Exists(SourceRoot))
            {
                log.AppendLine();
                log.AppendLine($"  !! {SourceRoot} does not exist.");
                log.AppendLine("     Create it, then put each raw recording in a subfolder named");
                log.AppendLine("     after what it is:");
                log.AppendLine("       Source/crow/   Source/woodpecker/   Source/wingbeat/");
                Debug.LogWarning(log.ToString());
                return;
            }

            // A recording loose in Source/ has no category, and the category is the only thing
            // naming its clips. Say which files they are rather than just that some exist.
            var stray = Directory.GetFiles(SourceRoot, "*.wav");
            if (stray.Length > 0)
            {
                log.AppendLine();
                log.AppendLine($"  !! {stray.Length} recording(s) sitting directly in {SourceRoot}/");
                log.AppendLine("     with no folder around them. The folder name is what the clips");
                log.AppendLine("     get named after, so there is nothing to call these:");
                foreach (var s in stray)
                    log.AppendLine("       " + Path.GetFileName(s));
                log.AppendLine("     Move each one into a folder named after the sound:");
                log.AppendLine("       Source/crow/   Source/woodpecker/   Source/wingbeat/");
            }

            var categoryDirs = Directory.GetDirectories(SourceRoot);
            if (categoryDirs.Length == 0)
            {
                log.AppendLine();
                log.AppendLine($"  !! {SourceRoot} has no category folders in it.");
                if (stray.Length == 0)
                {
                    log.AppendLine("     Make one per sound — crow, woodpecker, wingbeat — and");
                    log.AppendLine("     put the raw recordings inside. The folder name becomes");
                    log.AppendLine("     the clip name.");
                }
                Debug.LogWarning(log.ToString());
                return;
            }

            Directory.CreateDirectory(Root);

            int written = 0;

            foreach (var categoryDir in categoryDirs)
            {
                string category = Path.GetFileName(categoryDir);

                var sources = new List<string>(Directory.GetFiles(categoryDir, "*.wav"));
                sources.Sort(System.StringComparer.OrdinalIgnoreCase);

                log.AppendLine();
                log.AppendLine($"[{category}]  {sources.Count} recording(s)");

                if (sources.Count == 0)
                {
                    log.AppendLine("  (nothing to split)");
                    continue;
                }

                // Clear last run's clips for this category, so re-splitting after any change to
                // the detector leaves the current set and nothing else. Without it the tail of a
                // previous, longer run stays behind looking exactly like the new clips.
                int stale = ClearPreviousSet(category);
                if (stale > 0)
                    log.AppendLine($"  replaced {stale} clip(s) from the previous run");

                // Numbered across every recording in this category, in filename order, so a second
                // take dropped in later extends the set instead of overwriting the first's clips.
                int number = 1;
                int pairNumber = 1;

                foreach (var source in sources)
                    written += SplitOne(source, category, ref number, ref pairNumber, log, problems);
            }

            // Sweep before the refresh, so Unity never sees an orphan meta and never has to warn
            // about one. Doing it after would report every deleted clip first.
            int staleMetas = SweepOrphanMetas();

            AssetDatabase.Refresh();

            log.AppendLine();
            if (staleMetas > 0)
                log.AppendLine($"[Cleaned]  {staleMetas} stale .meta file(s) from the previous run");
            log.AppendLine($"[Written]  {written} clips into {Root}");

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
                log.AppendLine("Check the call list above: every call should be listed, the level");
                log.AppendLine("column should not have one entry far below the rest, and no call");
                log.AppendLine("should be at the ceiling.");
                Debug.Log(log.ToString());
            }
        }

        // ---------------------------------------------------------------------------------
        // One recording
        // ---------------------------------------------------------------------------------

        static int SplitOne(string sourcePath, string category,
                            ref int number, ref int pairNumber,
                            StringBuilder log, List<string> problems)
        {
            int written = 0;
            string name = Path.GetFileName(sourcePath);

            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(sourcePath);
            if (clip == null)
            {
                problems.Add($"{name} did not import as an AudioClip — is it a valid WAV?");
                return written;
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
                    // not merely deprecated — it is an error to touch it (CS0619).
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
                return written;
            }

            // Downmix. A stereo call cannot be placed in space — Unity folds it flat to 2D — and
            // these are all positional, so mono is the only usable form. Doing it here rather
            // than at import means the slice on disk is what the game plays.
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
            var cues = FindCues(mono, rate);

            log.AppendLine();
            log.AppendLine($"  {name}");
            log.AppendLine($"    {clip.samples / (float)rate:F2} s, {rate} Hz, {clip.channels} ch"
                           + (clip.channels > 1 ? "  (downmixed to mono)" : ""));

            if (cues.Count == 0)
            {
                problems.Add($"{name}: no onsets found — the threshold may be wrong for this take");
                log.AppendLine("    !! no onsets found");
                return written;
            }

            float filePeak = 0f;
            for (int i = 0; i < mono.Length; i++)
            {
                float a = mono[i] < 0f ? -mono[i] : mono[i];
                if (a > filePeak)
                    filePeak = a;
            }

            log.AppendLine($"    {cues.Count} call(s):");

            int ceiling = Mathf.RoundToInt(MaxCueSeconds * rate);

            for (int i = 0; i < cues.Count; i++)
            {
                float at = cues[i].x / (float)rate;
                float len = (cues[i].y - cues[i].x) / (float)rate;

                string gap = i + 1 < cues.Count
                    ? $"{(cues[i + 1].x - cues[i].x) / (float)rate:F2} s to next"
                    : "last";

                // Level against the loudest call in the take. A call far below the rest is the
                // one the threshold is about to lose, and seeing it here is the difference
                // between noticing and finding out three phases later that a crow went missing.
                float cuePeak = 0f;
                for (int s = cues[i].x; s < cues[i].y; s++)
                {
                    float a = mono[s] < 0f ? -mono[s] : mono[s];
                    if (a > cuePeak)
                        cuePeak = a;
                }
                int lvl = filePeak > 0f ? Mathf.RoundToInt(cuePeak / filePeak * 100f) : 0;

                // A slice that runs to the ceiling means the envelope never fell far enough for
                // the detector to call the call finished. Worth saying outright rather than
                // leaving it to be inferred from a suspiciously round length.
                bool hitCeiling = cues[i].y - cues[i].x >= ceiling;

                // Concatenated rather than interpolated across lines: this project is C# 9, where
                // an interpolated string cannot span lines.
                log.AppendLine("      " + $"{i + 1,2}  at {at,6:F2} s   len {len:F2} s   lvl {lvl,3}%   {gap}"
                               + (hitCeiling ? "   <-- at the ceiling, possibly cut short" : ""));
            }

            // Singles.
            for (int i = 0; i < cues.Count; i++)
            {
                string outName = $"{category}_{number:00}.wav";
                WriteWav16($"{Root}/{outName}", mono, cues[i].x, cues[i].y, rate);
                log.AppendLine($"      -> {outName}");
                number++;
                written++;
            }

            // Groups of consecutive calls, kept as one cue with the pause between them intact.
            // The pause is the point: it is what makes two caws read as a bird rather than as a
            // sound effect fired twice.
            if (PairSize > 1 && cues.Count >= PairSize)
            {
                int groups = cues.Count / PairSize;

                for (int g = 0; g < groups; g++)
                {
                    int first = g * PairSize;
                    int start = cues[first].x;
                    int end = cues[first + PairSize - 1].y;

                    string outName = $"{category}_pair_{pairNumber:00}.wav";
                    WriteWav16($"{Root}/{outName}", mono, start, end, rate);

                    float total = (end - start) / (float)rate;

                    // Every gap inside the group, not just the first — with PairSize at 2 they
                    // are the same number, and if it is ever raised this stays right.
                    float air = 0f;
                    for (int k = first; k < first + PairSize - 1; k++)
                        air += (cues[k + 1].x - cues[k].y) / (float)rate;

                    log.AppendLine("      -> " + outName
                                   + $"   ({PairSize} calls, {total:F2} s, {air:F2} s of air)");
                    pairNumber++;
                    written++;
                }

                // A group needs a full set of calls. Worth saying, because the count in the
                // folder will not match the number of calls listed above it.
                int leftover = cues.Count % PairSize;
                if (leftover > 0)
                    log.AppendLine("    " + $"{leftover} call(s) past the last full group, left unpaired");
            }

            return written;
        }

        // ---------------------------------------------------------------------------------
        // Onset detection
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Finds each call and returns it as a (start, end) sample pair.
        ///
        /// Threshold-relative rather than absolute, and hysteretic rather than a single level:
        /// a threshold a call has to cross downward again before the next one can be found is
        /// what stops a ringing call from being counted twice.
        /// </summary>
        static List<Vector2Int> FindCues(float[] mono, int rate)
        {
            var cues = new List<Vector2Int>();

            int win = Mathf.Max(1, Mathf.RoundToInt(WindowSeconds * rate));
            int windows = mono.Length / win;
            if (windows < 3)
                return cues;

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
                return cues;

            float onsetLevel = peak * OnsetFraction;
            float releaseLevel = peak * ReleaseFraction;

            int minGapWindows = Mathf.Max(1, Mathf.RoundToInt(MinGapSeconds * rate / (float)win));
            int preRoll = Mathf.RoundToInt(PreRollSeconds * rate);
            int maxCue = Mathf.RoundToInt(MaxCueSeconds * rate);
            int minCue = Mathf.RoundToInt(MinCueSeconds * rate);
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
                // start of the next call, or the end of the file.
                int end = Mathf.Min(onset + maxCue, total);
                if (i + 1 < onsets.Count)
                    end = Mathf.Min(end, onsets[i + 1] - preRoll);

                // ...and then back off to where the envelope went quiet, so a take does not carry
                // a second of forest into every call.
                int lastLoud = onset;
                for (int w = onset / win; w < windows && w * win < end; w++)
                    if (rms[w] >= releaseLevel)
                        lastLoud = w * win;

                end = Mathf.Min(end, lastLoud + tail);
                end = Mathf.Min(end, total);

                if (end - start < minCue)
                {
                    // Reported by omission: it never becomes a clip, and the count in the log will
                    // not match the onsets listed above it.
                    continue;
                }

                cues.Add(new Vector2Int(start, end));
            }

            return cues;
        }

        // ---------------------------------------------------------------------------------
        // Housekeeping
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Deletes the clips a previous run wrote for one category, so the folder holds the
        /// current split and nothing else. Returns how many were removed.
        ///
        /// Only the .wav goes. Unity matches a .meta to its asset by filename, so leaving the meta
        /// means a clip that comes back under the same name keeps its GUID and anything already
        /// pointing at it keeps working; the metas that get left with no clip are cleared by
        /// SweepOrphanMetas before the refresh that would otherwise warn about each one.
        /// </summary>
        static int ClearPreviousSet(string category)
        {
            return DeleteNumbered(category + "_") + DeleteNumbered(category + "_pair_");
        }

        static int DeleteNumbered(string prefix)
        {
            var existing = Directory.GetFiles(Root, prefix + "*.wav");
            int removed = 0;

            foreach (var path in existing)
            {
                // The wildcard's * matches "pair_02" as happily as "02", so check that what
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
        /// can't be found" once per deleted clip, and a re-split of a long recording buries the
        /// call list under those warnings. Sweeping first means the refresh never sees an orphan.
        ///
        /// Metas whose clip survived are left alone on purpose: Unity matches a meta to its asset
        /// by filename, so a clip rewritten under the same name keeps its GUID and anything that
        /// was already pointing at it keeps working without being rewired.
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
        // WAV writing
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Writes 16-bit mono PCM.
        ///
        /// 16-bit rather than the source's 24: a call is one of several variations played at
        /// random, and the noise floor of the game's mix is nowhere near the 24th bit. Half the
        /// size, and the slices are what ship.
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
