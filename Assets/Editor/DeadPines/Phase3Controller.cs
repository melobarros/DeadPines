using System.Collections.Generic;
using System.Text;
using DeadPines.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DeadPines.EditorTools
{
    /// <summary>
    /// Phase 3 of M0 — the first-person controller: walk, run, crouch.
    ///
    /// ROADMAP.md's M0 list is exactly "walk, run, crouch" and the success test is walking around
    /// for five minutes without getting bored, so this wires movement and stops. No jump, no head
    /// bob, no stamina, no crosshair — "No UI, no infected, no loot, no HUD — not even a
    /// crosshair." And no body: "M0 and M1 need no character at all", which is why the camera
    /// becomes the player rather than following one.
    ///
    /// Structure is a body carrying the capsule with the camera on a head child:
    ///
    ///   M0_Player   CharacterController, FirstPersonController, yaw        — feet on the ground
    ///     M0_Camera   Camera, AudioListener, pitch                         — at eye height
    ///
    /// Yaw on the body and pitch on the head is what keeps the view from rolling, and it lets the
    /// eye drop on crouch without moving the capsule that decides where the feet are.
    ///
    /// Phase 1 left M0_Camera loose at ground + 1.7 m; this adopts it rather than creating a
    /// second camera, because TECHNICAL.md §"use one camera".
    ///
    /// Idempotent: re-running re-applies the settings and reports what it found. It does not move
    /// a player that is already seated, so a re-run will not teleport you out of wherever you were
    /// standing while testing.
    /// </summary>
    public static class Phase3Controller
    {
        const string MenuPath = "Dead Pines/Phase 3 — First-Person Controller";

        const string ScenePath = "Assets/Scenes/M0.unity";
        const string InputPath = "Assets/InputSystem_Actions.inputactions";

        const string PlayerName = "M0_Player";
        const string CameraName = "M0_Camera";
        const string TerrainName = "M0_Terrain";

        // The four actions this phase binds, in the order the read-back reports them.
        static readonly string[] RequiredActions = { "Move", "Look", "Crouch", "Sprint" };

        // The standing eye height. Phase 1 used 1.7 m for the loose camera; keeping the same
        // number means the view does not shift when the camera changes parents.
        const float EyeHeight = 1.70f;

        // Capsule. These have to agree with FirstPersonController's own serialized defaults —
        // not because anything reads both, but because the script sets controller.center from
        // its own height every frame, so a mismatch here is a one-frame lurch on the first
        // Update. Change one, change the other.
        const float Radius = 0.35f;
        const float Height = 1.80f;

        const float SlopeLimit = 45f;
        const float StepOffset = 0.35f;
        // Unity's rule of thumb is about 10% of the radius. Too small and the controller sticks
        // on contact; too large and it visibly floats above the ground it is standing on.
        const float SkinWidth = 0.05f;

        [MenuItem(MenuPath, priority = 30)]
        public static void Build()
        {
            var log = new StringBuilder();
            var problems = new List<string>();

            log.AppendLine("=== Dead Pines — Phase 3 first-person controller ===");

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.Log("[Dead Pines] Phase 3 cancelled — the open scene was not saved.");
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

            var terrain = FindTerrain(scene);
            if (terrain == null)
            {
                log.AppendLine();
                log.AppendLine($"  !! no {TerrainName} in the scene — run Phase 2 first.");
                Debug.LogWarning(log.ToString());
                return;
            }

            var input = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputPath);
            if (input == null)
                problems.Add($"{InputPath} not found — the controller will have no bindings");

            var player = BuildPlayer(scene, terrain, input, log, problems);
            if (player == null)
            {
                log.AppendLine();
                log.AppendLine("--- COULD NOT BUILD (paste these lines back to Claude) ---");
                foreach (var p in problems)
                    log.AppendLine("  !! " + p);
                Debug.LogWarning(log.ToString());
                return;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();

            Verify(scene, player, terrain, input, log);

            if (problems.Count > 0)
            {
                log.AppendLine();
                log.AppendLine("--- COULD NOT APPLY (paste these lines back to Claude) ---");
                foreach (var p in problems)
                    log.AppendLine("  !! " + p);
                Debug.LogWarning(log.ToString());
            }
            else
            {
                log.AppendLine();
                log.AppendLine("--- controller wired ---");
                log.AppendLine();
                log.AppendLine("WHAT TO DO:");
                log.AppendLine("  1. Press Play. WASD to walk, mouse to look, Shift to run, C to crouch.");
                log.AppendLine("  2. Walk for five minutes. That is the whole M0 success test.");
                log.AppendLine("  3. Tell me the first thing that feels wrong — speed, weight, or the look.");
                Debug.Log(log.ToString());
            }
        }

        // ---------------------------------------------------------------------------------
        // Assembly
        // ---------------------------------------------------------------------------------

        static GameObject BuildPlayer(UnityEngine.SceneManagement.Scene scene, Terrain terrain,
                                      InputActionAsset input, StringBuilder log, List<string> problems)
        {
            var cameraGo = FindAnywhere(scene, CameraName);
            if (cameraGo == null)
            {
                problems.Add($"no {CameraName} in the scene — run Phase 1 first");
                return null;
            }

            if (cameraGo.GetComponent<Camera>() == null)
                problems.Add($"{CameraName} has no Camera component");

            log.AppendLine();
            log.AppendLine("[Player]");

            var player = FindAnywhere(scene, PlayerName);
            bool created = player == null;

            if (created)
            {
                player = new GameObject(PlayerName);

                // Seated where the camera was standing, but with the origin at the feet — the
                // capsule's bottom is at transform.position when center is height/2, which is
                // the convention FirstPersonController maintains while crouching.
                var wasAt = cameraGo.transform.position;
                float ground = GroundHeight(terrain, wasAt.x, wasAt.z);
                player.transform.position = new Vector3(wasAt.x, ground, wasAt.z);

                // The camera's current heading is kept, so building this does not spin the view
                // away from wherever Phase 1 was last pointed.
                float yaw = cameraGo.transform.eulerAngles.y;
                player.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

                log.AppendLine($"  created       {PlayerName} at ({wasAt.x:F2}, {ground:F2}, {wasAt.z:F2}), yaw {yaw:F1}°");
            }
            else
            {
                log.AppendLine($"  found         {PlayerName} already in the scene, left where it is");
            }

            var controller = player.GetComponent<CharacterController>();
            if (controller == null)
                controller = player.AddComponent<CharacterController>();

            controller.radius = Radius;
            controller.height = Height;
            // Capsule bottom at the transform origin. FirstPersonController recomputes this every
            // frame; setting it here is what stops the first frame from being a pop.
            controller.center = new Vector3(0f, Height * 0.5f, 0f);
            controller.slopeLimit = SlopeLimit;
            controller.stepOffset = StepOffset;
            controller.skinWidth = SkinWidth;
            // Unity's default is 0.001 m, which makes the controller ignore any motion smaller
            // than that. At crouch-walk speed on a heightmap that shows up as a visible stutter,
            // so this is the one place the default is worth overriding.
            controller.minMoveDistance = 0f;

            // The default, set explicitly because the default is the behaviour the game needs:
            // the player has to collide with the world. The capsule casts no shadow — it is a
            // collider with no renderer — so D35 has nothing to switch off here.
            controller.detectCollisions = true;

            // Adopt the camera. worldPositionStays: false so the local values below are the only
            // thing deciding where the eye ends up, rather than Unity preserving an offset.
            if (cameraGo.transform.parent != player.transform)
            {
                cameraGo.transform.SetParent(player.transform, false);

                var pivot = cameraGo.transform;
                pivot.localPosition = new Vector3(0f, EyeHeight, 0f);
                pivot.localRotation = Quaternion.identity;

                // Pitch is reset, not preserved — and it could not be preserved from here anyway.
                // FirstPersonController overwrites the head's local rotation from its own pitch
                // field on the first Update, so any tilt set at this line survives zero frames.
                // Carrying it over would mean writing into that private field, which is more
                // machinery than a build script should hold. Yaw does survive, because yaw
                // belongs to the body.
                log.AppendLine($"  adopted       {CameraName} as the head, local (0, {EyeHeight:F2}, 0)");
            }
            else
            {
                log.AppendLine($"  head          {CameraName} already parented, local position left alone");
            }

            // The camera is the player's ears. A camera added from the GameObject menu gets a
            // listener for free; one built from a script does not, and Phase 1 built this one from
            // a script — which is why the read-back below reported zero. Nothing else in the
            // project notices: Unity logs a warning every frame and M0's whole layered soundscape
            // (wind under the birds under the trees) plays to an empty room.
            if (cameraGo.GetComponent<AudioListener>() == null)
            {
                cameraGo.AddComponent<AudioListener>();
                log.AppendLine("  added         AudioListener to the camera — it had none");
            }

            var fps = player.GetComponent<FirstPersonController>();
            if (fps == null)
                fps = player.AddComponent<FirstPersonController>();

            // The two references are private [SerializeField]s, so they go in through
            // SerializedObject. A renamed field surfaces here as a named failure rather than as
            // a controller that silently cannot move.
            var so = new SerializedObject(fps);

            var actionsProp = so.FindProperty("actions");
            if (actionsProp == null)
                problems.Add("FirstPersonController has no 'actions' field — was it renamed?");
            else if (input != null)
                actionsProp.objectReferenceValue = input;

            var headProp = so.FindProperty("head");
            if (headProp == null)
                problems.Add("FirstPersonController has no 'head' field — was it renamed?");
            else
                headProp.objectReferenceValue = cameraGo.transform;

            so.ApplyModifiedPropertiesWithoutUndo();

            log.AppendLine($"  capsule       r {Radius} m, h {Height} m, slope {SlopeLimit}°, " +
                           $"step {StepOffset} m, skin {SkinWidth} m");
            log.AppendLine($"  bindings      {(input != null ? input.name : "NONE")}");

            return player;
        }

        /// <summary>
        /// Terrain height under a world position. GetInterpolatedHeight takes normalized 0..1
        /// coordinates within the tile, and the tile is not centred on its transform — Phase 2
        /// put the origin at the corner and offset the transform by half a tile, so the
        /// conversion has to divide by the size rather than assume it.
        /// </summary>
        static float GroundHeight(Terrain terrain, float x, float z)
        {
            var origin = terrain.transform.position;
            var size = terrain.terrainData.size;

            float u = Mathf.Clamp01((x - origin.x) / size.x);
            float v = Mathf.Clamp01((z - origin.z) / size.z);

            return origin.y + terrain.terrainData.GetInterpolatedHeight(u, v);
        }

        // ---------------------------------------------------------------------------------
        // Verification
        // ---------------------------------------------------------------------------------

        static void Verify(UnityEngine.SceneManagement.Scene scene, GameObject player, Terrain terrain,
                           InputActionAsset input, StringBuilder log)
        {
            log.AppendLine();
            log.AppendLine("[Read back]");

            var controller = player.GetComponent<CharacterController>();
            var fps = player.GetComponent<FirstPersonController>();
            var head = FindAnywhere(scene, CameraName);

            log.AppendLine($"  controller    {(controller != null ? "ok" : "MISSING")}");
            if (controller != null)
                log.AppendLine($"    r {controller.radius} m, h {controller.height}, center {controller.center}, " +
                               $"slope {controller.slopeLimit}°, step {controller.stepOffset}, skin {controller.skinWidth}");

            log.AppendLine($"  behaviour     {(fps != null ? "ok" : "MISSING")}");

            if (fps != null)
            {
                // Read through SerializedObject for the same reason it was written through it:
                // the fields are private, and reading back the value Unity actually stored is
                // the whole point of the read-back.
                var so = new SerializedObject(fps);

                var actionsProp = so.FindProperty("actions");
                var headProp = so.FindProperty("head");

                var boundActions = actionsProp != null ? actionsProp.objectReferenceValue : null;
                var boundHead = headProp != null ? headProp.objectReferenceValue : null;

                log.AppendLine($"    actions     {(boundActions != null ? boundActions.name : "NOT ASSIGNED")}");
                log.AppendLine($"    head        {(boundHead != null ? boundHead.name : "NOT ASSIGNED")}");
            }

            if (head != null)
            {
                // Explicit ==, not ?? — Transform is a UnityEngine.Object, so the null-coalescing
                // operator compares by reference and would sail straight past a destroyed parent.
                var parent = head.transform.parent;
                string parentName = parent != null ? parent.name : "(root)";

                log.AppendLine($"  camera        parent {parentName}, local {head.transform.localPosition}");
            }

            float ground = GroundHeight(terrain, player.transform.position.x, player.transform.position.z);
            log.AppendLine($"  seated        player y {player.transform.position.y:F2}, " +
                           $"terrain {ground:F2}, offset {player.transform.position.y - ground:F2} m");

            // TECHNICAL.md — "use one camera". A second camera in the scene is the kind of thing
            // that renders a duplicate view to nowhere and costs a full frame's worth of work.
            int cameras = 0, listeners = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                cameras += root.GetComponentsInChildren<Camera>(true).Length;
                listeners += root.GetComponentsInChildren<AudioListener>(true).Length;
            }

            log.AppendLine($"  cameras       {cameras}  (TECHNICAL.md: one)");
            log.AppendLine($"  listeners     {listeners}  (one, or Unity logs a warning every frame)");

            if (input == null)
            {
                log.AppendLine();
                log.AppendLine("  !! no input asset — the controller has nothing to read");
                return;
            }

            var map = input.FindActionMap("Player", false);
            log.AppendLine();
            log.AppendLine($"[Input]  {input.name}");

            if (map == null)
            {
                log.AppendLine("  !! no 'Player' action map — the template asset may have changed shape");
                return;
            }

            log.AppendLine($"  Player map    {map.actions.Count} actions");
            foreach (var name in RequiredActions)
            {
                var action = map.FindAction(name, false);
                string bindings = action != null
                    ? $"{action.bindings.Count} binding(s), {action.expectedControlType}"
                    : "MISSING";
                log.AppendLine($"    {name,-8}  {bindings}");
            }
        }

        static Terrain FindTerrain(UnityEngine.SceneManagement.Scene scene)
        {
            var go = FindInScene(scene, TerrainName);
            return go != null ? go.GetComponent<Terrain>() : null;
        }

        static GameObject FindInScene(UnityEngine.SceneManagement.Scene scene, string name)
        {
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == name)
                    return root;
            return null;
        }

        /// <summary>
        /// Finds a GameObject at any depth, not just at the root.
        ///
        /// M0_Camera starts as a root object — Phase 1 left it loose — and becomes a child of
        /// M0_Player the first time this runs. A root-only search would therefore find it once
        /// and then report it missing on every re-run, which reads as "run Phase 1 first" and
        /// sends you off to rebuild a scene that was never broken.
        /// </summary>
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
