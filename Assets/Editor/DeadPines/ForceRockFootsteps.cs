using System.Collections.Generic;
using System.Text;
using DeadPines.Audio;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeadPines.EditorTools
{
    /// <summary>
    /// Points every footstep surface at the rock take.
    ///
    /// Phase 6 wires the splatmap by layer: forest floor and leaf litter get the litter
    /// recording, moss and rock gets the rock take, mud falls through to the fallback. That is
    /// the right long-term mix, but it means the ground around and inside the new cabin — a
    /// litter layer under a box floor — sounds soft, and the empty room has to be judged on the
    /// rock take first. This menu item copies the wired rock pools into all four layers and
    /// both fallbacks, so every step is rock until the mix is restored.
    ///
    /// Re-running Phase 6 puts the surface mix back. No clips are loaded from disk here: the
    /// pool is read from the "moss and rock" surface Phase 6 already wrote, so the splitter's
    /// ranges stay the single source of truth.
    /// </summary>
    public static class ForceRockFootsteps
    {
        const string MenuPath = "Dead Pines/Audio — Footsteps on Rock";

        const string ScenePath = "Assets/Scenes/M0.unity";
        const string PlayerName = "M0_Player";
        const string RockLabel = "moss and rock";

        [MenuItem(MenuPath, priority = 70)]
        public static void Apply()
        {
            var log = new StringBuilder();

            log.AppendLine("=== Dead Pines — footsteps on rock ===");

            if (EditorApplication.isPlaying)
            {
                Debug.LogWarning("[Dead Pines] Stop Play mode first — this writes the scene.");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.Log("[Dead Pines] Footsteps were not changed — the open scene was not saved.");
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
            var player = FindAnywhere(scene, PlayerName);
            var audio = player != null ? player.GetComponent<FootstepAudio>() : null;

            if (audio == null)
            {
                log.AppendLine();
                log.AppendLine("  !! " + PlayerName + " is missing or carries no FootstepAudio — "
                               + "run Phase 6 first");
                Debug.LogWarning(log.ToString());
                return;
            }

            var so = new SerializedObject(audio);
            var layers = so.FindProperty("layers");

            if (layers == null || layers.arraySize == 0)
            {
                log.AppendLine();
                log.AppendLine("  !! FootstepAudio carries no layers — run Phase 6 first");
                Debug.LogWarning(log.ToString());
                return;
            }

            SerializedProperty rock = null;

            for (int i = 0; i < layers.arraySize; i++)
            {
                var element = layers.GetArrayElementAtIndex(i);
                var label = element.FindPropertyRelative("label");

                if (label != null && label.stringValue == RockLabel)
                {
                    rock = element;
                    break;
                }
            }

            if (rock == null)
            {
                log.AppendLine();
                log.AppendLine("  !! no '" + RockLabel + "' surface — run Phase 6 first");
                Debug.LogWarning(log.ToString());
                return;
            }

            var rockWalkProp = rock.FindPropertyRelative("walk");
            var rockRunProp = rock.FindPropertyRelative("run");

            List<AudioClip> rockWalk = ReadClips(rockWalkProp);
            List<AudioClip> rockRun = ReadClips(rockRunProp);

            if (rockWalk.Count == 0 && rockRun.Count == 0)
            {
                log.AppendLine();
                log.AppendLine("  !! the '" + RockLabel + "' surface has no clips — "
                               + "run the footstep splitter, then Phase 6");
                Debug.LogWarning(log.ToString());
                return;
            }

            for (int i = 0; i < layers.arraySize; i++)
            {
                var element = layers.GetArrayElementAtIndex(i);

                WriteClips(element.FindPropertyRelative("walk"), rockWalk);
                WriteClips(element.FindPropertyRelative("run"), rockRun);
            }

            WriteClips(so.FindProperty("fallbackWalk"), rockWalk);
            WriteClips(so.FindProperty("fallbackRun"), rockRun);

            so.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();

            log.AppendLine();
            log.AppendLine("  source        '" + RockLabel + "' pool: "
                           + rockWalk.Count + " walk / " + rockRun.Count + " run clip(s)");
            log.AppendLine("  layers        " + layers.arraySize + " rewritten to the rock take");
            log.AppendLine("  fallback      also rock (" + rockWalk.Count + " walk / "
                           + rockRun.Count + " run)");
            log.AppendLine("  written       " + ScenePath + ", " + PlayerName + "'s FootstepAudio");
            log.AppendLine();
            log.AppendLine("  note          the splatmap still reports the terrain layer under the");
            log.AppendLine("                cabin's box floor, so steps inside the room are rock too.");
            log.AppendLine("                Re-run 'Dead Pines/Phase 6 — Wire the Footsteps' to");
            log.AppendLine("                restore the litter / rock surface mix.");
            Debug.Log(log.ToString());
        }

        static List<AudioClip> ReadClips(SerializedProperty property)
        {
            var clips = new List<AudioClip>();

            if (property == null)
                return clips;

            for (int i = 0; i < property.arraySize; i++)
                clips.Add(property.GetArrayElementAtIndex(i).objectReferenceValue as AudioClip);

            return clips;
        }

        static void WriteClips(SerializedProperty property, List<AudioClip> clips)
        {
            if (property == null)
                return;

            property.arraySize = clips.Count;

            for (int i = 0; i < clips.Count; i++)
                property.GetArrayElementAtIndex(i).objectReferenceValue = clips[i];
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
