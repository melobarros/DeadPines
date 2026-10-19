using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeadPines.EditorTools
{
    /// <summary>
    /// Phase 8 of M0 — the fire lookout tower.
    ///
    /// D27 asks for one distant structure and allows no interior, no loot and no interaction; D28
    /// says it is a fire lookout, tall and thin so it reads as a silhouette from a long way off.
    /// That makes this pass a composition problem more than a modelling one: the tower has to
    /// stand where the fog lets it be seen from the spawn, and the forest has to be open along the
    /// line of sight or the silhouette sits behind a wall of trunks.
    ///
    /// It is built from primitives rather than an imported model because M0 is a test area and the
    /// tower's job is to be a shape on the horizon. The geometry is deliberately plain — four
    /// splayed legs, cross bracing, a railed walkway, a cabin and a pitched roof — because that is
    /// what survives 250 m of ExpSquared fog. The site is chosen from the terrain itself, not by
    /// hand, so moving the terrain moves the tower instead of burying it.
    ///
    /// Idempotent: re-running destroys the previous tower and rebuilds it, and the tree clearing
    /// is a filter over the current forest, so it removes nothing twice. Run it after Phase 5;
    /// re-running Phase 5 re-scatters the trees over the corridor and Phase 8 must run again.
    /// </summary>
    public static class Phase8Tower
    {
        const string MenuPath = "Dead Pines/Phase 8 — The Lookout Tower";

        const string ScenePath = "Assets/Scenes/M0.unity";
        const string TerrainName = "M0_Terrain";
        const string PlayerName = "M0_Player";
        const string RootName = "M0_Lookout";
        const string MaterialPath = "Assets/Settings/M0_Lookout.mat";

        // Where the tower is allowed to stand. The fog is ExpSquared at 0.0055, so 250 m leaves
        // about 15% of the tower's own colour and 290 m about 8%; past 320 m there is nothing left
        // to read. The two rings straddle the band where a silhouette is still an edge rather than
        // a smear.
        static readonly float[] RingRadii = { 250f, 290f };
        const int Bearings = 72;

        // Keep the footprint, its clearing and both ends of the corridor on the tile.
        const float EdgeMargin = 40f;

        // How finely the sightline is tested, and the clearance below which a candidate counts as
        // blocked. Sixty-four samples over 250-290 m is a reading every four metres; the heightmap
        // has one-metre texels, so nothing can hide between samples.
        const int SightSamples = 64;
        const float VisibilityMargin = 1f;

        // Phase 3 seats the camera at this height and Phase 1 built it there.
        const float EyeHeight = 1.7f;

        // The corridor is the width of a forest track. The cabin is 3.8 m across, so six metres
        // either side of the centre line frames it without reading as a road.
        const float CorridorHalfWidth = 6f;

        // And it runs this far past the tower as well. The first look from the spawn found trees
        // directly behind the ridge; a silhouette is only as clean as its backdrop, and sixty
        // metres of cut gives the fog a gap to show through behind the cabin.
        const float CorridorBeyond = 60f;

        // A real lookout stands in a hole in the trees, cut so the watcher can see. Twenty-two
        // metres is that hole at this scale, and it also gives the base room to be walked around.
        const float ClearingRadius = 22f;

        // The feet are set this far below the lowest ground under the footprint, so no leg hangs
        // in the air on the downhill side of a slope.
        const float GroundEmbed = 0.8f;

        // The tower's dimensions. Leg height is the open frame; the cabin sits on a walkway above
        // it, and the roof rises from that.
        //
        // The first pass stopped at 16 m, which put the ridge under the canopy: the Pine_large
        // prefabs are 31.8 m tall before Phase 5's per-tree scale of up to 1.15, so the forest
        // reaches 36.6 m and a 20 m tower is simply another trunk. Thirty-eight metres of legs
        // puts the ridge at 42 m above its own ground — above the tallest pine — so the cabin and
        // roof stand against the fog instead of among the crowns. The cabin, walkway and roof are
        // unchanged; only the frame between the ground and the floor grew.
        const float LegHeight = 38f;

        // One braced bay. The frame divides into as many as fit — seven at this height — so
        // lengthening the legs adds bays rather than stretching them.
        const float BayHeight = 5.5f;
        const float BaseHalf = 3.4f;
        const float TopHalf = 1.8f;
        const float LegThickness = 0.30f;
        const float BraceThickness = 0.16f;

        const float WalkwayHalf = 3f;
        const float WalkwayThickness = 0.25f;
        const float RailingHeight = 1f;

        const float CabinSide = 3.8f;
        const float CabinHeight = 2.6f;

        const float RoofRise = 1.1f;
        const float RoofOverhang = 0.45f;
        const float RoofThickness = 0.12f;

        // What the sightline test aims at: the ridge, not the eaves.
        const float TowerHeight = LegHeight + WalkwayThickness + CabinHeight + RoofRise;

        /// <summary>
        /// A position found by the site search. ground is the terrain height at the centre;
        /// clearance is the tightest gap between the sightline and the terrain, negative when the
        /// ridge is behind a hill.
        /// </summary>
        struct Site
        {
            public Vector3 position;
            public float ground;
            public float clearance;
        }

        [MenuItem(MenuPath, priority = 80)]
        public static void Build()
        {
            var log = new StringBuilder();
            var problems = new List<string>();

            log.AppendLine("=== Dead Pines — Phase 8, the lookout tower ===");

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.Log("[Dead Pines] Phase 8 cancelled — the open scene was not saved.");
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

            if (terrain == null)
                problems.Add(TerrainName + " is missing or has no Terrain component — run Phase 2 first");

            if (player == null)
                problems.Add(PlayerName + " is missing — run Phase 3 first");

            if (terrain == null || player == null)
            {
                Report(log, problems);
                return;
            }

            var material = CreateMaterial(problems);
            if (material == null)
            {
                Report(log, problems);
                return;
            }

            Vector3 spawn = player.transform.position;

            Site site;
            if (!FindSite(terrain, spawn, log, problems, out site))
            {
                Report(log, problems);
                return;
            }

            // The previous tower is destroyed rather than reused: its dimensions may have moved
            // between runs, and rebuilding is the only way the scene is guaranteed to match the
            // constants above.
            var previous = FindAnywhere(scene, RootName);
            if (previous != null)
                Object.DestroyImmediate(previous);

            // A face, not a corner, turned towards the spawn: the projected width of a square is
            // its side when viewed face-on and its diagonal when viewed corner-on. Face-on is the
            // thinner silhouette, and thin is the brief.
            float yaw = YawTowards(site.position, spawn);

            var root = BuildTower(site, yaw, material, terrain);

            ClearTrees(terrain, spawn, site.position, log);

            // The forest lives on the TerrainData asset, not in the scene, so it needs saving
            // separately from the scene.
            EditorUtility.SetDirty(terrain.terrainData);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();

            Verify(root, terrain, player.transform, material, site, log);

            if (problems.Count > 0)
            {
                Report(log, problems);
                return;
            }

            float relative = Mathf.DeltaAngle(player.transform.eulerAngles.y,
                                              Bearing(player.transform.position, site.position));

            log.AppendLine();
            log.AppendLine("--- lookout built ---");
            log.AppendLine();
            log.AppendLine("WHAT TO DO:");
            log.AppendLine("  1. From the spawn, turn about " + Mathf.Abs(relative).ToString("F0")
                           + " degrees " + (relative >= 0f ? "right" : "left") + ".");
            log.AppendLine("     The tower should read as a dark vertical silhouette at the end of a");
            log.AppendLine("     corridor cut through the pines, roughly 250-290 m away, with the");
            log.AppendLine("     cabin and roof standing above the treeline rather than among it.");
            log.AppendLine("  2. Walk the corridor to the base and circle it: four splayed legs, cross");
            log.AppendLine("     bracing, a railed walkway, a cabin, a pitched roof.");
            log.AppendLine("  3. There is no door, no ladder and no interior. D27 keeps M0 free of");
            log.AppendLine("     interaction; the climbable tower is M1's job.");
            log.AppendLine("  4. Tell me: whether the silhouette reads at that distance, whether the");
            log.AppendLine("     corridor looks cut rather than natural, and how tall it feels.");
            Debug.Log(log.ToString());
        }

        // ---------------------------------------------------------------------------------
        // Site
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Finds where the tower stands.
        ///
        /// Two rings of bearings around the spawn are tested against the heightmap. A candidate is
        /// usable when the straight line from the player's eye to the ridge never comes within a
        /// metre of the ground; among those, the one with the most clearance wins, because that is
        /// the one that shows the most of itself above whatever rises between. If nothing is
        /// clear — a valley spawn behind a ridge, say — the highest ground is used and the run
        /// says so rather than silently building a tower nobody can see.
        /// </summary>
        static bool FindSite(Terrain terrain, Vector3 spawn, StringBuilder log,
                             List<string> problems, out Site site)
        {
            var origin = terrain.transform.position;
            var size = terrain.terrainData.size;

            var eye = new Vector3(spawn.x, spawn.y + EyeHeight, spawn.z);

            Site bestClear = default;
            float bestClearance = float.NegativeInfinity;

            Site highest = default;
            float highestGround = float.NegativeInfinity;

            int considered = 0;
            int clearCount = 0;

            foreach (float radius in RingRadii)
            {
                for (int i = 0; i < Bearings; i++)
                {
                    float theta = i * Mathf.PI * 2f / Bearings;

                    float x = spawn.x + Mathf.Sin(theta) * radius;
                    float z = spawn.z + Mathf.Cos(theta) * radius;

                    if (x < origin.x + EdgeMargin || x > origin.x + size.x - EdgeMargin ||
                        z < origin.z + EdgeMargin || z > origin.z + size.z - EdgeMargin)
                        continue;

                    float ground = GroundHeight(terrain, x, z);
                    var ridge = new Vector3(x, ground + TowerHeight, z);

                    float clearance = MinClearance(terrain, eye, ridge);

                    var candidate = new Site
                    {
                        position = new Vector3(x, ground, z),
                        ground = ground,
                        clearance = clearance,
                    };

                    considered++;

                    if (clearance >= VisibilityMargin)
                    {
                        clearCount++;

                        if (clearance > bestClearance)
                        {
                            bestClearance = clearance;
                            bestClear = candidate;
                        }
                    }

                    if (ground > highestGround)
                    {
                        highestGround = ground;
                        highest = candidate;
                    }
                }
            }

            if (considered == 0)
            {
                site = default;
                problems.Add("no candidate position fits on the terrain tile — is the player near an edge?");
                return false;
            }

            bool visible = clearCount > 0;

            site = visible ? bestClear : highest;

            if (!visible)
                problems.Add("no position on the search rings has a clear sightline to its own ridge — "
                             + "the tower went on the highest ground instead");

            float spawnGround = GroundHeight(terrain, spawn.x, spawn.z);

            log.AppendLine();
            log.AppendLine("[Site]");
            log.AppendLine("  search        " + considered + " position(s)  ("
                           + RingRadii.Length + " rings x " + Bearings + " bearings)");
            log.AppendLine("  clear tops    " + clearCount + " of " + considered);
            log.AppendLine("  chosen        x " + site.position.x.ToString("F1")
                           + ", z " + site.position.z.ToString("F1")
                           + ", ground " + site.ground.ToString("F1") + " m");
            log.AppendLine("  distance      " + DistanceXZ(spawn, site.position).ToString("F0")
                           + " m, bearing " + Bearing(spawn, site.position).ToString("F0") + " deg");
            log.AppendLine("  prominence    " + (site.ground - spawnGround).ToString("F1")
                           + " m above the spawn's ground (" + spawnGround.ToString("F1") + " m)");

            if (site.clearance >= 0f)
            {
                log.AppendLine("  sightline     ridge clears the ground by "
                               + site.clearance.ToString("F1") + " m at the tightest point");
            }
            else
            {
                log.AppendLine("  sightline     ridge is " + (-site.clearance).ToString("F1")
                               + " m under the terrain at the tightest point");
            }

            return true;
        }

        /// <summary>
        /// The tightest gap between the eye-to-ridge line and the ground. Negative means the
        /// terrain crosses the line and the ridge cannot be seen.
        /// </summary>
        static float MinClearance(Terrain terrain, Vector3 eye, Vector3 ridge)
        {
            float worst = float.PositiveInfinity;

            for (int i = 1; i < SightSamples; i++)
            {
                float t = i / (float)SightSamples;

                float x = Mathf.Lerp(eye.x, ridge.x, t);
                float z = Mathf.Lerp(eye.z, ridge.z, t);
                float y = Mathf.Lerp(eye.y, ridge.y, t);

                float gap = y - GroundHeight(terrain, x, z);

                if (gap < worst)
                    worst = gap;
            }

            return worst;
        }

        // ---------------------------------------------------------------------------------
        // Building
        // ---------------------------------------------------------------------------------

        static GameObject BuildTower(Site site, float yaw, Material material, Terrain terrain)
        {
            var root = new GameObject(RootName);

            float foot = LowestCorner(terrain, site.position, yaw) - GroundEmbed;

            root.transform.position = new Vector3(site.position.x, foot, site.position.z);
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            var structure = Group(root.transform, "Structure");
            var cabin = Group(root.transform, "Cabin");
            var railing = Group(root.transform, "Railing");
            var roof = Group(root.transform, "Roof");

            // The four legs, splayed from the footprint inwards to the cabin's frame.
            for (int c = 0; c < 4; c++)
            {
                Beam(structure, "Leg" + (c + 1),
                     LegPoint(c, 0f), LegPoint(c, LegHeight), LegThickness, material);
            }

            // Horizontal rings at every bay top, tying the legs together. The bay count follows
            // from the leg height, so a taller frame adds bays instead of stretching them.
            int bays = Mathf.Max(1, Mathf.RoundToInt(LegHeight / BayHeight));
            var heights = new float[bays + 1];

            for (int i = 0; i <= bays; i++)
                heights[i] = LegHeight * i / bays;

            for (int level = 1; level < heights.Length; level++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int next = (c + 1) % 4;

                    Beam(structure, "Ring" + level + "_" + (c + 1),
                         LegPoint(c, heights[level]), LegPoint(next, heights[level]),
                         BraceThickness, material);
                }
            }

            // One diagonal per face per bay, alternating direction. Alternating rather than an X:
            // at this distance the bracing is texture, and a real tower's faces read better with
            // the eye following a single run of zigzags up the frame.
            for (int bay = 0; bay < bays; bay++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int next = (c + 1) % 4;
                    bool forward = ((bay + c) & 1) == 0;

                    Vector3 from = LegPoint(forward ? c : next, heights[bay]);
                    Vector3 to = LegPoint(forward ? next : c, heights[bay + 1]);

                    Beam(structure, "Brace" + (bay + 1) + "_" + (c + 1),
                         from, to, BraceThickness * 0.85f, material);
                }
            }

            // The walkway floor, which overhangs the leg tops, and the cabin standing on it.
            Box(cabin, "Floor",
                new Vector3(0f, LegHeight + WalkwayThickness * 0.5f, 0f),
                new Vector3(WalkwayHalf * 2f, WalkwayThickness, WalkwayHalf * 2f),
                Quaternion.identity, material);

            float cabinBase = LegHeight + WalkwayThickness;

            Box(cabin, "Walls",
                new Vector3(0f, cabinBase + CabinHeight * 0.5f, 0f),
                new Vector3(CabinSide, CabinHeight, CabinSide),
                Quaternion.identity, material);

            // The railing. This is the detail that makes the cabin read as a lookout rather than
            // as a box on stilts, and it is nearly free at this scale.
            float postHalf = WalkwayHalf - 0.10f;

            for (int c = 0; c < 4; c++)
            {
                Vector3 post = SquareCorner(c, postHalf, cabinBase);

                Beam(railing, "Post" + (c + 1),
                     post, post + Vector3.up * RailingHeight, 0.10f, material);
            }

            for (int c = 0; c < 4; c++)
            {
                int next = (c + 1) % 4;

                Beam(railing, "TopRail" + (c + 1),
                     SquareCorner(c, postHalf, cabinBase + RailingHeight),
                     SquareCorner(next, postHalf, cabinBase + RailingHeight),
                     0.08f, material);

                Beam(railing, "MidRail" + (c + 1),
                     SquareCorner(c, postHalf, cabinBase + RailingHeight * 0.55f),
                     SquareCorner(next, postHalf, cabinBase + RailingHeight * 0.55f),
                     0.07f, material);
            }

            // Two tilted slabs meeting at a ridge. Solid slabs rather than a pyramid: a pyramid
            // made of single-sided triangles would be a hole when seen from below the eaves.
            float roofBase = cabinBase + CabinHeight;
            float halfSpan = WalkwayHalf + RoofOverhang;
            float angle = Mathf.Atan2(RoofRise, halfSpan) * Mathf.Rad2Deg;
            float slabLength = Mathf.Sqrt(halfSpan * halfSpan + RoofRise * RoofRise);
            float slabWidth = CabinSide + RoofOverhang * 2f;
            float slabY = roofBase + RoofRise * 0.5f;

            Box(roof, "RoofFront",
                new Vector3(0f, slabY, halfSpan * 0.5f),
                new Vector3(slabWidth, RoofThickness, slabLength),
                Quaternion.Euler(angle, 0f, 0f), material);

            Box(roof, "RoofBack",
                new Vector3(0f, slabY, -halfSpan * 0.5f),
                new Vector3(slabWidth, RoofThickness, slabLength),
                Quaternion.Euler(-angle, 0f, 0f), material);

            Box(roof, "Ridge",
                new Vector3(0f, roofBase + RoofRise, 0f),
                new Vector3(slabWidth * 0.98f, 0.14f, 0.22f),
                Quaternion.identity, material);

            return root;
        }

        /// <summary>
        /// One box of a given size, centred on a point in the parent's space. The cube primitive
        /// is one metre on a side, so a scale is a size.
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

        /// <summary>
        /// A beam between two points in the parent's space. The cube's local Y is stretched along
        /// the span and the whole box is rotated to point that way, so a beam is just a scaled and
        /// turned cube.
        /// </summary>
        static void Beam(Transform parent, string name, Vector3 from, Vector3 to,
                         float thickness, Material material)
        {
            Vector3 delta = to - from;
            float length = delta.magnitude;

            if (length < 0.01f)
                return;

            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);

            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = (from + to) * 0.5f;
            go.transform.localRotation = Quaternion.FromToRotation(Vector3.up, delta / length);
            go.transform.localScale = new Vector3(thickness, length, thickness);

            Finish(go, material);
        }

        static void Finish(GameObject go, Material material)
        {
            var renderer = go.GetComponent<MeshRenderer>();

            renderer.sharedMaterial = material;

            // D35 — nothing in this game casts a shadow. The tower is the last object in M0 that
            // could, and a long shadow across the corridor would be the scene's only one.
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        static Transform Group(Transform parent, string name)
        {
            var go = new GameObject(name);

            go.transform.SetParent(parent, false);

            return go.transform;
        }

        /// <summary>
        /// A corner of the square frame at a given height, in the tower's local space. Corners run
        /// anticlockwise from the -X/-Z one.
        /// </summary>
        static Vector3 SquareCorner(int corner, float half, float y)
        {
            float x = (corner == 0 || corner == 3) ? -half : half;
            float z = (corner == 0 || corner == 1) ? -half : half;

            return new Vector3(x, y, z);
        }

        /// <summary>
        /// Where a leg is at a given height. The legs lean inwards: the footprint is wider than
        /// the frame the cabin sits on, which is what gives a lookout its silhouette.
        /// </summary>
        static Vector3 LegPoint(int corner, float height)
        {
            float half = Mathf.Lerp(BaseHalf, TopHalf, height / LegHeight);

            return SquareCorner(corner, half, height);
        }

        /// <summary>
        /// The lowest ground under the footprint, sampled at the rotated corners. The tower's foot
        /// is set below this, so the downhill legs are as planted as the uphill ones.
        /// </summary>
        static float LowestCorner(Terrain terrain, Vector3 centre, float yaw)
        {
            Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
            float lowest = float.PositiveInfinity;

            for (int c = 0; c < 4; c++)
            {
                Vector3 corner = turn * SquareCorner(c, BaseHalf, 0f);

                float height = GroundHeight(terrain, centre.x + corner.x, centre.z + corner.z);

                if (height < lowest)
                    lowest = height;
            }

            return lowest;
        }

        // ---------------------------------------------------------------------------------
        // Clearing
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Removes the trees that would stand between the spawn and the tower, and those standing
        /// where the tower stands.
        ///
        /// D28's silhouette only works if the line of sight is open. The forest is 286 trees to
        /// the hectare; a 260 m line at eye height passes through dozens of canopies, and a tower
        /// behind them is a rumour. So the corridor is cut — twelve metres wide, the width of a
        /// forest track, from the spawn to sixty metres past the base — and the base gets a
        /// twenty-two metre clearing of its own. That is also what a real lookout has: the tower
        /// stood in a hole in the trees, cut so the watcher could see.
        ///
        /// A filter over the current instances rather than a stored list, so it is idempotent and
        /// survives an earlier Phase 8 having run.
        ///
        /// In practice the corridor usually finds nothing to remove, and that is not a bug: the
        /// site search picks the high ground with the clearest sightline, and Phase 5's clearing
        /// noise leaves much of that ground open. The first Phase 8 run reported zero removals
        /// while the terrain's count had fallen from 28,566 to 28,334, which looked like the
        /// filter was reading the wrong coordinates. It was not. The raw ranges below came back
        /// as 0-1 as documented, and the nearest tree stood 22 m from the base — the line was
        /// simply open. The count drift is Unity pruning a few hundred instances when the
        /// TerrainData is serialized and reloaded; it happens outside this code. The readout
        /// stays because it is the standing proof of all three facts.
        /// </summary>
        static void ClearTrees(Terrain terrain, Vector3 spawn, Vector3 tower, StringBuilder log)
        {
            var data = terrain.terrainData;
            var origin = terrain.transform.position;
            var size = data.size;

            // The corridor continues past the tower, so its far end is a point beyond the base on
            // the same bearing rather than the base itself.
            float dirX = tower.x - spawn.x;
            float dirZ = tower.z - spawn.z;
            float span = Mathf.Sqrt(dirX * dirX + dirZ * dirZ);

            if (span > 1f)
            {
                dirX /= span;
                dirZ /= span;
            }

            float endX = tower.x + dirX * CorridorBeyond;
            float endZ = tower.z + dirZ * CorridorBeyond;

            int count = data.treeInstanceCount;

            var kept = new List<TreeInstance>(count);

            float radiusSq = ClearingRadius * ClearingRadius;
            float corridorSq = CorridorHalfWidth * CorridorHalfWidth;

            int nearBase = 0;
            int alongLine = 0;

            float nearest = float.PositiveInfinity;
            float rawMinX = float.PositiveInfinity, rawMaxX = float.NegativeInfinity;
            float rawMinZ = float.PositiveInfinity, rawMaxZ = float.NegativeInfinity;

            for (int i = 0; i < count; i++)
            {
                // GetTreeInstance rather than the treeInstances array: it is the documented
                // per-index accessor and does not build a 28,000-element copy to read one at a
                // time. Phase 5 uses the same call to count its forest back.
                TreeInstance tree = data.GetTreeInstance(i);

                if (tree.position.x < rawMinX) rawMinX = tree.position.x;
                if (tree.position.x > rawMaxX) rawMaxX = tree.position.x;
                if (tree.position.z < rawMinZ) rawMinZ = tree.position.z;
                if (tree.position.z > rawMaxZ) rawMaxZ = tree.position.z;

                // Normalized to the terrain, as the documentation and Phase 5 both say; the raw
                // range printed below is the standing check that it still is.
                float x = origin.x + tree.position.x * size.x;
                float z = origin.z + tree.position.z * size.z;

                float dx = x - tower.x;
                float dz = z - tower.z;
                float toTower = Mathf.Sqrt(dx * dx + dz * dz);

                if (toTower < nearest)
                    nearest = toTower;

                if (toTower * toTower <= radiusSq)
                {
                    nearBase++;
                    continue;
                }

                if (DistanceToSegmentSq(x, z, spawn.x, spawn.z, endX, endZ) <= corridorSq)
                {
                    alongLine++;
                    continue;
                }

                kept.Add(tree);
            }

            // Only write when something was actually removed. Rewriting an unchanged list means
            // serializing 28,000 instances for nothing, and the terrain's count has already
            // drifted down a few hundred across saves; a run with nothing to remove has no reason
            // to touch it.
            bool written = kept.Count != count;

            if (written)
                data.SetTreeInstances(kept.ToArray(), true);

            log.AppendLine();
            log.AppendLine("[Forest]");
            log.AppendLine("  read          " + count + " instance(s), kept " + kept.Count
                           + (written ? ", rewritten" : ", left untouched"));
            log.AppendLine("  raw range     x " + rawMinX.ToString("F3") + " .. " + rawMaxX.ToString("F3")
                           + ", z " + rawMinZ.ToString("F3") + " .. " + rawMaxZ.ToString("F3")
                           + "   (normalised is 0-1)");

            if (count > 0)
                log.AppendLine("  nearest       " + nearest.ToString("F1") + " m from the tower centre");
            else
                log.AppendLine("  nearest       n/a — the terrain holds no trees");

            log.AppendLine();
            log.AppendLine("[Clearing]");
            log.AppendLine("  base          " + nearBase + " tree(s) removed within "
                           + ClearingRadius.ToString("F0") + " m of the tower");
            log.AppendLine("  corridor      " + alongLine + " tree(s) removed within "
                           + CorridorHalfWidth.ToString("F0") + " m of the sightline");
            log.AppendLine("  remaining     " + data.treeInstanceCount + " tree(s) in the terrain");
        }

        /// <summary>
        /// Squared distance from a point to a line segment, in the XZ plane. Squared because the
        /// only use is a comparison against a radius.
        /// </summary>
        static float DistanceToSegmentSq(float px, float pz,
                                         float ax, float az, float bx, float bz)
        {
            float abx = bx - ax;
            float abz = bz - az;
            float lengthSq = abx * abx + abz * abz;

            if (lengthSq < 0.0001f)
            {
                float dx = px - ax;
                float dz = pz - az;

                return dx * dx + dz * dz;
            }

            float t = ((px - ax) * abx + (pz - az) * abz) / lengthSq;
            t = Mathf.Clamp01(t);

            float ex = px - (ax + abx * t);
            float ez = pz - (az + abz * t);

            return ex * ex + ez * ez;
        }

        // ---------------------------------------------------------------------------------
        // Material
        // ---------------------------------------------------------------------------------

        static Material CreateMaterial(List<string> problems)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);

            if (material == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");

                if (shader == null)
                {
                    problems.Add("could not find the URP Lit shader — is the render pipeline set up?");
                    return null;
                }

                material = new Material(shader);
                AssetDatabase.CreateAsset(material, MaterialPath);
            }

            // Dark weathered timber. The tower's value is its shape: at 250-290 m the fog leaves
            // a few percent of this colour, and anything lighter would read as a pale smear
            // rather than a silhouette.
            material.SetColor("_BaseColor", new Color(0.13f, 0.12f, 0.10f, 1f));
            material.SetFloat("_Smoothness", 0.05f);
            material.SetFloat("_Metallic", 0f);

            EditorUtility.SetDirty(material);

            return material;
        }

        // ---------------------------------------------------------------------------------
        // Verification
        // ---------------------------------------------------------------------------------

        static void Verify(GameObject root, Terrain terrain, Transform player, Material material,
                           Site site, StringBuilder log)
        {
            log.AppendLine();
            log.AppendLine("[Read back]");

            var renderers = root.GetComponentsInChildren<MeshRenderer>();

            log.AppendLine("  root          " + root.name
                           + " at " + root.transform.position.ToString("F1")
                           + ", yaw " + root.transform.eulerAngles.y.ToString("F0"));

            if (renderers.Length == 0)
            {
                log.AppendLine("  parts         NONE — the tower is empty");
                return;
            }

            Bounds bounds = renderers[0].bounds;
            int wrongMaterial = 0;

            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            foreach (var renderer in renderers)
                if (renderer.sharedMaterial != material)
                    wrongMaterial++;

            float ground = GroundHeight(terrain, site.position.x, site.position.z);

            log.AppendLine("  parts         " + renderers.Length + " renderer(s)"
                           + (wrongMaterial > 0
                               ? ", " + wrongMaterial + " on the wrong material"
                               : ", all on M0_Lookout"));
            log.AppendLine("  footprint     " + bounds.size.x.ToString("F1") + " x "
                           + bounds.size.z.ToString("F1") + " m");
            log.AppendLine("  top           " + (bounds.max.y - ground).ToString("F1")
                           + " m above the ground");
            log.AppendLine("  distance      " + DistanceXZ(player.position, site.position).ToString("F0")
                           + " m from the spawn, bearing "
                           + Bearing(player.position, site.position).ToString("F0") + " deg");
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

        static float YawTowards(Vector3 from, Vector3 to)
        {
            return Bearing(from, to);
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
