using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeadPines.EditorTools
{
    /// <summary>
    /// Phase 2 of M0 — the terrain: one ~1 km² tile, heightmap 1025, four layers on one
    /// splatmap. TECHNICAL.md §11 "Terrain", sized against §9's table.
    ///
    /// The heightmap is GENERATED, not hand-sculpted, and the generator is a constant in this
    /// file rather than a binary asset — D33: authoring-time deterministic generation, where
    /// the generators are part of the game's source. Change the seed or the octaves and the
    /// landscape changes; change nothing and you get the same landscape forever.
    ///
    /// The splatmap is generated from the same heights, so the surface follows the shape:
    /// mud collects in the hollows, rock shows on the steep ground, leaf litter drifts across
    /// the mid slopes. That is what stops a 1 km² tile reading as one texture repeated.
    ///
    /// Runs with or without the layer textures present. Missing ones fall back to flat colour
    /// so the shape is walkable and judgeable before the art arrives.
    /// </summary>
    public static class Phase2Terrain
    {
        const string MenuPath = "Dead Pines/Phase 2 — Build Terrain";

        const string ScenePath = "Assets/Scenes/M0.unity";
        const string TerrainDataPath = "Assets/Settings/M0_Terrain.asset";
        const string LayerFolder = "Assets/Settings/TerrainLayers";
        const string TextureFolder = "Assets/Art/Terrain";

        // §9 — heightmap resolution must be 2ⁿ+1, and doubling it quadruples memory.
        const int HeightmapResolution = 1025;
        const int AlphamapResolution = 1024;
        const int DetailResolution = 1024;

        // §9 — "the one documented link between a Terrain setting and draw call count".
        const int DetailResolutionPerPatch = 16;

        // §9 — ~1 km² per tile.
        const float TileSize = 1000f;

        // 60 m of relief across a kilometre is a 6% average grade: enough that the land has
        // a high side and a low side, gentle enough to walk. Not decoration — it is what
        // gives mud somewhere to collect and the lookout tower somewhere to go (D27, D28).
        const float MaxHeight = 60f;

        const int NoiseSeed = 20261014;
        const float BaseFrequency = 2.5f;
        const int Octaves = 5;

        [MenuItem(MenuPath, priority = 20)]
        public static void Build()
        {
            var log = new StringBuilder();
            var problems = new List<string>();

            log.AppendLine("=== Dead Pines — Phase 2 terrain ===");

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.Log("[Dead Pines] Phase 2 cancelled — the open scene was not saved.");
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

            var data = LoadOrCreateTerrainData(log);
            if (data == null)
            {
                problems.Add("could not create the TerrainData asset");
                Debug.LogWarning(log.ToString());
                return;
            }

            var heights = GenerateHeights(log);
            var slopes = GenerateSlopes(heights);
            var layers = BuildLayers(log, problems);

            ApplyTerrainData(data, heights, slopes, layers, log);
            var terrain = PlaceTerrain(scene, data, log);
            ApplyTerrainSettings(terrain, log);
            RemoveScaffolding(scene, log);
            DropCameraOntoTerrain(scene, data, log);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();

            Verify(terrain, data, log);

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
                log.AppendLine("--- terrain built ---");
                log.AppendLine();
                log.AppendLine("WHAT TO LOOK AT:");
                log.AppendLine("  1. Does the ground read as a forest floor, or as a texture on a hill?");
                log.AppendLine("  2. Walk the gradient — is the 6% average grade comfortable?");
                log.AppendLine("  3. Do the layer transitions look deliberate, or like a smudge?");
                Debug.Log(log.ToString());
            }
        }

        // ---------------------------------------------------------------------------------
        // Generation — D33
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Fractal noise, normalised to fill 0..1. The normalisation pass is not cosmetic:
        /// summing octaves pulls the distribution toward the mean, so raw fBm in 0..1 only
        /// ever occupies the middle of the range and the terrain comes out looking ironed.
        /// </summary>
        static float[,] GenerateHeights(StringBuilder log)
        {
            var heights = new float[HeightmapResolution, HeightmapResolution];
            float min = float.MaxValue, max = float.MinValue;

            try
            {
                for (int y = 0; y < HeightmapResolution; y++)
                {
                    if ((y & 127) == 0)
                        EditorUtility.DisplayProgressBar("Dead Pines — terrain",
                            $"Generating heightmap {y}/{HeightmapResolution}", y / (float)HeightmapResolution);

                    float v = y / (float)(HeightmapResolution - 1);

                    for (int x = 0; x < HeightmapResolution; x++)
                    {
                        float u = x / (float)(HeightmapResolution - 1);

                        float sum = 0f, amplitude = 1f, frequency = BaseFrequency, total = 0f;
                        for (int octave = 0; octave < Octaves; octave++)
                        {
                            float offset = NoiseSeed * 0.0001f + octave * 13.37f;
                            sum += amplitude * Mathf.PerlinNoise(offset + u * frequency, offset + v * frequency);
                            total += amplitude;
                            amplitude *= 0.5f;
                            frequency *= 2f;
                        }

                        // A single broad slope on top of the noise, so the tile has a high side
                        // and a low side instead of being a uniform field of bumps.
                        float broad = u * 0.6f + v * 0.4f;
                        float value = 0.7f * (sum / total) + 0.3f * broad;

                        heights[y, x] = value;
                        if (value < min) min = value;
                        if (value > max) max = value;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            float span = Mathf.Max(max - min, 1e-5f);
            for (int y = 0; y < HeightmapResolution; y++)
                for (int x = 0; x < HeightmapResolution; x++)
                    heights[y, x] = (heights[y, x] - min) / span;

            log.AppendLine();
            log.AppendLine("[Heightmap]");
            log.AppendLine($"  resolution    {HeightmapResolution}²  (§9 requires 2ⁿ+1)");
            log.AppendLine($"  relief        {MaxHeight} m over {TileSize} m  ({MaxHeight / TileSize * 100f:F1}% average grade)");
            log.AppendLine($"  seed          {NoiseSeed}, {Octaves} octaves, base frequency {BaseFrequency}");
            log.AppendLine("  generated, not sculpted — D33. The seed is the source.");

            return heights;
        }

        /// <summary>
        /// Rise over run, in metres per metre, at alphamap resolution, scaled so the steepest
        /// texel in the tile reads 1.0.
        ///
        /// The splat thresholds that consume this are percentiles of the result — see
        /// ValueAtFraction — so this scaling no longer anchors their meaning the way it did
        /// when those thresholds were fixed numbers. A max is a single texel and moves with
        /// the seed; it was never the stable anchor it looked like.
        /// </summary>
        static float[,] GenerateSlopes(float[,] heights)
        {
            var slopes = new float[AlphamapResolution, AlphamapResolution];
            float texel = TileSize / (HeightmapResolution - 1);
            float scale = HeightmapResolution / (float)AlphamapResolution;
            float maxSlope = 1e-5f;

            for (int y = 0; y < AlphamapResolution; y++)
            {
                int hy = Mathf.Clamp(Mathf.RoundToInt(y * scale), 0, HeightmapResolution - 1);
                int y0 = Mathf.Max(0, hy - 1), y1 = Mathf.Min(HeightmapResolution - 1, hy + 1);
                float run = (y1 - y0) * texel;

                for (int x = 0; x < AlphamapResolution; x++)
                {
                    int hx = Mathf.Clamp(Mathf.RoundToInt(x * scale), 0, HeightmapResolution - 1);
                    int x0 = Mathf.Max(0, hx - 1), x1 = Mathf.Min(HeightmapResolution - 1, hx + 1);
                    float runX = (x1 - x0) * texel;

                    float dx = runX > 0f ? (heights[hy, x1] - heights[hy, x0]) * MaxHeight / runX : 0f;
                    float dy = run > 0f ? (heights[y1, hx] - heights[y0, hx]) * MaxHeight / run : 0f;
                    float slope = Mathf.Sqrt(dx * dx + dy * dy);

                    slopes[y, x] = slope;
                    if (slope > maxSlope) maxSlope = slope;
                }
            }

            for (int y = 0; y < AlphamapResolution; y++)
                for (int x = 0; x < AlphamapResolution; x++)
                    slopes[y, x] /= maxSlope;

            return slopes;
        }

        /// <summary>
        /// Four weights per texel, normalised to sum to one. Layer order has to match
        /// LayerSpecs: 0 forest floor, 1 leaf litter, 2 mud, 3 moss and rock.
        ///
        /// Each rule multiplies by (1 - whatever already claimed the texel), so the layers
        /// compete rather than stack — rock wins on steep ground, mud takes the hollows that
        /// rock did not take, litter drifts over what is left, and bare floor is the
        /// remainder. Blending them additively would let three layers each reach 1.0 and the
        /// normalisation would quietly flatten the result to a third each.
        /// </summary>
        static float[,,] GenerateSplats(float[,] heights, float[,] slopes)
        {
            var splats = new float[AlphamapResolution, AlphamapResolution, 4];
            float scale = HeightmapResolution / (float)AlphamapResolution;

            // Band edges are read off the distributions instead of being fixed on the 0..1
            // range. See ValueAtFraction for what the fixed ones cost.
            float mudEdge    = ValueAtFraction(heights, 0.28f);
            float mudPeak    = ValueAtFraction(heights, 0.08f);
            float litterOn   = ValueAtFraction(heights, 0.48f);
            float litterFull = ValueAtFraction(heights, 0.80f);
            float rockOn     = ValueAtFraction(slopes, 0.70f);
            float rockFull   = ValueAtFraction(slopes, 0.90f);

            for (int y = 0; y < AlphamapResolution; y++)
            {
                for (int x = 0; x < AlphamapResolution; x++)
                {
                    int hx = Mathf.Clamp(Mathf.RoundToInt(x * scale), 0, HeightmapResolution - 1);
                    int hy = Mathf.Clamp(Mathf.RoundToInt(y * scale), 0, HeightmapResolution - 1);

                    float height = heights[hy, hx];
                    float slope = slopes[y, x];

                    float rock = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(rockOn, rockFull, slope));
                    float mud = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(mudEdge, mudPeak, height)) * (1f - rock);
                    float litter = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(litterOn, litterFull, height))
                                   * (1f - rock) * (1f - mud);
                    float floor = Mathf.Max(0f, 1f - rock - mud - litter);

                    float sum = rock + mud + litter + floor;
                    if (sum < 1e-5f) sum = 1f;

                    splats[y, x, 0] = floor / sum;
                    splats[y, x, 1] = litter / sum;
                    splats[y, x, 2] = mud / sum;
                    splats[y, x, 3] = rock / sum;
                }
            }

            return splats;
        }

        /// <summary>
        /// The value at a given fraction of the distribution — ValueAtFraction(v, 0.5f) is the
        /// median.
        ///
        /// The splat bands have to be anchored here rather than to the 0..1 range. Normalising
        /// the heightmap so it fills 0..1 guarantees the extremes reach 0 and 1, but it does not
        /// spread the mass: fBm bunches it toward the middle, and on this terrain the median
        /// sits at 0.49. The first build fixed the litter band at 0.20..0.62, which is centred
        /// on that median, and litter took 73% of the tile while mud — which needed ground below
        /// 0.40, where almost no mass lives — got 5.9%.
        ///
        /// A percentile says "the lowest quarter of this ground", which stays true whatever the
        /// seed or the relief. A max, which is what the slope normalisation uses, is one noisy
        /// texel and is not stable in the same way.
        /// </summary>
        static float ValueAtFraction(float[,] values, float fraction)
        {
            const int Bins = 1024;
            var histogram = new int[Bins];

            int size = values.GetLength(0);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int bin = Mathf.Clamp((int)(values[y, x] * Bins), 0, Bins - 1);
                    histogram[bin]++;
                }

            int target = Mathf.Max(1, Mathf.RoundToInt(size * size * Mathf.Clamp01(fraction)));
            int running = 0;

            for (int bin = 0; bin < Bins; bin++)
            {
                running += histogram[bin];
                if (running >= target)
                    return (bin + 1) / (float)Bins;
            }

            return 1f;
        }

        // ---------------------------------------------------------------------------------
        // Layers
        // ---------------------------------------------------------------------------------

        struct LayerSpec
        {
            public string Id;          // the Poly Haven asset id, and the filename stem
            public string Label;
            public float TileMetres;   // world size of one texture repeat
            public Color Fallback;
        }

        static readonly LayerSpec[] LayerSpecs =
        {
            new LayerSpec { Id = "forest_ground_04", Label = "forest floor",  TileMetres = 16f, Fallback = new Color(0.20f, 0.17f, 0.12f) },
            new LayerSpec { Id = "forest_leaves_04", Label = "leaf litter",   TileMetres = 12f, Fallback = new Color(0.26f, 0.19f, 0.11f) },
            new LayerSpec { Id = "brown_mud_02",     Label = "mud",           TileMetres = 14f, Fallback = new Color(0.14f, 0.11f, 0.08f) },
            new LayerSpec { Id = "mossy_rock",       Label = "moss and rock", TileMetres = 20f, Fallback = new Color(0.19f, 0.19f, 0.16f) },
        };

        static TerrainLayer[] BuildLayers(StringBuilder log, List<string> problems)
        {
            log.AppendLine();
            log.AppendLine("[Terrain layers]");

            if (!AssetDatabase.IsValidFolder(LayerFolder))
                AssetDatabase.CreateFolder("Assets/Settings", "TerrainLayers");

            var layers = new TerrainLayer[LayerSpecs.Length];

            for (int i = 0; i < LayerSpecs.Length; i++)
            {
                var spec = LayerSpecs[i];
                var path = $"{LayerFolder}/Layer_{i}_{spec.Label.Replace(' ', '_')}.asset";

                var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
                bool created = layer == null;
                if (created)
                {
                    layer = new TerrainLayer();
                    AssetDatabase.CreateAsset(layer, path);
                }

                var diffuse = FindTexture(spec.Id, "_diff");
                var normal = FindTexture(spec.Id, "_nor_gl");

                layer.diffuseTexture = diffuse;
                layer.normalMapTexture = normal;
                layer.normalScale = 1f;
                layer.tileSize = new Vector2(spec.TileMetres, spec.TileMetres);
                // Wet forest floor. Almost no specular — this is the surface that would break
                // the overcast read fastest if it started catching highlights.
                layer.smoothness = 0.05f;
                layer.metallic = 0f;

                if (diffuse == null)
                {
                    // With no diffuse map the shader samples white, so the remap maximum is
                    // what the layer reads as. Flat colour, but the shape stays judgeable.
                    layer.diffuseRemapMax = spec.Fallback;
                    problems.Add($"{spec.Label}: no texture in {TextureFolder} matching '{spec.Id}*_diff*' — flat colour for now");
                }

                EditorUtility.SetDirty(layer);
                layers[i] = layer;

                log.AppendLine($"  {i}  {spec.Label,-15} {spec.TileMetres,4:F0} m tiles  " +
                               $"{(diffuse != null ? diffuse.name : "FLAT COLOUR")}" +
                               $"{(normal != null ? " + normal" : "")}");
            }

            return layers;
        }

        static Texture2D FindTexture(string id, string mapSuffix)
        {
            if (!AssetDatabase.IsValidFolder(TextureFolder))
                return null;

            // Confined to TextureFolder on purpose. An unscoped FindAssets is what wrote into
            // an immutable URP package asset in Phase 0.
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { TextureFolder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var name = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                if (name.Contains(id.ToLowerInvariant()) && name.Contains(mapSuffix))
                    return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }

            return null;
        }

        // ---------------------------------------------------------------------------------
        // Assembly
        // ---------------------------------------------------------------------------------

        static TerrainData LoadOrCreateTerrainData(StringBuilder log)
        {
            var data = AssetDatabase.LoadAssetAtPath<TerrainData>(TerrainDataPath);
            if (data != null)
                return data;

            data = new TerrainData();
            AssetDatabase.CreateAsset(data, TerrainDataPath);
            log.AppendLine();
            log.AppendLine($"[TerrainData]  created {TerrainDataPath}");
            return data;
        }

        static void ApplyTerrainData(TerrainData data, float[,] heights, float[,] slopes,
                                     TerrainLayer[] layers, StringBuilder log)
        {
            // Resolution and size have to be settled before the heights go in — SetHeights
            // resamples against whatever the resolution currently is.
            data.heightmapResolution = HeightmapResolution;
            data.alphamapResolution = AlphamapResolution;
            // Detail resolution is the one size that is not a settable property: both halves
            // are get-only, because the two numbers constrain each other (resolution must
            // divide evenly into patches) and Unity will not let them drift apart. It is a
            // paired method call instead.
            data.SetDetailResolution(DetailResolution, DetailResolutionPerPatch);
            data.size = new Vector3(TileSize, MaxHeight, TileSize);

            // Layers go on before the data, not after. SetAlphamaps sizes the array it will
            // accept from the layer count the TerrainData has at that moment — assign the
            // layers second and it asks for a zero-channel map and rejects the real one.
            data.terrainLayers = layers;

            data.SetHeights(0, 0, heights);

            var splats = GenerateSplats(heights, slopes);
            data.SetAlphamaps(0, 0, splats);

            EditorUtility.SetDirty(data);
            ReportLayerCoverage(splats, layers, log);
        }

        /// <summary>
        /// Average weight per layer across the whole tile. A layer that never wins a texel is a
        /// silent failure — the terrain still renders, it just quietly renders as three
        /// materials instead of four, and nothing anywhere says so. From a screenshot the
        /// layers are indistinguishable anyway: at 16 m tiles seen from 27 m up, every one of
        /// them is a couple of pixels and they average into a single brown. These four numbers
        /// are the only way to see the splatmap from outside it.
        /// </summary>
        static void ReportLayerCoverage(float[,,] splats, TerrainLayer[] layers, StringBuilder log)
        {
            int size = splats.GetLength(0);
            var totals = new double[splats.GetLength(2)];

            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    for (int l = 0; l < totals.Length; l++)
                        totals[l] += splats[y, x, l];

            log.AppendLine();
            log.AppendLine("[Layer coverage]  mean weight per texel");

            double texels = (double)size * size;
            for (int l = 0; l < totals.Length; l++)
            {
                string label = l < layers.Length && layers[l] != null
                    ? layers[l].name
                    : $"layer {l}";

                log.AppendLine($"  {l}  {label,-30} {totals[l] / texels * 100.0:F1}%");
            }
        }

        static Terrain PlaceTerrain(UnityEngine.SceneManagement.Scene scene, TerrainData data, StringBuilder log)
        {
            log.AppendLine();
            log.AppendLine("[Terrain]");

            var go = FindInScene(scene, "M0_Terrain");
            Terrain terrain;

            if (go == null)
            {
                go = Terrain.CreateTerrainGameObject(data);
                go.name = "M0_Terrain";
                terrain = go.GetComponent<Terrain>();
            }
            else
            {
                terrain = go.GetComponent<Terrain>();
                if (terrain == null)
                    terrain = go.AddComponent<Terrain>();
                terrain.terrainData = data;
            }

            // Centred on the origin, so the camera Phase 1 left at (0, 1.7, 0) is standing in
            // the middle of the tile rather than on a corner.
            go.transform.position = new Vector3(-TileSize * 0.5f, 0f, -TileSize * 0.5f);

            log.AppendLine($"  size          {TileSize} × {TileSize} m, {MaxHeight} m of relief");
            log.AppendLine($"  origin        {go.transform.position}  (tile centred on world zero)");

            return terrain;
        }

        static void ApplyTerrainSettings(Terrain terrain, StringBuilder log)
        {
            log.AppendLine();
            log.AppendLine("[Terrain settings]");

            // §9 — pixel error 3–5. Higher is cheaper and coarser.
            terrain.heightmapPixelError = 4f;
            terrain.basemapDistance = 800f;

            // §11 — grass detail distance 15–30 m, and §9 calls this "the real terrain cost,
            // not geometry". drawInstanced is a hard requirement for instanced details and
            // for GPU instancing on terrain in general.
            terrain.detailObjectDistance = 25f;
            terrain.detailObjectDensity = 1f;
            terrain.drawInstanced = true;

            // §9 — tree distance 150–250 m, billboards start 80–120 m, narrow fade band
            // because cross-fade renders both LODs at once and a wide band is where a 2017
            // GPU hitches.
            terrain.treeDistance = 200f;
            terrain.treeBillboardDistance = 100f;
            terrain.treeCrossFadeLength = 5f;
            terrain.treeMaximumFullLODCount = 300;

            // D35 — nothing in this game casts a shadow, terrain included.
            terrain.shadowCastingMode = ShadowCastingMode.Off;
            terrain.reflectionProbeUsage = ReflectionProbeUsage.Off;

            log.AppendLine($"  pixel error   {terrain.heightmapPixelError}  (§9: 3–5)");
            log.AppendLine($"  detail dist   {terrain.detailObjectDistance} m  (§11: 15–30 m)");
            log.AppendLine($"  tree dist     {terrain.treeDistance} m, billboard at {terrain.treeBillboardDistance} m");
            log.AppendLine($"  instanced     {terrain.drawInstanced}");
            log.AppendLine($"  shadows       {terrain.shadowCastingMode}  (D35)");
        }

        /// <summary>
        /// The Phase 1 ground plane existed to give fog and ambient something to act on.
        /// Now there is terrain, so it is not just redundant — it would z-fight with the new
        /// ground and hide it.
        /// </summary>
        static void RemoveScaffolding(UnityEngine.SceneManagement.Scene scene, StringBuilder log)
        {
            var plane = FindInScene(scene, "TEMP_GroundPlane");
            if (plane == null)
                return;

            Object.DestroyImmediate(plane);
            log.AppendLine();
            log.AppendLine("[Scaffolding]  TEMP_GroundPlane removed — terrain replaces it");
        }

        static void DropCameraOntoTerrain(UnityEngine.SceneManagement.Scene scene, TerrainData data, StringBuilder log)
        {
            var camera = FindInScene(scene, "M0_Camera");
            if (camera == null)
                return;

            // Phase 1 placed the camera at a fixed 1.7 m over a flat plane at y = 0. The
            // ground is no longer flat, so 1.7 m is now measured from somewhere arbitrary.
            float ground = data.GetInterpolatedHeight(0.5f, 0.5f);
            var position = camera.transform.position;
            camera.transform.position = new Vector3(position.x, ground + 1.7f, position.z);

            log.AppendLine();
            log.AppendLine($"[Camera]  dropped onto terrain at y = {camera.transform.position.y:F2} " +
                           $"(ground {ground:F2} + 1.70 eye height)");
        }

        // ---------------------------------------------------------------------------------
        // Verification
        // ---------------------------------------------------------------------------------

        static void Verify(Terrain terrain, TerrainData data, StringBuilder log)
        {
            log.AppendLine();
            log.AppendLine("[Read back]");

            if (terrain == null)
            {
                log.AppendLine("  terrain       MISSING");
                return;
            }

            log.AppendLine($"  heightmap     {data.heightmapResolution}²  (§9 wants 1025)");
            log.AppendLine($"  alphamap      {data.alphamapResolution}²  (4 layers, one splatmap)");
            log.AppendLine($"  detail        {data.detailResolution}, {data.detailResolutionPerPatch} per patch  (§9 wants 16)");
            log.AppendLine($"  layers        {data.terrainLayers.Length}");
            log.AppendLine($"  size          {data.size}");
            log.AppendLine($"  pixel error   {terrain.heightmapPixelError}");
        }

        static GameObject FindInScene(UnityEngine.SceneManagement.Scene scene, string name)
        {
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == name)
                    return root;
            return null;
        }
    }
}
