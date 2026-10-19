using System.Text;
using DeadPines.Diagnostics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeadPines.EditorTools
{
    /// <summary>
    /// Adds and removes the M0 performance probe.
    ///
    /// The probe is a scene object rather than editor-only tooling for one reason: the numbers
    /// that matter come from a build, not from the Editor. Keeping the same component in the
    /// scene means the run in Play mode and the run in a development player measure the same
    /// thing, and the second one does not need a different tool.
    ///
    /// Adding it saves M0, so pressing Play runs it. Removing it takes the object out again —
    /// the probe is not part of the game and must not survive the measurement pass.
    /// </summary>
    public static class MeasurePerformance
    {
        const string MenuAdd = "Dead Pines/Measure — Add the Performance Probe";
        const string MenuRemove = "Dead Pines/Measure — Remove the Performance Probe";

        const string ScenePath = "Assets/Scenes/M0.unity";
        const string RootName = "M0_PerformanceProbe";

        // The probe's run length, written on every add so a probe already in the scene picks up
        // a changed length rather than keeping the value it was serialized with.
        const float SampleSeconds = 180f;

        [MenuItem(MenuAdd, priority = 90)]
        public static void AddProbe()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogWarning("[Dead Pines] Stop Play mode before adding the probe.");
                return;
            }

            if (!System.IO.File.Exists(ScenePath))
            {
                Debug.LogWarning("[Dead Pines] " + ScenePath + " does not exist — run Phase 1 first.");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var root = FindRoot(scene, RootName);
            if (root == null)
                root = new GameObject(RootName);

            if (root.GetComponent<PerformanceProbe>() == null)
                root.AddComponent<PerformanceProbe>();

            // Written through SerializedObject rather than relying on the field initialiser: a
            // probe already saved into the scene keeps its own serialized value, so changing the
            // default alone would leave an earlier add running for the old length.
            var probe = root.GetComponent<PerformanceProbe>();
            var probeSo = new SerializedObject(probe);
            var sample = probeSo.FindProperty("sampleSeconds");

            if (sample != null)
                sample.floatValue = SampleSeconds;

            probeSo.ApplyModifiedPropertiesWithoutUndo();

            // FrameTimingManager only captures while this player setting is on. It is left on
            // deliberately: the cost is negligible and every later build measurement needs it.
            bool changed = !PlayerSettings.enableFrameTimingStats;
            PlayerSettings.enableFrameTimingStats = true;

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();

            var log = new StringBuilder();
            log.AppendLine("=== Dead Pines — performance probe ===");
            log.AppendLine();
            log.AppendLine("  probe         " + RootName + " added to M0");
            log.AppendLine("  frame stats   " + (changed
                               ? "enabled in Player Settings (was off)"
                               : "already enabled in Player Settings"));
            log.AppendLine("  vSync         " + QualitySettings.vSyncCount + " (0 is uncapped)");
            log.AppendLine("  sample        " + (sample != null
                               ? SampleSeconds.ToString("F0") + " s after the warm-up"
                               : "could not be set — 'sampleSeconds' field not found"));
            log.AppendLine();
            log.AppendLine("HOW TO RUN:");
            log.AppendLine("  1. Maximise the Game view. A visible Scene view renders the whole");
            log.AppendLine("     terrain too and inflates draw calls and triangles; the report");
            log.AppendLine("     prints the resolution it actually measured.");
            log.AppendLine("  2. Press Play and keep the Game view focused. The probe ignores the");
            log.AppendLine("     first 10 s while shaders and terrain settle, then samples for");
            log.AppendLine("     three minutes, printing one line a minute.");
            log.AppendLine("  3. Walk around normally after the warm-up. The report aggregates");
            log.AppendLine("     the whole run; stopping early still prints a partial report.");
            log.AppendLine("  4. Paste the final report. Then click");
            log.AppendLine("     '" + MenuRemove + "'.");
            Debug.Log(log.ToString());
        }

        [MenuItem(MenuRemove, priority = 91)]
        public static void RemoveProbe()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogWarning("[Dead Pines] Stop Play mode before removing the probe.");
                return;
            }

            if (!System.IO.File.Exists(ScenePath))
                return;

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var root = FindRoot(scene, RootName);

            if (root == null)
            {
                Debug.Log("[Dead Pines] No " + RootName + " in M0 — nothing to remove.");
                return;
            }

            Object.DestroyImmediate(root);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();

            Debug.Log("[Dead Pines] Removed " + RootName + " from M0.");
        }

        static GameObject FindRoot(UnityEngine.SceneManagement.Scene scene, string name)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == name)
                    return root;
            }

            return null;
        }
    }
}
