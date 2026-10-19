using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DeadPines.EditorTools
{
    /// <summary>
    /// Turns the wind recording into a loop that has no seam.
    ///
    /// The source is a 48 s field-style recording, not a designed loop, so its last sample has no
    /// relationship to its first. Played on repeat it clicks once a minute, at a level that is
    /// exactly as audible as the quiet it interrupts.
    ///
    /// The fix is a crossfade: the last three seconds are mixed into the first three, and the file
    /// is cut three seconds shorter. The joint then falls between two samples that were adjacent
    /// in the original, so there is nothing to hear there; the fade hides the fact that the
    /// material either side of it is different.
    ///
    /// Equal-power rather than linear. Wind is uncorrelated noise, and a linear crossfade of two
    /// noise passages dips in the middle — an audible hole in the bed once a minute, which is the
    /// same defect as the click in a different disguise.
    ///
    /// The source is kept. As with the splitters it is the artefact and this is derived output:
    /// change the crossfade and re-run rather than editing the loop by hand.
    /// </summary>
    public static class MakeWindLoop
    {
        const string MenuPath = "Dead Pines/Make the Wind Loop";

        const string SourcePath = "Assets/Audio/CC0/Ambience/wind_desert_01.wav";
        const string OutputPath = "Assets/Audio/CC0/Ambience/wind_desert_loop_01.wav";

        // Long enough to cover a slow swell in the recording, short enough that losing three
        // seconds from forty-eight changes nothing about the bed.
        const float FadeSeconds = 3f;

        [MenuItem(MenuPath, priority = 53)]
        public static void Build()
        {
            var log = new StringBuilder();

            log.AppendLine("=== Dead Pines — wind loop ===");

            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(SourcePath);
            if (clip == null)
            {
                log.AppendLine();
                log.AppendLine("  !! " + SourcePath + " did not load as an AudioClip.");
                Debug.LogWarning(log.ToString());
                return;
            }

            string importerProblem = MakeResident(SourcePath);
            if (importerProblem != null)
            {
                log.AppendLine();
                log.AppendLine("  !! " + importerProblem);
                Debug.LogWarning(log.ToString());
                return;
            }

            // Reloaded after the reimport: the reference above may point at a clip whose data
            // has since been replaced.
            clip = AssetDatabase.LoadAssetAtPath<AudioClip>(SourcePath);

            int channels = clip.channels;
            int rate = clip.frequency;
            int total = clip.samples;

            if (total < rate)
            {
                log.AppendLine();
                log.AppendLine("  !! the source is only " + (total / (float)rate).ToString("F2") + " s long.");
                log.AppendLine("     Too short to crossfade into a loop.");
                Debug.LogWarning(log.ToString());
                return;
            }

            var samples = new float[total * channels];
            if (!clip.GetData(samples, 0))
            {
                log.AppendLine();
                log.AppendLine("  !! GetData returned false — the samples cannot be read.");
                Debug.LogWarning(log.ToString());
                return;
            }

            int fade = Mathf.Clamp(Mathf.RoundToInt(FadeSeconds * rate), 2, total / 2);
            int frames = total - fade;

            var loop = new float[frames * channels];

            for (int i = 0; i < frames; i++)
            {
                if (i < fade)
                {
                    // sin/cos rather than a straight line: the two passages are uncorrelated
                    // noise, so their powers add and a linear fade dips halfway through.
                    float t = i / (float)(fade - 1);
                    float head = Mathf.Sin(t * Mathf.PI * 0.5f);
                    float tail = Mathf.Cos(t * Mathf.PI * 0.5f);

                    for (int c = 0; c < channels; c++)
                    {
                        loop[i * channels + c] =
                            samples[i * channels + c] * head +
                            samples[(frames + i) * channels + c] * tail;
                    }
                }
                else
                {
                    for (int c = 0; c < channels; c++)
                        loop[i * channels + c] = samples[i * channels + c];
                }
            }

            float peak = 0f;
            double sumSquares = 0d;

            for (int i = 0; i < loop.Length; i++)
            {
                float a = loop[i] < 0f ? -loop[i] : loop[i];
                if (a > peak)
                    peak = a;

                sumSquares += loop[i] * (double)loop[i];
            }

            float rms = Mathf.Sqrt((float)(sumSquares / loop.Length));

            WriteWav16(OutputPath, loop, channels, rate);
            AssetDatabase.Refresh();
            ImportLoop();

            log.AppendLine();
            log.AppendLine("  source     " + Path.GetFileNameWithoutExtension(SourcePath).PadRight(24)
                           + (total / (float)rate).ToString("F2").PadLeft(6) + " s   "
                           + rate + " Hz   " + channels + " ch");
            log.AppendLine("  crossfade  " + FadeSeconds.ToString("F2") + " s, equal-power");
            log.AppendLine("  loop       " + Path.GetFileNameWithoutExtension(OutputPath).PadRight(24)
                           + (frames / (float)rate).ToString("F2").PadLeft(6) + " s");
            log.AppendLine("  peak       " + Mathf.RoundToInt(peak * 100f) + "%");
            log.AppendLine("  rms        " + Mathf.RoundToInt(rms * 100f) + "%");
            log.AppendLine();
            log.AppendLine("Written as PCM. A compressed loop can carry encoder padding into the");
            log.AppendLine("joint, which is the click this file exists to remove.");
            Debug.Log(log.ToString());
        }

        /// <summary>
        /// Makes sure the clip's samples are resident, so GetData can read them. Returns null on
        /// success, or a message on failure.
        ///
        /// The same dance the splitters do: GetData works on a compressed clip in the editor, but
        /// only while the data is loaded, and the default import settings do not promise that.
        /// </summary>
        static string MakeResident(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as AudioImporter;
            if (importer == null)
                return path + ": no AudioImporter — is it imported as audio?";

            var settings = importer.defaultSampleSettings;
            if (settings.loadType == AudioClipLoadType.DecompressOnLoad && settings.preloadAudioData)
                return null;

            settings.loadType = AudioClipLoadType.DecompressOnLoad;
            settings.preloadAudioData = true;

            importer.defaultSampleSettings = settings;
            importer.loadInBackground = false;
            importer.SaveAndReimport();

            return null;
        }

        /// <summary>
        /// PCM, deliberately, and set here rather than left to the project's default. A
        /// Vorbis-compressed loop can carry encoder padding into the joint, which is the click
        /// this file exists to remove. Forty-five seconds of stereo 16-bit is about 8.6 MB —
        /// small next to the artefact it prevents.
        /// </summary>
        static void ImportLoop()
        {
            var importer = AssetImporter.GetAtPath(OutputPath) as AudioImporter;
            if (importer == null)
                return;

            var settings = importer.defaultSampleSettings;

            settings.compressionFormat = AudioCompressionFormat.PCM;
            settings.loadType = AudioClipLoadType.DecompressOnLoad;
            settings.preloadAudioData = true;

            importer.defaultSampleSettings = settings;
            importer.loadInBackground = false;
            importer.SaveAndReimport();
        }

        /// <summary>
        /// Writes interleaved 16-bit PCM.
        ///
        /// No fades at the ends: the crossfade has already made the joint continuous, and fading
        /// the loop's edges would dip the bed once a minute — exactly the artefact it exists to
        /// remove.
        /// </summary>
        static void WriteWav16(string path, float[] interleaved, int channels, int rate)
        {
            int frames = interleaved.Length / channels;
            var pcm = new byte[interleaved.Length * 2];

            for (int i = 0; i < frames; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    int j = i * channels + c;
                    int v = Mathf.RoundToInt(Mathf.Clamp(interleaved[j], -1f, 1f) * 32767f);

                    pcm[j * 2] = (byte)(v & 0xFF);
                    pcm[j * 2 + 1] = (byte)((v >> 8) & 0xFF);
                }
            }

            int blockAlign = channels * 2;

            using (var stream = new FileStream(path, FileMode.Create))
            using (var w = new BinaryWriter(stream))
            {
                int dataBytes = pcm.Length;

                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + dataBytes);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));

                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);                // PCM header size
                w.Write((short)1);          // PCM
                w.Write((short)channels);
                w.Write(rate);
                w.Write(rate * blockAlign);
                w.Write((short)blockAlign);
                w.Write((short)16);         // bits per sample

                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(dataBytes);
                w.Write(pcm);
            }
        }
    }
}
