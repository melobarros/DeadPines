using System.Collections.Generic;
using System.Text;
using DeadPines.Audio;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeadPines.EditorTools
{
    /// <summary>
    /// Phase 6 of M0 — footsteps on the surfaces underfoot.
    ///
    /// ROADMAP.md puts sound first: "Pillar 1 is sound, so build sound first and build it well",
    /// and M0's test is "footsteps synced to actual steps on 2-3 ground surfaces". This is the
    /// footsteps half. Wind and birds follow once these are confirmed by ear.
    ///
    /// The clips come from SplitFootsteps' output, not from the raw recordings. The takes hold
    /// several gaits in one file, so which numbered clips are walk and which are run is read off
    /// the splitter's own report and pinned below. That is deliberate: a wrong range here is a
    /// sprint footfall in a slow walk, which is audible instantly, and pinning it means the
    /// splitter can be re-run and checked against a number rather than against a guess.
    ///
    /// Idempotent: re-running re-applies the wiring and reports what it found.
    /// </summary>
    public static class Phase6Audio
    {
        const string MenuPath = "Dead Pines/Phase 6 — Wire the Footsteps";

        const string ScenePath = "Assets/Scenes/M0.unity";
        const string FootstepRoot = "Assets/Audio/CC0/Footsteps";

        const string PlayerName = "M0_Player";
        const string TerrainName = "M0_Terrain";

        // Phase 2's splatmap layer order. The component is wired one entry per layer in this
        // order, so the array index and the terrain layer index are the same number.
        static readonly string[] LayerLabels =
        {
            "forest floor",     // 0 — forest_ground_04
            "leaf litter",      // 1 — forest_leaves_04
            "mud",              // 2 — brown_mud_02
            "moss and rock",    // 3 — mossy_rock
        };

        // Which numbered clips in a surface's set are which gait, inclusive, 1-based.
        //
        // Read off the splitter's report rather than guessed, and these numbers replace a first
        // pass that got two of them wrong.
        //
        // Litter is ten walk footsteps at a little over a second apart. There is a two-second
        // pause after the fifth where the recordist reset, but the pacing either side of it is the
        // same to a hundredth of a second — 1.01, 1.06, 0.99, 1.02 before, 0.89, 1.06, 0.98, 0.98
        // after — so it is one walk take, not a walk followed by a run. There is no run material
        // for litter, so both pools get all ten; the cadence difference between walking and
        // running comes from the stride in FootstepAudio, not from the clips.
        //
        // Rock is nineteen walk footsteps, a two-second break, then two continuous fast sequences
        // from 20 to 110 at roughly a third of a second apart. Past 110 the same file holds
        // slower, sparser footfalls — around 0.8 s and 1.5 s apart — that are neither the walk nor
        // the run take and are left out rather than wired on a guess.
        //
        // If the splitter is re-run these numbers have to be checked against its report:
        // LoadRange reports a missing clip rather than quietly wiring a shorter set.
        static readonly Range LitterWalk = new Range("litter", 1, 10);
        static readonly Range LitterRun = new Range("litter", 1, 10);
        static readonly Range RockWalk = new Range("rock", 1, 19);

        // Two slices ran to the splitter's 0.60 s ceiling, which it flags as two footfalls merged
        // into one clip: 23 and 92. Played on a single step they read as a stutter, and both sit
        // inside the run pool, which is the pool in continuous use — so they are left out.
        static readonly int[] RockMerged = { 23, 92 };

        static readonly Range RockRun = new Range("rock", 20, 110, RockMerged);

        struct Range
        {
            public readonly string Surface;
            public readonly int First;
            public readonly int Last;
            public readonly int[] Skip;

            public Range(string surface, int first, int last, int[] skip = null)
            {
                Surface = surface;
                First = first;
                Last = last;
                Skip = skip;
            }
        }

        [MenuItem(MenuPath, priority = 60)]
        public static void Build()
        {
            var log = new StringBuilder();
            var problems = new List<string>();

            log.AppendLine("=== Dead Pines — Phase 6, footsteps ===");

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.Log("[Dead Pines] Phase 6 cancelled — the open scene was not saved.");
                return;
            }

            if (!System.IO.File.Exists(ScenePath))
            {
                log.AppendLine();
                log.AppendLine($"  !! {ScenePath} does not exist — run Phase 1 first.");
                Debug.LogWarning(log.ToString());
                return;
            }

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var player = FindAnywhere(scene, PlayerName);
            if (player == null)
            {
                log.AppendLine();
                log.AppendLine($"  !! no {PlayerName} in the scene — run Phase 3 first.");
                Debug.LogWarning(log.ToString());
                return;
            }

            var terrain = FindTerrain(scene);
            if (terrain == null)
                problems.Add($"no {TerrainName} in the scene — surface detection will fall back");

            log.AppendLine();
            log.AppendLine("[Clip sets]");

            // The totals matter as much as the ranges. The splitter's first run used a minimum gap
            // of 0.12 s and wrote 13 litter clips and 161 rock clips; at 0.28 s it writes 10 and
            // 133. A set of the wrong length means the re-split did not happen and the numbers
            // below would land on the wrong slices.
            log.AppendLine($"  litter  {CountSet("litter")} clip(s) on disk   (expected 10)");
            log.AppendLine($"  rock    {CountSet("rock")} clip(s) on disk   (expected 133)");

            AudioClip[] litterWalk = LoadRange(LitterWalk, "litter walk", problems, log);
            AudioClip[] litterRun = LoadRange(LitterRun, "litter run", problems, log);
            AudioClip[] rockWalk = LoadRange(RockWalk, "rock walk", problems, log);
            AudioClip[] rockRun = LoadRange(RockRun, "rock run", problems, log);

            var audio = player.GetComponent<FootstepAudio>();
            if (audio == null)
                audio = player.AddComponent<FootstepAudio>();

            var source = player.GetComponent<AudioSource>();
            if (source == null)
            {
                source = player.AddComponent<AudioSource>();
                log.AppendLine();
                log.AppendLine("[Player]");
                log.AppendLine("  added         AudioSource — the player had none");
            }

            // The source is shared by every footstep; the component re-applies the settings it
            // depends on in Awake, and these are here so the inspector shows the truth at rest.
            source.playOnAwake = false;
            source.spatialBlend = 0f;

            var so = new SerializedObject(audio);

            var terrainProp = so.FindProperty("terrain");
            if (terrainProp == null)
                problems.Add("FootstepAudio has no 'terrain' field — was it renamed?");
            else
                terrainProp.objectReferenceValue = terrain;

            var layersProp = so.FindProperty("layers");
            if (layersProp == null)
            {
                problems.Add("FootstepAudio has no 'layers' field — was it renamed?");
            }
            else
            {
                layersProp.arraySize = LayerLabels.Length;

                // Forest floor and leaf litter are both the litter recording: a pine forest floor
                // is litter, and the take was made on exactly that. The moss-and-rock layer gets
                // the rock take. Mud has no recording yet, so it is left empty and falls through
                // to the fallback below rather than dropping the sound entirely.
                WriteSurface(layersProp, 0, litterWalk, litterRun, problems);
                WriteSurface(layersProp, 1, litterWalk, litterRun, problems);
                WriteSurface(layersProp, 2, new AudioClip[0], new AudioClip[0], problems);
                WriteSurface(layersProp, 3, rockWalk, rockRun, problems);
            }

            WriteClips(so.FindProperty("fallbackWalk"), litterWalk, "fallback walk", problems);
            WriteClips(so.FindProperty("fallbackRun"), litterRun, "fallback run", problems);

            so.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();

            Verify(scene, player, terrain, log);

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
                log.AppendLine("--- footsteps wired ---");
                log.AppendLine();
                log.AppendLine("WHAT TO DO:");
                log.AppendLine("  1. Press Play and walk. You should hear a step every stride.");
                log.AppendLine("  2. Run (Shift) — the steps should get further apart and change sound.");
                log.AppendLine("  3. Crouch (C) — fewer, quieter steps.");
                log.AppendLine("  4. Walk onto the moss and rock, and listen for the surface change.");
                log.AppendLine("  5. Tell me what is wrong: the timing, the level, or the choice.");
                Debug.Log(log.ToString());
            }
        }

        // ---------------------------------------------------------------------------------
        // Clips
        // ---------------------------------------------------------------------------------

        static AudioClip[] LoadRange(Range range, string label, List<string> problems, StringBuilder log)
        {
            var clips = new List<AudioClip>();

            for (int i = range.First; i <= range.Last; i++)
            {
                // Skipped numbers are excluded by name in the range above, not silently: a merge
                // is a known defect in that one slice, and it is better to leave the clip unused
                // than to hear it as a stutter once every half-minute of running.
                if (range.Skip != null && System.Array.IndexOf(range.Skip, i) >= 0)
                    continue;

                string path = $"{FootstepRoot}/step_{range.Surface}_{i:00}.wav";
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);

                if (clip == null)
                {
                    problems.Add($"{label}: {path} is missing — re-run the footstep splitter");
                    break;
                }

                clips.Add(clip);
            }

            string span = $"{range.Surface} {range.First}-{range.Last}";
            if (range.Skip != null && range.Skip.Length > 0)
                span += $", skipping {string.Join(", ", range.Skip)}";

            log.AppendLine("  " + $"{label,-12}  {clips.Count,2} clip(s)   {span}");

            return clips.ToArray();
        }

        /// <summary>
        /// How many clips a surface has, counted from step_&lt;surface&gt;_01 upwards. Reported
        /// rather than asserted, because the useful signal is the number itself: a litter set of
        /// 13 or a rock set of 161 means the splitter has not been re-run since the minimum-gap
        /// fix, and the ranges above would then be pointing at the wrong slices.
        /// </summary>
        static int CountSet(string surface)
        {
            int count = 0;

            while (AssetDatabase.LoadAssetAtPath<AudioClip>(
                       $"{FootstepRoot}/step_{surface}_{count + 1:00}.wav") != null)
                count++;

            return count;
        }

        // ---------------------------------------------------------------------------------
        // Serialized fields
        // ---------------------------------------------------------------------------------

        static void WriteSurface(SerializedProperty layersProp, int index,
                                 AudioClip[] walk, AudioClip[] run, List<string> problems)
        {
            if (index >= layersProp.arraySize)
                return;

            var element = layersProp.GetArrayElementAtIndex(index);
            if (element == null)
            {
                problems.Add($"FootstepAudio layer {index} came back null after resizing the array");
                return;
            }

            var labelProp = element.FindPropertyRelative("label");
            if (labelProp != null)
                labelProp.stringValue = index < LayerLabels.Length ? LayerLabels[index] : "layer " + index;

            WriteClips(element.FindPropertyRelative("walk"), walk,
                       LayerLabels[index] + " walk", problems);
            WriteClips(element.FindPropertyRelative("run"), run,
                       LayerLabels[index] + " run", problems);
        }

        static void WriteClips(SerializedProperty prop, AudioClip[] clips, string label,
                               List<string> problems)
        {
            if (prop == null)
            {
                problems.Add($"FootstepAudio has no clip array for {label} — was a field renamed?");
                return;
            }

            prop.arraySize = clips.Length;

            for (int i = 0; i < clips.Length; i++)
                prop.GetArrayElementAtIndex(i).objectReferenceValue = clips[i];
        }

        // ---------------------------------------------------------------------------------
        // Verification
        // ---------------------------------------------------------------------------------

        static void Verify(UnityEngine.SceneManagement.Scene scene, GameObject player, Terrain terrain,
                           StringBuilder log)
        {
            log.AppendLine();
            log.AppendLine("[Read back]");

            var audio = player.GetComponent<FootstepAudio>();
            var source = player.GetComponent<AudioSource>();

            log.AppendLine($"  component     {(audio != null ? "ok" : "MISSING")}");
            log.AppendLine("  audio source  " + (source != null
                ? "spatialBlend " + source.spatialBlend + ", playOnAwake " + source.playOnAwake
                : "MISSING"));

            if (audio == null)
                return;

            var so = new SerializedObject(audio);

            var terrainProp = so.FindProperty("terrain");
            var boundTerrain = terrainProp != null ? terrainProp.objectReferenceValue : null;
            log.AppendLine("  terrain       " + (boundTerrain != null ? boundTerrain.name : "NOT ASSIGNED"));

            var layersProp = so.FindProperty("layers");
            if (layersProp == null)
                return;

            log.AppendLine($"  layers        {layersProp.arraySize}");

            for (int i = 0; i < layersProp.arraySize; i++)
            {
                var element = layersProp.GetArrayElementAtIndex(i);
                if (element == null)
                {
                    log.AppendLine($"    {i}  (null)");
                    continue;
                }

                var labelProp = element.FindPropertyRelative("label");
                var walkProp = element.FindPropertyRelative("walk");
                var runProp = element.FindPropertyRelative("run");

                int walk = walkProp != null ? walkProp.arraySize : 0;
                int run = runProp != null ? runProp.arraySize : 0;

                string label = labelProp != null ? labelProp.stringValue : "(unlabelled)";
                string note = walk == 0 && run == 0 ? "   <- no clips, falls back" : "";

                log.AppendLine("    " + $"{i}  {label,-14}  walk {walk,2}   run {run,2}{note}");
            }

            log.AppendLine();
            log.AppendLine("  splatmap      layer 0 forest floor, 1 leaf litter, 2 mud, 3 moss and rock");
            log.AppendLine("                (Phase 2 order — the array index is the layer index)");
        }

        static Terrain FindTerrain(UnityEngine.SceneManagement.Scene scene)
        {
            var go = FindAnywhere(scene, TerrainName);
            return go != null ? go.GetComponent<Terrain>() : null;
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
