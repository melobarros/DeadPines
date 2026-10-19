using System.Collections.Generic;
using System.Text;
using DeadPines.Audio;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeadPines.EditorTools
{
    /// <summary>
    /// Phase 7 of M0 — the soundscape D25 asks for: wind under birds.
    ///
    /// Two pieces, because they solve opposite problems. Wind is a bed: one looping 2D source the
    /// player should never be able to locate. Birds are the reverse — each call is mono, 3D, and
    /// comes from somewhere different, so the forest keeps sounding larger than what is visible
    /// through the fog.
    ///
    /// The wind is wired from the crossfaded loop MakeWindLoop writes, not from the raw
    /// recording. The source is 48 s of field recording whose end does not meet its beginning,
    /// and looping that directly clicks once a minute.
    ///
    /// Idempotent: re-running re-wires the clips, re-applies the wind's designed levels, and
    /// reports what it found. The bird settings are not written, so anything tuned by ear on
    /// BirdAudio survives.
    /// </summary>
    public static class Phase7Soundscape
    {
        const string MenuPath = "Dead Pines/Phase 7 — Wire the Soundscape";

        const string ScenePath = "Assets/Scenes/M0.unity";
        const string LoopPath = "Assets/Audio/CC0/Ambience/wind_desert_loop_01.wav";
        const string OneshotRoot = "Assets/Audio/CC0/Oneshot";
        const string RootName = "M0_Soundscape";

        [MenuItem(MenuPath, priority = 70)]
        public static void Build()
        {
            var log = new StringBuilder();
            var problems = new List<string>();

            log.AppendLine("=== Dead Pines — Phase 7, the soundscape ===");

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.Log("[Dead Pines] Phase 7 cancelled — the open scene was not saved.");
                return;
            }

            if (!System.IO.File.Exists(ScenePath))
            {
                log.AppendLine();
                log.AppendLine("  !! " + ScenePath + " does not exist — run Phase 1 first.");
                Debug.LogWarning(log.ToString());
                return;
            }

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var wind = AssetDatabase.LoadAssetAtPath<AudioClip>(LoopPath);
            if (wind == null)
                problems.Add(LoopPath + " is missing — run 'Dead Pines/Make the Wind Loop' first");

            int singles;
            int pairs;
            var calls = LoadCalls(problems, out singles, out pairs);

            log.AppendLine();
            log.AppendLine("[Clips]");
            log.AppendLine("  wind loop   " + (wind != null ? LoopPath : "MISSING")
                           + (wind != null ? "   " + wind.length.ToString("F2") + " s" : ""));
            log.AppendLine("  bird calls  " + calls.Count
                           + "   (" + singles + " single, " + pairs + " pair)");

            var root = FindAnywhere(scene, RootName);
            if (root == null)
                root = new GameObject(RootName);

            // WindAudio requires an AudioSource, so adding it brings one along.
            var windAudio = root.GetComponent<WindAudio>();
            if (windAudio == null)
                windAudio = root.AddComponent<WindAudio>();

            var source = root.GetComponent<AudioSource>();
            if (source == null)
            {
                source = root.AddComponent<AudioSource>();
                problems.Add("WindAudio came without its AudioSource — one was added");
            }

            source.clip = wind;
            source.loop = true;
            source.playOnAwake = true;
            source.spatialBlend = 0f;
            source.volume = 0.18f;

            // The wind's level knobs, written every run rather than only on the first. They are
            // part of the designed soundscape, not a per-scene preference: 0.30 was too loud, and
            // the 37 s drift breathed at nearly the same rate as the 45 s loop. The bird settings
            // are deliberately not written here, so anything tuned by ear on BirdAudio survives a
            // re-run.
            var windSo = new SerializedObject(windAudio);
            WriteFloat(windSo, "baseVolume", 0.18f, problems);
            WriteFloat(windSo, "drift", 0.20f, problems);
            WriteFloat(windSo, "period", 110f, problems);
            windSo.ApplyModifiedPropertiesWithoutUndo();

            var birds = root.GetComponent<BirdAudio>();
            if (birds == null)
                birds = root.AddComponent<BirdAudio>();

            var so = new SerializedObject(birds);
            var callsProp = so.FindProperty("calls");

            if (callsProp == null)
            {
                problems.Add("BirdAudio has no 'calls' field — was it renamed?");
            }
            else
            {
                callsProp.arraySize = calls.Count;

                for (int i = 0; i < calls.Count; i++)
                    callsProp.GetArrayElementAtIndex(i).objectReferenceValue = calls[i];

                so.ApplyModifiedPropertiesWithoutUndo();
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();

            Verify(root, log);

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
                log.AppendLine("--- soundscape wired ---");
                log.AppendLine();
                log.AppendLine("WHAT TO DO:");
                log.AppendLine("  1. Press Play. The wind should be there before you move.");
                log.AppendLine("  2. Stand still for two minutes. The 45 s bed must not click or");
                log.AppendLine("     audibly repeat. That is what the crossfade is for.");
                log.AppendLine("  3. Turn on the spot. The wind must not swing: it is 2D, so it should");
                log.AppendLine("     sit still while you rotate. If it moves, something is wrong.");
                log.AppendLine("  4. Wait for crows — one every 9 to 30 s. Turn towards a call: it");
                log.AppendLine("     should hold its place in the world as you turn.");
                log.AppendLine("  5. Tell me: the wind level, the spacing of the calls, and whether");
                log.AppendLine("     the calls sound placed or flat.");
                Debug.Log(log.ToString());
            }
        }

        // ---------------------------------------------------------------------------------
        // Clips
        // ---------------------------------------------------------------------------------

        static List<AudioClip> LoadCalls(List<string> problems, out int singles, out int pairs)
        {
            var clips = new List<AudioClip>();

            // Singles first, then pairs, so the two kinds sit in a known order in the array. Name
            // order would interleave them, and the pairs — two caws with the real pause between
            // them — are the more convincing call, not a variant to bury.
            singles = AddNumbered(clips, "crow");
            pairs = AddNumbered(clips, "crow_pair");

            if (clips.Count == 0)
                problems.Add("no crow clips in " + OneshotRoot
                             + " — run 'Dead Pines/Split One-shot Recordings'");

            return clips;
        }

        static int AddNumbered(List<AudioClip> clips, string prefix)
        {
            int added = 0;

            for (int i = 1; i <= 99; i++)
            {
                string path = OneshotRoot + "/" + prefix + "_" + i.ToString("00") + ".wav";
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);

                if (clip == null)
                    break;

                clips.Add(clip);
                added++;
            }

            return added;
        }

        /// <summary>
        /// Writes one serialized float, reporting a renamed field rather than throwing. A silent
        /// failure here would leave the wind at whatever the scene last held, which is exactly the
        /// bug this pass exists to correct.
        /// </summary>
        static void WriteFloat(SerializedObject so, string field, float value, List<string> problems)
        {
            var prop = so.FindProperty(field);

            if (prop == null)
            {
                problems.Add("WindAudio has no '" + field + "' field — was it renamed?");
                return;
            }

            prop.floatValue = value;
        }

        // ---------------------------------------------------------------------------------
        // Verification
        // ---------------------------------------------------------------------------------

        static void Verify(GameObject root, StringBuilder log)
        {
            log.AppendLine();
            log.AppendLine("[Read back]");

            var source = root.GetComponent<AudioSource>();
            var wind = root.GetComponent<WindAudio>();
            var birds = root.GetComponent<BirdAudio>();

            log.AppendLine("  root          " + root.name);
            log.AppendLine("  wind          " + (wind != null ? "ok" : "MISSING"));
            log.AppendLine("  birds         " + (birds != null ? "ok" : "MISSING"));

            if (source != null)
            {
                log.AppendLine("  wind source   " + (source.clip != null ? source.clip.name : "NO CLIP")
                               + ", loop " + source.loop
                               + ", spatialBlend " + source.spatialBlend
                               + ", playOnAwake " + source.playOnAwake);
            }

            if (wind != null)
            {
                // Read back the fields the pass just wrote: a renamed field reports as a problem
                // above, but a value that silently did not change would only show up here.
                var windSo = new SerializedObject(wind);
                var baseVolume = windSo.FindProperty("baseVolume");
                var drift = windSo.FindProperty("drift");
                var period = windSo.FindProperty("period");

                if (baseVolume != null && drift != null && period != null)
                {
                    log.AppendLine("  wind levels   baseVolume " + baseVolume.floatValue
                                   + ", drift " + drift.floatValue
                                   + ", period " + period.floatValue + " s");
                }
            }

            if (birds != null)
            {
                var so = new SerializedObject(birds);
                var calls = so.FindProperty("calls");

                log.AppendLine("  bird calls    " + (calls != null ? calls.arraySize : 0));
            }

            log.AppendLine();
            log.AppendLine("  wind is 2D — it should be inaudible from the mouse and unplaceable");
            log.AppendLine("  birds are 3D mono — every call should come from a direction");
        }

        static GameObject FindAnywhere(UnityEngine.SceneManagement.Scene scene, string name)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == name)
                    return root;

                foreach (var child in root.GetComponentsInChildren<Transform>(true))
                    if (child.name == name)
                        return child.gameObject;
            }

            return null;
        }
    }
}
