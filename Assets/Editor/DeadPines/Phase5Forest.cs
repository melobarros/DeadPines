using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeadPines.EditorTools
{
    /// <summary>
    /// Phase 5 of M0 — the forest.
    ///
    /// ROADMAP.md's M0 is "a forest you can walk through at dusk". Phase 1 built the dusk, Phase
    /// 2 the ground, Phases 3 and 4 the walker and the fifteen trees. Three sample trees is not
    /// a forest, and this is the phase that makes the milestone's own sentence true.
    ///
    /// Planted as TERRAIN TREES rather than as prefab instances, which is the decision this
    /// script exists to carry out. Scene objects would mean ~20,000 GameObjects, each with its
    /// own transform, its own culling entry and its own LODGroup evaluation; terrain trees are
    /// one array of structs on the TerrainData, rendered, culled and LOD-selected by the terrain
    /// system, and they stream with the terrain instead of beside it. The prefabs do not change
    /// shape for this: TreePrototype takes a GameObject, and TreePrototype.navMeshLod's own
    /// documentation distinguishes "Trees with a LODGroup" from "regular Trees", so the
    /// four-level LODGroup Phase 4 built is a supported thing for a terrain tree to have.
    ///
    /// Generated, not painted — D33, the seed is the source. The same seed rebuilds this exact
    /// forest; a different one rebuilds a different one, and nothing is lost either way because
    /// the generator is the artefact, not the output.
    ///
    /// Idempotent: re-running replaces the instance array wholesale. It does not accumulate, and
    /// it does not matter how many times it has been run before.
    /// </summary>
    public static class Phase5Forest
    {
        const string MenuPath = "Dead Pines/Phase 5 — Plant the Forest";

        const string ScenePath = "Assets/Scenes/M0.unity";
        const string TerrainName = "M0_Terrain";
        const string PlayerName = "M0_Player";
        const string PrefabFolder = "Assets/Prefabs/Trees";

        /// Phase 4's sample drop, kept only long enough to look at the prefabs. Three trees
        /// standing in a real forest would read as three trees standing in a real forest.
        const string SampleRootName = "TEMP_TreeSamples";

        /// D33 — the seed is the source.
        const int Seed = 20261015;

        /// One candidate per cell, jittered inside it. This is the density ceiling: the noise
        /// field below only ever removes candidates, so the count can never exceed
        /// (1000 / CellMetres)² and a runaway scatter is not possible. The jitter is held to
        /// 0.4 of a cell so a tree cannot land on top of its neighbour's trunk.
        ///
        /// 4 m rather than something rounder because of what it buys: at this spacing the
        /// average gap between trunks is about 5 m, and that is the number that decides whether
        /// the forest blocks a sightline. A woodland you can see across is a park, and the M0
        /// question is precisely whether this reads as a forest. Too dense is the recoverable
        /// mistake — the two edges below thin it without touching this.
        const float CellMetres = 4f;
        const float CellJitter = 0.4f;

        /// Where a stand ends and a clearing begins, in the noise field's own units. Widening
        /// this pair softens every edge in the forest and raises the count; narrowing it makes
        /// the stands harder and the clearings emptier. These two numbers are the density dial,
        /// and the log reports the trees per hectare they produced so tuning them is not blind.
        const float ClearingEdge = 0.35f;
        const float StandEdge = 0.60f;

        /// Trees do not grow on the rock. Phase 2 puts the moss-and-rock layer on the steep
        /// ground, and a trunk planted on a 40° face stands on one root and floats on the other
        /// — the worse of the two failures, because D35 gives us no shadow to hide the gap.
        const float MaxSlopeDegrees = 30f;

        /// Clear ground around wherever the player starts, so the first thing the walk shows
        /// you is not the inside of a trunk.
        const float SpawnClearanceMetres = 8f;

        static readonly string[] Sizes = { "big", "large", "medium", "small", "sapling" };

        /// The stand's make-up, in the order of Sizes. Mature trees dominate and regeneration
        /// fills the gaps: the shape a real stand has, and also what makes the mixed heights
        /// read as a forest instead of one tree repeated. Saplings are the smallest share
        /// because they are the least visible — 1.5 m of tree is under the eye line at 1.7 m.
        static readonly float[] SizeWeights = { 0.30f, 0.20f, 0.25f, 0.15f, 0.10f };

        /// Per-tree size variation. Deliberately narrow, and applied to height and width
        /// together: the same factor on both axes keeps a tree the shape it was modelled, and a
        /// wide spread would make the LOD transitions pop, because the LODGroup switches on
        /// screen height and a 20% larger tree crosses every threshold at a different distance.
        const float MinScale = 0.85f;
        const float MaxScale = 1.15f;

        /// Phase 1 put fog on ExponentialSquared at 0.0055. At 400 m that is 99.2% fogged — a
        /// tree out there is contributing nothing anybody can see and costing a draw either
        /// way, so this is where the forest stops. It is a derivation, not a taste call, and it
        /// is the single biggest performance number in this file.
        const float TreeDistanceMetres = 400f;

        /// Where a tree stops being a mesh and becomes a billboard.
        const float BillboardDistanceMetres = 70f;

        /// The distance over which the two cross-fade, so nothing snaps.
        const float CrossFadeMetres = 10f;

        /// Trees rendered at full detail at once. See the note in ApplyTreeSettings — Unity's
        /// own documentation scopes this to SpeedTree trees, so it may do nothing here.
        const int MaxFullLodCount = 200;

        [MenuItem(MenuPath, priority = 50)]
        public static void Build()
        {
            var log = new StringBuilder();
            var problems = new List<string>();

            log.AppendLine("=== Dead Pines — Phase 5 forest ===");

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.Log("[Dead Pines] Phase 5 cancelled — the open scene was not saved.");
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

            var prototypes = BuildPrototypes(log, problems);
            if (prototypes == null || prototypes.Count == 0)
            {
                log.AppendLine();
                log.AppendLine("--- COULD NOT BUILD (paste these lines back to Claude) ---");
                log.AppendLine("  !! no tree prefabs found — run Phase 4 first");
                foreach (var p in problems)
                    log.AppendLine("  !! " + p);
                Debug.LogWarning(log.ToString());
                return;
            }

            var instances = Scatter(terrain, scene, log);
            if (instances.Count == 0)
            {
                log.AppendLine();
                log.AppendLine("--- COULD NOT BUILD (paste these lines back to Claude) ---");
                log.AppendLine("  !! the scatter produced nothing — every candidate was rejected");
                foreach (var p in problems)
                    log.AppendLine("  !! " + p);
                Debug.LogWarning(log.ToString());
                return;
            }

            Apply(terrain, prototypes, instances, log);
            ApplyTreeSettings(terrain, log);
            RemoveSamples(scene, log);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();

            Verify(terrain, prototypes, instances, log);

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
                log.AppendLine("--- forest planted ---");
                log.AppendLine();
                log.AppendLine("WHAT TO LOOK AT (I can't see the game — this part is yours):");
                log.AppendLine("  1. Walk into the trees. Does the density feel like a forest, or like");
                log.AppendLine("     a plantation? Too even, or too gappy?");
                log.AppendLine("  2. Look into the distance. Trees should fade into fog, not pop or");
                log.AppendLine("     turn into flat cards you can see the edge of.");
                log.AppendLine("  3. Walk through a trunk. You will — see the note about colliders.");
                Debug.Log(log.ToString());
            }
        }

        // ---------------------------------------------------------------------------------
        // Prototypes
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The fifteen prefabs, as terrain tree prototypes.
        ///
        /// Ordered size-major, variant-minor, so a prototype index divided by three is its size
        /// — which is what lets the scatter pick a size first and a variant second without
        /// carrying a second table around.
        /// </summary>
        static List<TreePrototype> BuildPrototypes(StringBuilder log, List<string> problems)
        {
            log.AppendLine();
            log.AppendLine("[Prototypes]");

            var prototypes = new List<TreePrototype>();

            foreach (var size in Sizes)
            {
                for (int n = 1; n <= 3; n++)
                {
                    string id = $"Pine_{size}_{n}";
                    var path = $"{PrefabFolder}/{id}.prefab";
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);

                    if (prefab == null)
                    {
                        problems.Add($"{path} not found — that variant will not appear in the forest");
                        continue;
                    }

                    var group = prefab.GetComponentInChildren<LODGroup>(true);
                    if (group == null)
                    {
                        // Not fatal, but it means this tree keeps its full mesh at every
                        // distance, which at 400 m of draw distance is the expensive way to
                        // render a tree nobody can see.
                        problems.Add($"{id} has no LODGroup — it will render at full detail at " +
                                     "every distance, and the billboard will never be used");
                    }

                    prototypes.Add(new TreePrototype
                    {
                        prefab = prefab,

                        // Wind bending is a shader feature — the tree shaders read a bend
                        // factor and push vertices. URP Lit does not, so setting this would
                        // promise a sway that never happens. M0's wind is D25's audio layer;
                        // visual wind arrives with a foliage shader, and this is the hook it
                        // will use.
                        bendFactor = 0f,
                    });
                }
            }

            if (prototypes.Count == 0)
                return prototypes;

            // The index arithmetic the scatter depends on. If a prefab was missing above, the
            // list is no longer size-major in threes and the scatter would plant the wrong
            // sizes — so it is checked rather than assumed.
            if (prototypes.Count != Sizes.Length * 3)
            {
                problems.Add($"expected {Sizes.Length * 3} tree prefabs, found {prototypes.Count} — " +
                             "the size-major ordering the scatter relies on does not hold");
                return new List<TreePrototype>();
            }

            log.AppendLine($"  {prototypes.Count} prototypes, {Sizes.Length} sizes × 3 variants");
            log.AppendLine($"  order         {string.Join(", ", Sizes)}");

            return prototypes;
        }

        // ---------------------------------------------------------------------------------
        // The scatter
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Walks a jittered grid over the tile and decides, per cell, what grows there.
        ///
        /// Every rejection is counted and reported. A density dial you cannot see the effect of
        /// is a dial you tune by guessing, and the three reasons a candidate dies here — open
        /// ground, too steep, too close to the player — are each worth a different fix.
        /// </summary>
        static List<TreeInstance> Scatter(Terrain terrain, UnityEngine.SceneManagement.Scene scene,
                                          StringBuilder log)
        {
            var data = terrain.terrainData;
            var origin = terrain.transform.position;
            var size = data.size;

            var rng = new System.Random(Seed);
            var instances = new List<TreeInstance>();

            int cells = Mathf.FloorToInt(size.x / CellMetres);
            int total = cells * cells;
            int clearing = 0, steep = 0, nearPlayer = 0;

            float spawnX = 0f, spawnZ = 0f;
            bool haveSpawn = false;

            var player = FindAnywhere(scene, PlayerName);
            if (player != null)
            {
                spawnX = player.transform.position.x;
                spawnZ = player.transform.position.z;
                haveSpawn = true;
            }

            for (int cz = 0; cz < cells; cz++)
            {
                for (int cx = 0; cx < cells; cx++)
                {
                    // Drawn unconditionally, before any decision, so the sequence a given cell
                    // sees does not depend on how many of its neighbours were rejected. Take
                    // these out of order and the same seed stops rebuilding the same forest.
                    float jx = (float)rng.NextDouble() * 2f - 1f;
                    float jz = (float)rng.NextDouble() * 2f - 1f;
                    float pick = (float)rng.NextDouble();
                    float spin = (float)rng.NextDouble();
                    float scale = Mathf.Lerp(MinScale, MaxScale, (float)rng.NextDouble());

                    float x = origin.x + (cx + 0.5f + jx * CellJitter) * CellMetres;
                    float z = origin.z + (cz + 0.5f + jz * CellJitter) * CellMetres;

                    float u = Mathf.Clamp01((x - origin.x) / size.x);
                    float v = Mathf.Clamp01((z - origin.z) / size.z);

                    // Two octaves: the 180 m one decides where the stands are, the 60 m one
                    // breaks their edges up so they do not read as a contour map.
                    float density = Noise(x, z, 180f, Seed) * 0.6f + Noise(x, z, 60f, Seed) * 0.4f;
                    if (pick > Mathf.InverseLerp(ClearingEdge, StandEdge, density))
                    {
                        clearing++;
                        continue;
                    }

                    if (data.GetSteepness(u, v) > MaxSlopeDegrees)
                    {
                        steep++;
                        continue;
                    }

                    if (haveSpawn)
                    {
                        float dx = x - spawnX, dz = z - spawnZ;
                        if (dx * dx + dz * dz < SpawnClearanceMetres * SpawnClearanceMetres)
                        {
                            nearPlayer++;
                            continue;
                        }
                    }

                    int sizeIndex = PickSize((float)rng.NextDouble());
                    int prototype = sizeIndex * 3 + rng.Next(0, 3);

                    instances.Add(new TreeInstance
                    {
                        // Normalised to the terrain, not world metres. y is a fraction of the
                        // terrain's height range as well as x and z being fractions of its
                        // width — and it is written correctly even though Apply() asks Unity to
                        // snap to the heightmap, because a value that is only right when a flag
                        // is set is a trap for whoever next flips the flag.
                        position = new Vector3(u, Mathf.Clamp01(data.GetInterpolatedHeight(u, v) / size.y), v),
                        prototypeIndex = prototype,

                        // Radians, on the X-Z plane. Unity's own documentation calls this field
                        // read-only, which is the one thing in this file that may quietly not
                        // take — so Verify() reads an instance back and says whether it did.
                        rotation = spin * Mathf.PI * 2f,

                        heightScale = scale,
                        widthScale = scale,

                        // White, and it will stay white: a per-instance tint reaches the shader
                        // only if the shader reads it, and URP Lit does not. Variation has to
                        // come from the three variants and the scale instead.
                        color = Color.white,
                        lightmapColor = Color.white,
                    });
                }
            }

            log.AppendLine();
            log.AppendLine("[Scatter]");
            log.AppendLine($"  candidates    {total}  ({cells}×{cells} cells of {CellMetres} m)");
            log.AppendLine($"  planted       {instances.Count}");
            log.AppendLine($"  open ground   {clearing}  (the density field said no)");
            log.AppendLine($"  too steep     {steep}  (over {MaxSlopeDegrees}°)");
            log.AppendLine($"  spawn area    {nearPlayer}  (within {SpawnClearanceMetres} m of {PlayerName})");

            if (!haveSpawn)
                log.AppendLine($"  note          no {PlayerName} in the scene, so nothing was kept clear");

            float hectares = (size.x * size.z) / 10000f;
            log.AppendLine($"  density       {instances.Count / hectares:F0} trees per hectare over {hectares:F0} ha");

            return instances;
        }

        /// <summary>
        /// Picks a size from SizeWeights. Linear scan rather than a cumulative table built once,
        /// because the table is ten floats and a scan of ten is not worth a field to keep in
        /// step with the array above it.
        /// </summary>
        static int PickSize(float roll)
        {
            float total = 0f;
            foreach (var w in SizeWeights)
                total += w;

            float threshold = roll * total;
            float running = 0f;

            for (int i = 0; i < SizeWeights.Length; i++)
            {
                running += SizeWeights[i];
                if (threshold < running)
                    return i;
            }

            return SizeWeights.Length - 1;
        }

        /// <summary>
        /// Deterministic value noise in 0..1, bilinear across a lattice of the given wavelength.
        ///
        /// Value noise rather than Perlin: this decides where a stand is, and a stand wants soft
        /// blobby regions rather than the ridge-and-valley structure gradient noise is built to
        /// produce. Smoothstep between lattice points so the stands have no visible grid.
        /// </summary>
        static float Noise(float x, float z, float wavelength, int seed)
        {
            float fx = x / wavelength, fz = z / wavelength;
            int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz);

            float tx = fx - x0, tz = fz - z0;
            tx = tx * tx * (3f - 2f * tx);
            tz = tz * tz * (3f - 2f * tz);

            float a = Hash01(x0, z0, seed);
            float b = Hash01(x0 + 1, z0, seed);
            float c = Hash01(x0, z0 + 1, seed);
            float d = Hash01(x0 + 1, z0 + 1, seed);

            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz);
        }

        /// <summary>
        /// Integer hash to 0..1. Not System.Random, because this is indexed by lattice position
        /// rather than consumed in sequence — the same coordinate has to give the same value
        /// however many times it is asked.
        /// </summary>
        static float Hash01(int x, int y, int seed)
        {
            unchecked
            {
                int h = seed;
                h = h * 73856093 ^ x * 19349663;
                h = h * 83492791 ^ y * 297121507;
                h ^= h >> 15;
                h *= 0x2c1b3c6d;
                h ^= h >> 12;
                h *= 0x297a2d39;
                h ^= h >> 15;

                return (h & 0x7fffffff) / (float)0x7fffffff;
            }
        }

        // ---------------------------------------------------------------------------------
        // Application
        // ---------------------------------------------------------------------------------

        static void Apply(Terrain terrain, List<TreePrototype> prototypes,
                          List<TreeInstance> instances, StringBuilder log)
        {
            var data = terrain.terrainData;

            // Prototypes first. An instance whose prototypeIndex points past the end of this
            // array is dropped by Unity without an error, and the forest comes back quietly
            // smaller than the count says.
            data.treePrototypes = prototypes.ToArray();

            // snapToHeightmap: true — Unity places each tree on the heightmap itself, which is
            // the same answer the position field carries but computed against the real surface
            // rather than the interpolated one.
            data.SetTreeInstances(instances.ToArray(), true);

            log.AppendLine();
            log.AppendLine("[Applied]");
            log.AppendLine($"  prototypes    {data.treePrototypes.Length}");
            log.AppendLine($"  instances     {data.treeInstanceCount}");
        }

        /// <summary>
        /// The terrain's own tree rendering settings.
        ///
        /// Unity's documentation scopes treeLODBiasMultiplier and treeMaximumFullLODCount to
        /// "LOD trees (i.e. SpeedTree trees)". Ours are LODGroup prefabs, so those two may be
        /// doing nothing at all here and the LODGroup's own thresholds may be doing all the
        /// work. They are set anyway — they cost nothing, and if the LODGroup turns out not to
        /// govern distance the way it should, one of these is the next lever. The log says what
        /// they are so a later reading is not a guess.
        /// </summary>
        static void ApplyTreeSettings(Terrain terrain, StringBuilder log)
        {
            terrain.treeDistance = TreeDistanceMetres;
            terrain.treeBillboardDistance = BillboardDistanceMetres;
            terrain.treeCrossFadeLength = CrossFadeMetres;
            terrain.treeMaximumFullLODCount = MaxFullLodCount;
            terrain.drawTreesAndFoliage = true;

            // D35 — nothing in this game casts a shadow, and the terrain is the largest thing
            // in it. This is the terrain's own shadow switch, separate from the trees' renderer
            // flags, and it is the one that would quietly re-enable a shadow pass.
            //
            // shadowCastingMode, not castShadows: the old name is deprecated in 6.3 and only
            // warns, so a project can carry it indefinitely and never notice it is dead.
            terrain.shadowCastingMode = ShadowCastingMode.Off;

            log.AppendLine();
            log.AppendLine("[Terrain tree settings]");
            log.AppendLine($"  tree distance     {terrain.treeDistance} m");
            log.AppendLine($"  billboard at      {terrain.treeBillboardDistance} m, cross-fade {terrain.treeCrossFadeLength} m");
            log.AppendLine($"  max full LOD      {terrain.treeMaximumFullLODCount}");
            log.AppendLine($"  terrain shadows   {terrain.shadowCastingMode}  (D35 wants Off)");
        }

        /// <summary>
        /// Phase 4's three sample trees. They existed to prove the prefabs stood up and were the
        /// right size, and they did — but three trees in a forest read as three trees.
        /// </summary>
        static void RemoveSamples(UnityEngine.SceneManagement.Scene scene, StringBuilder log)
        {
            var samples = FindAnywhere(scene, SampleRootName);
            if (samples == null)
                return;

            Object.DestroyImmediate(samples);
            log.AppendLine();
            log.AppendLine($"  removed       {SampleRootName} — the real forest replaces it");
        }

        // ---------------------------------------------------------------------------------
        // Verification
        // ---------------------------------------------------------------------------------

        static void Verify(Terrain terrain, List<TreePrototype> prototypes,
                           List<TreeInstance> instances, StringBuilder log)
        {
            var data = terrain.terrainData;

            log.AppendLine();
            log.AppendLine("[Read back]");

            log.AppendLine($"  prototypes    {data.treePrototypes.Length}  (want {prototypes.Count})");
            log.AppendLine($"  instances     {data.treeInstanceCount}  (want {instances.Count})");

            if (data.treeInstanceCount != instances.Count)
                log.AppendLine("  !! the terrain kept a different number than were handed to it");

            // Per-size counts, read back from the terrain rather than from the list that was
            // written. A prototype index that silently wrapped would show up here as a size
            // with no trees in it and another with twice as many.
            var bySize = new int[Sizes.Length];
            var byPrototype = new int[Mathf.Max(1, data.treePrototypes.Length)];

            for (int i = 0; i < data.treeInstanceCount; i++)
            {
                var t = data.GetTreeInstance(i);
                if (t.prototypeIndex >= 0 && t.prototypeIndex < byPrototype.Length)
                    byPrototype[t.prototypeIndex]++;

                int sizeIndex = t.prototypeIndex / 3;
                if (sizeIndex >= 0 && sizeIndex < bySize.Length)
                    bySize[sizeIndex]++;
            }

            log.AppendLine();
            log.AppendLine("  by size       count    share   asked    variant 1/2/3");
            for (int i = 0; i < Sizes.Length; i++)
            {
                float pct = data.treeInstanceCount > 0 ? 100f * bySize[i] / data.treeInstanceCount : 0f;

                // The variant split is the check that the size-major prototype ordering held.
                // A roll that ran off the end of the list, or a variant that failed to load,
                // shows up here as an uneven split rather than as a forest that merely looks
                // a bit samey.
                var variants = new StringBuilder();
                for (int v = 0; v < 3; v++)
                {
                    int index = i * 3 + v;
                    if (index < byPrototype.Length)
                        variants.Append(byPrototype[index]).Append(v < 2 ? "/" : "");
                }

                log.AppendLine($"    {Sizes[i],-9} {bySize[i],6}   {pct,5:F1}%  {SizeWeights[i] * 100f,5:F0}%    {variants}");
            }

            // The one field Unity's documentation calls read-only. If it is, every tree in the
            // forest faces the same way — which for a radially symmetric pine is nearly
            // invisible, and worth knowing rather than assuming.
            if (data.treeInstanceCount > 0 && instances.Count > 0)
            {
                float wanted = instances[0].rotation;
                float got = data.GetTreeInstance(0).rotation;
                bool honoured = Mathf.Abs(Mathf.DeltaAngle(wanted * Mathf.Rad2Deg, got * Mathf.Rad2Deg)) < 0.5f;

                log.AppendLine();
                log.AppendLine($"  rotation      wrote {wanted:F3} rad, read back {got:F3} rad");
                // Concatenated, not interpolated — see the note in Phase 1. An interpolated
                // string cannot span lines at this project's C# 9.
                log.AppendLine("                " + (honoured
                    ? "honoured — trees face different ways"
                    : "IGNORED — every tree faces the same way, as the docs warned"));
            }

            // The billboard is the part most likely to be wrong, and at 70 m it is also the part
            // that decides whether the distance looks like a forest. It gets reported rather
            // than assumed: a four-vertex mesh is a flat card that a camera-facing shader would
            // have to turn, and URP Lit is not one.
            log.AppendLine();
            log.AppendLine("  billboards    (a flat card needs a billboard shader; a cross does not)");

            foreach (var prototype in prototypes)
            {
                if (prototype == null || prototype.prefab == null)
                    continue;

                var group = prototype.prefab.GetComponentInChildren<LODGroup>(true);
                string levels = group != null ? $"{group.lodCount} levels" : "no LODGroup";
                string last = group != null ? $", lastLODBillboard {group.lastLODBillboard}" : "";

                var billboard = FindAnywhere(prototype.prefab, $"{prototype.prefab.name}_Billboard_LOD3");
                if (billboard == null)
                {
                    log.AppendLine($"    {prototype.prefab.name,-18} {levels}{last} — billboard child NOT FOUND");
                    continue;
                }

                var filter = billboard.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;

                string shape = mesh == null
                    ? "no mesh"
                    : $"{mesh.vertexCount} verts, {mesh.bounds.size.x:F1}×{mesh.bounds.size.y:F1}×{mesh.bounds.size.z:F1} m";

                log.AppendLine($"    {prototype.prefab.name,-18} {levels}{last} — {shape}");
            }
        }

        // ---------------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------------

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
        /// Finds a GameObject at any depth. M0_Player is a root object today, but the camera
        /// became a child the moment Phase 3 ran, and this is the same mistake Phase 1 made.
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

        static GameObject FindAnywhere(GameObject root, string name)
        {
            if (root.name == name)
                return root;

            foreach (var child in root.GetComponentsInChildren<Transform>(true))
                if (child.name == name)
                    return child.gameObject;

            return null;
        }
    }
}
