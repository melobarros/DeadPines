using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeadPines.EditorTools
{
    /// <summary>
    /// M1 — The Place, first step: a building you can walk into, and the change in sound when
    /// you do.
    ///
    /// ROADMAP's M1 asks for one enterable, lootable building and gives the success test in
    /// atmosphere rather than content: "the building feels tense when it's completely empty".
    /// This pass builds only the passive half of that — the shell, the doorway, the windows, a
    /// clearing and an interior reverb zone — so the place can be walked through and listened to
    /// before any interaction or loot exists. Judging the room before filling it is the point;
    /// a box of placeholder loot would answer the wrong question.
    ///
    /// TECHNICAL.md says a location is a scene of its own, joined to the outdoors by light
    /// probes. That split is deliberately not made yet. M0 is still the only scene, and a
    /// doorway between two scenes is only worth building once there is something inside worth
    /// loading. When there is, this shell is what moves across.
    ///
    /// D35 governs the lighting: overcast, no cast shadows, bake once and conventionally. No
    /// lights are placed here. The room is lit by ambient sky and by whatever comes through the
    /// two windows and the door, so it is darker inside than out — which is the first thing the
    /// empty room should say.
    ///
    /// Idempotent: re-running destroys the previous cabin and rebuilds it, and the tree clearing
    /// is a filter over the current instances, so it removes nothing twice.
    /// </summary>
    public static class PlaceShell
    {
        const string MenuPath = "Dead Pines/M1 — The Place (the shell)";

        const string ScenePath = "Assets/Scenes/M0.unity";
        const string TerrainName = "M0_Terrain";
        const string PlayerName = "M0_Player";
        const string CameraName = "M0_Camera";
        const string RootName = "M1_Cabin";

        const string WallsMaterialPath = "Assets/Settings/M1_Cabin_Walls.mat";
        const string FloorMaterialPath = "Assets/Settings/M1_Cabin_Floor.mat";
        const string RoofMaterialPath = "Assets/Settings/M1_Cabin_Roof.mat";

        // --- Site -------------------------------------------------------------------
        //
        // A short walk from the spawn: close enough to reach with one instruction and to be
        // worth re-visiting while the room is being tuned, far enough that arriving is a small
        // act rather than a step out of the spawn.
        const float TargetAheadMetres = 45f;
        const float SearchLateralMetres = 25f;
        const float SearchBackMetres = 10f;
        const float SearchForwardMetres = 20f;
        const float SearchStepMetres = 5f;

        // The flattest patch wins. Nine samples across this radius: the centre and the eight
        // compass points outside the floor slab's corners, so no corner of the slab can be
        // higher than the sampled ground the floor top was set from.
        const float SiteSampleRadius = 4.1f;
        const float SiteEdgeMargin = 12f;

        // --- The shell --------------------------------------------------------------
        //
        // Interior 5.6 x 4.6 m under a 2.6 m ceiling: one room, small enough that the walls are
        // always in view. A larger footprint would read as a hall, and a hall is not tense.
        const float Width = 6f;
        const float Depth = 5f;
        const float WallThickness = 0.2f;
        const float WallHeight = 2.6f;

        // The doorway. 1.1 m against a 0.7 m character: it admits one person without inviting
        // a crowd, and it is the first place in this project where a gap in geometry is the
        // design rather than a side effect.
        const float DoorWidth = 1.1f;
        const float DoorHeight = 2.05f;

        // A window each side at standing eye height. Sill 1.05 puts the outside at the level a
        // standing player looks through; top 1.9 keeps the opening under the eaves.
        const float WindowHalfWidth = 0.5f;
        const float WindowSill = 1.05f;
        const float WindowTop = 1.9f;

        const float RoofRise = 0.85f;
        const float RoofOverhang = 0.4f;
        const float RoofThickness = 0.15f;

        // The slab is thick because its top is fixed at the highest ground under the footprint
        // and its underside is buried. How deep it goes is invisible.
        const float FloorThickness = 0.8f;
        const float FloorLift = 0.1f;

        // The stoop. The terrain is uneven, so the floor can sit up to the site's height range
        // above the ground at the door. Steps of at most 0.28 m each — under the controller's
        // 0.35 step offset — make the doorway enterable from the downhill side too.
        const float StepDepth = 0.55f;
        const float StepThickness = 0.45f;
        const float MaxStepRise = 0.28f;
        const int MaxSteps = 4;
        const float StoopReach = StepDepth * MaxSteps;

        // Close enough that the trunk line is gone, far enough that the roof overhang has air
        // around it. At 286 stems to the hectare this removes about seven trees.
        const float ClearingRadius = 9f;

        // The reverb zone is a sphere centred in the room. Full strength within minDistance,
        // gone at maxDistance: the doorway sits just past minDistance and the fade is complete
        // a few metres outside the walls.
        const float ReverbHeight = 1.3f;
        const float ReverbMinDistance = 2.5f;
        const float ReverbMaxDistance = 7f;

        /// <summary>
        /// A position found by the site search: the flattest patch examined, with the ground
        /// range that made it the winner.
        /// </summary>
        struct Site
        {
            public float x;
            public float z;
            public float groundMin;
            public float groundMax;
        }

        [MenuItem(MenuPath, priority = 100)]
        public static void Build()
        {
            var log = new StringBuilder();
            var problems = new List<string>();

            log.AppendLine("=== Dead Pines — M1, the shell ===");

            if (EditorApplication.isPlaying)
            {
                Debug.LogWarning("[Dead Pines] Stop Play mode first — the cabin is scene content.");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.Log("[Dead Pines] The shell was not built — the open scene was not saved.");
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

            var terrainGo = FindAnywhere(scene, TerrainName);
            var terrain = terrainGo != null ? terrainGo.GetComponent<Terrain>() : null;
            var player = FindAnywhere(scene, PlayerName);
            var cameraGo = FindAnywhere(scene, CameraName);

            if (terrain == null)
                problems.Add(TerrainName + " is missing or has no Terrain component — run Phase 2 first");

            if (player == null)
                problems.Add(PlayerName + " is missing — run Phase 3 first");

            if (cameraGo == null)
                problems.Add(CameraName + " is missing — run Phase 1 first");

            if (terrain == null || player == null)
            {
                Report(log, problems);
                return;
            }

            var walls = CreateMaterial(WallsMaterialPath, new Color(0.16f, 0.13f, 0.10f, 1f), problems);
            var floor = CreateMaterial(FloorMaterialPath, new Color(0.11f, 0.09f, 0.07f, 1f), problems);
            var roof = CreateMaterial(RoofMaterialPath, new Color(0.06f, 0.06f, 0.06f, 1f), problems);

            if (walls == null || floor == null || roof == null)
            {
                Report(log, problems);
                return;
            }

            Vector3 spawn = player.transform.position;

            Vector3 forward = player.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;
            forward.Normalize();

            Site site = FindSite(terrain, spawn + forward * TargetAheadMetres, log);

            // The previous cabin is destroyed rather than reused: its dimensions may have moved
            // between runs, and rebuilding is the only way the scene is guaranteed to match the
            // constants above.
            var previous = FindAnywhere(scene, RootName);
            if (previous != null)
                Object.DestroyImmediate(previous);

            var root = BuildCabin(terrain, site, spawn, walls, floor, roof);

            ClearTrees(terrain, site, log);

            // Camera.main is null without this tag, and the performance probe reported exactly
            // that: "camera no MainCamera". Nothing in the game needs Camera.main today, but the
            // first system that does — a sound that follows the listener, a scoped interaction
            // ray — would fail silently. Set it now, while it costs one line.
            bool cameraTagged = false;
            if (cameraGo != null && cameraGo.tag != "MainCamera")
            {
                cameraGo.tag = "MainCamera";
                cameraTagged = true;
            }

            // The forest lives on the TerrainData asset, not in the scene, so it needs saving
            // separately from the scene.
            EditorUtility.SetDirty(terrain.terrainData);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();

            Verify(root, spawn, site, cameraGo, cameraTagged, log);

            if (problems.Count > 0)
            {
                Report(log, problems);
                return;
            }

            float distance = DistanceXZ(spawn, new Vector3(site.x, 0f, site.z));

            log.AppendLine();
            log.AppendLine("--- shell built ---");
            log.AppendLine();
            log.AppendLine("WHAT TO DO:");
            log.AppendLine("  1. Press Play and walk forward about " + distance.ToString("F0") + " m.");
            log.AppendLine("     The cabin stands in a small clearing, door facing you.");
            log.AppendLine("  2. Stand outside for a few seconds and listen to the wind. Then step");
            log.AppendLine("     through the doorway. The wind should recede and your footsteps");
            log.AppendLine("     should acquire a room: shorter, harder, with a tail.");
            log.AppendLine("  3. Turn on the spot inside and look out through the two windows. The");
            log.AppendLine("     room will be dim — no light is baked yet, and D35 bakes once.");
            log.AppendLine("     That dimness is expected, not a failure.");
            log.AppendLine("  4. Tell me: whether the room feels tense while empty, whether the reverb");
            log.AppendLine("     reads as a room rather than a cave, and whether the doorway is");
            log.AppendLine("     wide enough to enter without clipping a shoulder.");
            Debug.Log(log.ToString());
        }

        // ---------------------------------------------------------------------------------
        // Site
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Finds the flattest patch near a target point ahead of the spawn.
        ///
        /// The cabin's floor is a single level, so the ground under it wants to be level too.
        /// Every candidate on a 5 m grid is sampled at nine points across the footprint, and the
        /// one with the smallest height range wins. The stoop absorbs what is left, but a site
        /// that still falls away sharply is called out rather than quietly hidden.
        /// </summary>
        static Site FindSite(Terrain terrain, Vector3 target, StringBuilder log)
        {
            var origin = terrain.transform.position;
            var size = terrain.terrainData.size;

            Site best = default;
            float bestRange = float.PositiveInfinity;

            int considered = 0;

            for (float dz = -SearchBackMetres;
                 dz <= SearchForwardMetres + 0.001f;
                 dz += SearchStepMetres)
            {
                for (float dx = -SearchLateralMetres;
                     dx <= SearchLateralMetres + 0.001f;
                     dx += SearchStepMetres)
                {
                    float x = target.x + dx;
                    float z = target.z + dz;

                    if (x < origin.x + SiteEdgeMargin || x > origin.x + size.x - SiteEdgeMargin ||
                        z < origin.z + SiteEdgeMargin || z > origin.z + size.z - SiteEdgeMargin)
                        continue;

                    float lowest = float.PositiveInfinity;
                    float highest = float.NegativeInfinity;

                    for (int i = 0; i < 9; i++)
                    {
                        float angle = i * Mathf.PI * 0.25f;
                        float radius = i == 8 ? 0f : SiteSampleRadius;

                        float height = GroundHeight(terrain,
                            x + Mathf.Cos(angle) * radius,
                            z + Mathf.Sin(angle) * radius);

                        if (height < lowest) lowest = height;
                        if (height > highest) highest = height;
                    }

                    considered++;

                    float range = highest - lowest;

                    if (range < bestRange)
                    {
                        bestRange = range;
                        best = new Site
                        {
                            x = x,
                            z = z,
                            groundMin = lowest,
                            groundMax = highest,
                        };
                    }
                }
            }

            // A spawn near the tile edge can rule every candidate out. Building at the world
            // origin in that case would put the cabin in a corner of the terrain with no word
            // said, so the target itself is used and the log admits it.
            if (considered == 0)
            {
                float ground = GroundHeight(terrain, target.x, target.z);

                best = new Site
                {
                    x = target.x,
                    z = target.z,
                    groundMin = ground,
                    groundMax = ground,
                };

                bestRange = 0f;
            }

            log.AppendLine();
            log.AppendLine("[Site]");
            log.AppendLine("  search        " + considered + " position(s) on a "
                           + SearchStepMetres.ToString("F0") + " m grid ahead of the spawn");

            if (considered == 0)
                log.AppendLine("  note          no candidate fitted on the tile — the target point was used");

            log.AppendLine("  chosen        x " + best.x.ToString("F1")
                           + ", z " + best.z.ToString("F1"));
            log.AppendLine("  ground        " + best.groundMin.ToString("F2")
                           + " .. " + best.groundMax.ToString("F2")
                           + " m across the footprint (range " + bestRange.ToString("F2") + " m)");
            log.AppendLine("  floor top     " + (best.groundMax + FloorLift).ToString("F2")
                           + " m, door faces the spawn");

            if (bestRange > 0.5f)
            {
                log.AppendLine("  note          the flattest patch still falls "
                               + bestRange.ToString("F2")
                               + " m — the stoop covers it, but the ground under the walls is uneven");
            }

            return best;
        }

        // ---------------------------------------------------------------------------------
        // Building
        // ---------------------------------------------------------------------------------

        static GameObject BuildCabin(Terrain terrain, Site site, Vector3 spawn,
                                     Material walls, Material floor, Material roof)
        {
            float floorTop = site.groundMax + FloorLift;

            // The door is built on local -Z, so local +Z must point away from the spawn.
            float yaw = Bearing(spawn, new Vector3(site.x, 0f, site.z));

            var root = new GameObject(RootName);

            root.transform.position = new Vector3(site.x, floorTop, site.z);
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            var shell = Group(root.transform, "Shell");
            var steps = Group(shell, "Stoop");

            float halfWidth = Width * 0.5f;
            float halfDepth = Depth * 0.5f;
            float halfWall = WallThickness * 0.5f;

            // The front and back walls run the full width; the side walls fit between them.
            float frontZ = -(halfDepth - halfWall);
            float backZ = halfDepth - halfWall;
            float sideX = halfWidth - halfWall;
            float sideDepth = Depth - WallThickness * 2f;

            // --- Floor --------------------------------------------------------------
            Box(shell, "Floor",
                new Vector3(0f, -FloorThickness * 0.5f, 0f),
                new Vector3(Width + 0.2f, FloorThickness, Depth + 0.2f),
                Quaternion.identity, floor);

            // --- Back wall ----------------------------------------------------------
            Box(shell, "Wall_Back",
                new Vector3(0f, WallHeight * 0.5f, backZ),
                new Vector3(Width, WallHeight, WallThickness),
                Quaternion.identity, walls);

            // --- Front wall: two sides and a lintel over the doorway -----------------
            float frontSideWidth = (Width - DoorWidth) * 0.5f;
            float frontSideCentre = DoorWidth * 0.5f + frontSideWidth * 0.5f;
            float lintelHeight = WallHeight - DoorHeight;

            Box(shell, "Wall_Front_Left",
                new Vector3(-frontSideCentre, WallHeight * 0.5f, frontZ),
                new Vector3(frontSideWidth, WallHeight, WallThickness),
                Quaternion.identity, walls);

            Box(shell, "Wall_Front_Right",
                new Vector3(frontSideCentre, WallHeight * 0.5f, frontZ),
                new Vector3(frontSideWidth, WallHeight, WallThickness),
                Quaternion.identity, walls);

            Box(shell, "Wall_Front_Lintel",
                new Vector3(0f, DoorHeight + lintelHeight * 0.5f, frontZ),
                new Vector3(DoorWidth, lintelHeight, WallThickness),
                Quaternion.identity, walls);

            // --- Side walls: a window in each --------------------------------------
            float windowTopHeight = WallHeight - WindowTop;
            float windowMidHeight = WindowTop - WindowSill;

            float windowSideDepth = (sideDepth - WindowHalfWidth * 2f) * 0.5f;
            float windowSideCentre = WindowHalfWidth + windowSideDepth * 0.5f;
            float windowMidY = WindowSill + windowMidHeight * 0.5f;

            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? -1f : 1f;
                string label = side == 0 ? "Left" : "Right";
                float x = sign * sideX;

                Box(shell, "Wall_" + label + "_Bottom",
                    new Vector3(x, WindowSill * 0.5f, 0f),
                    new Vector3(WallThickness, WindowSill, sideDepth),
                    Quaternion.identity, walls);

                Box(shell, "Wall_" + label + "_Top",
                    new Vector3(x, WindowTop + windowTopHeight * 0.5f, 0f),
                    new Vector3(WallThickness, windowTopHeight, sideDepth),
                    Quaternion.identity, walls);

                Box(shell, "Wall_" + label + "_Front",
                    new Vector3(x, windowMidY, -windowSideCentre),
                    new Vector3(WallThickness, windowMidHeight, windowSideDepth),
                    Quaternion.identity, walls);

                Box(shell, "Wall_" + label + "_Back",
                    new Vector3(x, windowMidY, windowSideCentre),
                    new Vector3(WallThickness, windowMidHeight, windowSideDepth),
                    Quaternion.identity, walls);
            }

            // --- Roof: two slabs meeting at a ridge ---------------------------------
            //
            // Solid slabs rather than a single pitched shell: a plane has no thickness, and the
            // eave would be a paper edge seen from below.
            float halfSpan = halfDepth + RoofOverhang;
            float roofAngle = Mathf.Atan2(RoofRise, halfSpan) * Mathf.Rad2Deg;
            float slabLength = Mathf.Sqrt(halfSpan * halfSpan + RoofRise * RoofRise);
            float slabWidth = Width + RoofOverhang * 2f;
            float slabY = WallHeight + RoofRise * 0.5f;

            Box(shell, "Roof_Front",
                new Vector3(0f, slabY, halfSpan * 0.5f),
                new Vector3(slabWidth, RoofThickness, slabLength),
                Quaternion.Euler(roofAngle, 0f, 0f), roof);

            Box(shell, "Roof_Back",
                new Vector3(0f, slabY, -halfSpan * 0.5f),
                new Vector3(slabWidth, RoofThickness, slabLength),
                Quaternion.Euler(-roofAngle, 0f, 0f), roof);

            Box(shell, "Ridge",
                new Vector3(0f, WallHeight + RoofRise, 0f),
                new Vector3(slabWidth * 0.98f, 0.14f, 0.22f),
                Quaternion.identity, roof);

            // --- Stoop --------------------------------------------------------------
            //
            // The ground at the far end of the stoop decides how many steps are needed to come
            // down from the floor without a rise over the controller's step offset.
            Vector3 stoopEnd = root.transform.TransformPoint(
                new Vector3(0f, 0f, -(halfDepth + StoopReach)));

            float stoopGround = GroundHeight(terrain, stoopEnd.x, stoopEnd.z);
            float drop = Mathf.Max(floorTop - stoopGround, 0.1f);
            int stepCount = Mathf.Clamp(Mathf.CeilToInt(drop / MaxStepRise), 1, MaxSteps);

            for (int i = 0; i < stepCount; i++)
            {
                float top = floorTop - drop * (i + 1) / stepCount;
                float z = -(halfDepth + StepDepth * (i + 0.5f));

                Box(steps, "Step_" + (i + 1),
                    new Vector3(0f, top - StepThickness * 0.5f, z),
                    new Vector3(DoorWidth + 0.5f, StepThickness, StepDepth),
                    Quaternion.identity, floor);
            }

            // --- Reverb -------------------------------------------------------------
            //
            // The room's first piece of atmosphere, and the only one this pass can build
            // without knowing how the interior is lit. A stock Room preset rather than a
            // hand-tuned one: it is audibly a room, and it is the reference the eventual
            // per-location reverb should improve on rather than replace blind.
            var reverbGo = new GameObject("InteriorReverb");

            reverbGo.transform.SetParent(root.transform, false);
            reverbGo.transform.localPosition = new Vector3(0f, ReverbHeight, 0f);

            var reverb = reverbGo.AddComponent<AudioReverbZone>();

            reverb.minDistance = ReverbMinDistance;
            reverb.maxDistance = ReverbMaxDistance;
            reverb.reverbPreset = AudioReverbPreset.Room;

            return root;
        }

        /// <summary>
        /// One box of a given size, centred on a point in the parent's space. The cube primitive
        /// is one metre on a side, so a scale is a size, and it arrives with a collider — which
        /// is what makes the cabin enterable rather than a picture of one.
        /// </summary>
        static void Box(Transform parent, string name, Vector3 centre, Vector3 size,
                        Quaternion rotation, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);

            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = centre;
            go.transform.localRotation = rotation;
            go.transform.localScale = size;

            Finish(go, material);
        }

        static void Finish(GameObject go, Material material)
        {
            var renderer = go.GetComponent<MeshRenderer>();

            renderer.sharedMaterial = material;

            // D35 — nothing in this game casts a shadow, and the cabin's roof over the doorway
            // would be the scene's most conspicuous one.
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        static Transform Group(Transform parent, string name)
        {
            var go = new GameObject(name);

            go.transform.SetParent(parent, false);

            return go.transform;
        }

        // ---------------------------------------------------------------------------------
        // Clearing
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Removes the trees standing where the cabin stands.
        ///
        /// A filter over the current instances rather than a stored list, so it is idempotent and
        /// survives an earlier run having already cleared here. Only writes when something was
        /// actually removed — rewriting an unchanged 28,000-instance list would serialize it for
        /// nothing.
        /// </summary>
        static void ClearTrees(Terrain terrain, Site site, StringBuilder log)
        {
            var data = terrain.terrainData;
            var origin = terrain.transform.position;
            var size = data.size;

            int count = data.treeInstanceCount;

            var kept = new List<TreeInstance>(count);

            float radiusSq = ClearingRadius * ClearingRadius;
            int removed = 0;

            for (int i = 0; i < count; i++)
            {
                TreeInstance tree = data.GetTreeInstance(i);

                // Normalized to the terrain, as the documentation and Phase 5 both say.
                float x = origin.x + tree.position.x * size.x;
                float z = origin.z + tree.position.z * size.z;

                float dx = x - site.x;
                float dz = z - site.z;

                if (dx * dx + dz * dz <= radiusSq)
                {
                    removed++;
                    continue;
                }

                kept.Add(tree);
            }

            bool written = removed > 0;

            if (written)
                data.SetTreeInstances(kept.ToArray(), true);

            log.AppendLine();
            log.AppendLine("[Forest]");
            log.AppendLine("  read          " + count + " instance(s), kept " + kept.Count
                           + (written ? ", rewritten" : ", left untouched"));
            log.AppendLine("  removed       " + removed + " tree(s) within "
                           + ClearingRadius.ToString("F0") + " m of the cabin");
            log.AppendLine("  remaining     " + data.treeInstanceCount + " tree(s) in the terrain");
        }

        // ---------------------------------------------------------------------------------
        // Materials
        // ---------------------------------------------------------------------------------

        static Material CreateMaterial(string path, Color color, List<string> problems)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (material == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");

                if (shader == null)
                {
                    problems.Add("could not find the URP Lit shader — is the render pipeline set up?");
                    return null;
                }

                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            // Weathered timber, unlit-looking in value. At M0's ambient level these three read
            // as wall, floor and roof without furniture to tell them apart.
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", 0.05f);
            material.SetFloat("_Metallic", 0f);

            EditorUtility.SetDirty(material);

            return material;
        }

        // ---------------------------------------------------------------------------------
        // Verification
        // ---------------------------------------------------------------------------------

        static void Verify(GameObject root, Vector3 spawn, Site site,
                           GameObject cameraGo, bool cameraTagged, StringBuilder log)
        {
            log.AppendLine();
            log.AppendLine("[Read back]");

            var renderers = root.GetComponentsInChildren<MeshRenderer>();

            log.AppendLine("  root          " + root.name
                           + " at " + root.transform.position.ToString("F1")
                           + ", yaw " + root.transform.eulerAngles.y.ToString("F0"));

            if (renderers.Length == 0)
            {
                log.AppendLine("  parts         NONE — the cabin is empty");
                return;
            }

            Bounds bounds = renderers[0].bounds;

            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            int wrongMaterial = 0;
            var cabinMaterials = new List<Material>();

            for (int i = 0; i < renderers.Length; i++)
            {
                Material material = renderers[i].sharedMaterial;

                if (material == null)
                {
                    wrongMaterial++;
                    continue;
                }

                if (!cabinMaterials.Contains(material))
                    cabinMaterials.Add(material);
            }

            float floorTop = site.groundMax + FloorLift;

            log.AppendLine("  parts         " + renderers.Length + " renderer(s) on "
                           + cabinMaterials.Count + " material(s)"
                           + (wrongMaterial > 0 ? ", " + wrongMaterial + " with none" : ""));
            log.AppendLine("  footprint     " + bounds.size.x.ToString("F1") + " x "
                           + bounds.size.z.ToString("F1") + " m including the stoop");
            log.AppendLine("  interior      " + (Width - WallThickness * 2f).ToString("F1")
                           + " x " + (Depth - WallThickness * 2f).ToString("F1")
                           + " m, ceiling " + WallHeight.ToString("F1") + " m");
            log.AppendLine("  floor         top " + floorTop.ToString("F2")
                           + " m, ground below " + site.groundMin.ToString("F2")
                           + " .. " + site.groundMax.ToString("F2") + " m");
            log.AppendLine("  doorway       " + DoorWidth.ToString("F1") + " x "
                           + DoorHeight.ToString("F2") + " m, facing the spawn");

            var reverb = root.GetComponentInChildren<AudioReverbZone>();

            if (reverb == null)
            {
                log.AppendLine("  reverb        NONE — the zone did not survive the build");
            }
            else
            {
                log.AppendLine("  reverb        " + reverb.reverbPreset
                               + ", full within " + reverb.minDistance.ToString("F1")
                               + " m, silent at " + reverb.maxDistance.ToString("F1") + " m");
            }

            log.AppendLine("  camera        " + (cameraGo == null
                               ? "no " + CameraName
                               : cameraGo.tag + (cameraTagged ? " (tag set this run)" : " (unchanged)")));
            log.AppendLine("  distance      " + DistanceXZ(spawn, new Vector3(site.x, 0f, site.z))
                               .ToString("F0") + " m from the spawn, bearing "
                           + Bearing(spawn, new Vector3(site.x, 0f, site.z)).ToString("F0")
                           + " deg");
        }

        // ---------------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------------

        static float GroundHeight(Terrain terrain, float x, float z)
        {
            var origin = terrain.transform.position;
            var size = terrain.terrainData.size;

            float u = Mathf.Clamp01((x - origin.x) / size.x);
            float v = Mathf.Clamp01((z - origin.z) / size.z);

            return origin.y + terrain.terrainData.GetInterpolatedHeight(u, v);
        }

        static float DistanceXZ(Vector3 from, Vector3 to)
        {
            float dx = to.x - from.x;
            float dz = to.z - from.z;

            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// Compass-style bearing in degrees: +Z is 0 and +X is 90, the same convention as a Y yaw.
        /// </summary>
        static float Bearing(Vector3 from, Vector3 to)
        {
            return Mathf.Atan2(to.x - from.x, to.z - from.z) * Mathf.Rad2Deg;
        }

        static void Report(StringBuilder log, List<string> problems)
        {
            log.AppendLine();
            log.AppendLine("--- PROBLEMS (paste these lines back to Claude) ---");

            foreach (var problem in problems)
                log.AppendLine("  !! " + problem);

            Debug.LogWarning(log.ToString());
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
