using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeadPines.EditorTools
{
    /// <summary>
    /// Phase 4 of M0 — the trees.
    ///
    /// ROADMAP.md's M0 is "a forest you can walk through at dusk", so this is the content the
    /// milestone is named for. It takes the CC BY tree pack in Assets/Art/Trees and turns it into
    /// something the terrain can plant: two materials, fifteen prefabs with LOD chains, and a few
    /// sample trees standing next to the player so the look can be judged before a forest is
    /// committed to.
    ///
    /// The pack is deliberately NOT re-authored. The FBX, the meshes and the textures are the
    /// creator's work and stay as they are — that is both correct and the simplest way to keep
    /// the CC BY "indicate changes" obligation honest. Everything this file makes is either a
    /// new file (a packed texture, a material, a prefab) or a note in SOURCES.md.
    ///
    /// Idempotent: every asset is loaded if it exists and created if it does not, so re-running
    /// re-applies settings rather than duplicating.
    /// </summary>
    public static class Phase4Trees
    {
        const string MenuPath = "Dead Pines/Phase 4 — Build Trees";

        const string ScenePath = "Assets/Scenes/M0.unity";
        const string PlayerName = "M0_Player";

        const string TreeFolder = "Assets/Art/Trees";
        const string TextureFolder = TreeFolder + "/Textures";
        const string DerivedFolder = TreeFolder + "/Derived";
        const string MeshPath = TreeFolder + "/Pine_pack.fbx";

        const string MaterialFolder = "Assets/Settings/TreeMaterials";
        const string PrefabFolder = "Assets/Prefabs/Trees";

        // The child of each prefab root that carries the FBX node's rotation and scale. Named
        // rather than inlined because it is a structural contract: anything that later reaches
        // into a tree prefab — a wind shader, a collider fitter — needs to know the root is an
        // identity pivot and this is the node with the transform on it.
        const string PivotName = "Model";

        // The three the sample drop uses: one of each extreme, so the first screenshot shows the
        // size range rather than three variations on the same tree.
        static readonly string[] SampleTrees = { "Pine_big_1", "Pine_medium_2", "Pine_sapling_1" };

        static readonly string[] Sizes = { "big", "large", "medium", "small", "sapling" };

        // Where each sample tree stands, in metres from the player. Close enough to see the
        // needles, far enough that the LOD switch is visible when you walk toward it.
        static readonly Vector3[] SampleOffsets =
        {
            new Vector3(0f, 0f, 14f),
            new Vector3(-7f, 0f, 22f),
            new Vector3(8f, 0f, 26f),
        };

        // LOD screen-relative transition heights — the fraction of the screen's height the tree
        // covers at the point the level changes. These are first guesses and are meant to be
        // tuned by walking: M0's terrain draws trees to 200 m and billboards from 100 m out
        // (Phase 2), so the chain has to have handed over to the billboard by 100 m.
        static readonly float[] LodHeights = { 0.30f, 0.12f, 0.04f, 0.005f };

        [MenuItem(MenuPath, priority = 40)]
        public static void Build()
        {
            var log = new StringBuilder();
            var problems = new List<string>();

            log.AppendLine("=== Dead Pines — Phase 4 trees ===");

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.Log("[Dead Pines] Phase 4 cancelled — the open scene was not saved.");
                return;
            }

            if (!File.Exists(MeshPath))
            {
                log.AppendLine();
                log.AppendLine($"  !! {MeshPath} not found. The tree pack is not in the project.");
                Debug.LogWarning(log.ToString());
                return;
            }

            var meshes = LoadMeshes(log, problems);
            if (meshes.Count == 0)
            {
                Debug.LogWarning(log.ToString());
                return;
            }

            // The model's transform hierarchy, not just its meshes. This pack stores the unit
            // conversion as a x100 scale on each tree's root node, so the meshes on their own are
            // a hundredth of a tree — and reading only the meshes is what made the first version
            // of this script build 8 cm pines.
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(MeshPath);
            if (model == null)
            {
                problems.Add($"{MeshPath} has no model prefab — is it imported as a Model?");
                Debug.LogWarning(log.ToString());
                return;
            }

            ReportTreeScale(model, log, problems);

            var bark = BuildBarkMaterial(log, problems);
            var cluster = BuildClusterMaterial(log, problems);

            var prefabs = BuildPrefabs(model, meshes, bark, cluster, log, problems);

            if (prefabs.Count > 0)
                DropSamples(prefabs, log, problems);

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
                log.AppendLine("--- trees built ---");
                Debug.Log(log.ToString());
            }
        }

        // ---------------------------------------------------------------------------------
        // Meshes
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Pulls every Mesh out of the FBX. One FBX holds all sixty — fifteen trees with four
        /// levels each — so meshes are matched by name rather than by hierarchy position.
        /// </summary>
        static Dictionary<string, Mesh> LoadMeshes(StringBuilder log, List<string> problems)
        {
            var found = new Dictionary<string, Mesh>();

            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(MeshPath))
            {
                if (asset is Mesh mesh)
                    found[mesh.name] = mesh;
            }

            log.AppendLine();
            log.AppendLine("[Meshes]");
            log.AppendLine($"  {MeshPath}");
            log.AppendLine($"  {found.Count} mesh assets");

            if (found.Count == 0)
                problems.Add($"no meshes inside {MeshPath} — is the FBX imported as a Model?");

            return found;
        }

        /// <summary>
        /// Logs how big the trees actually are: mesh extent, the scale above it, and the product.
        ///
        /// The product is the only one of those that answers the question, and this pack is the
        /// reason why. Its FBX declares centimetre units and then carries the conversion as a
        /// x100 scale on each tree's root node, so a mesh extent of 0.08 is a hundredth of a
        /// tree. The first version of this measured the extent alone and reported every pine as
        /// centimetres tall — the FBX was never wrong, the reading was.
        ///
        /// The scale is accumulated by walking the parent chain rather than read from
        /// lossyScale, because this is a prefab ASSET and not an instantiated object — there is
        /// no world matrix for Unity to have computed a lossy scale from.
        ///
        /// The heights are not in the order the names suggest: Pine_large is twice Pine_big. That
        /// is the pack's own labelling, confirmed against the node scales in the FBX, and not a
        /// sign of anything wrong.
        ///
        /// These are the numbers BuildPrefabs has to reproduce. A tree that measures 16 m here
        /// and 16 cm in the scene means the node scale got dropped in between — which is exactly
        /// the bug this now guards, and exactly what happened the first time.
        /// </summary>
        static void ReportTreeScale(GameObject model, StringBuilder log, List<string> problems)
        {
            log.AppendLine();
            log.AppendLine("[Scale]  height, node scale and rotation, read from the FBX");

            int checkedCount = 0, plausible = 0, multiSubmesh = 0, offset = 0;

            foreach (var node in model.GetComponentsInChildren<Transform>(true))
            {
                if (!node.name.EndsWith("_LOD0"))
                    continue;

                var filter = node.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                    continue;

                var m = AccumulatedTransform(node);
                float height = TransformedHeight(filter.sharedMesh.bounds, m, out float baseY);
                Vector3 rotation = m.rotation.eulerAngles;

                checkedCount++;
                if (height >= 1f && height <= 60f)
                    plausible++;
                if (filter.sharedMesh.subMeshCount > 1)
                    multiSubmesh++;
                if (node.localPosition.sqrMagnitude > 0.0001f)
                    offset++;

                log.AppendLine($"  {node.name,-24} {height,7:F2} m   base {baseY,6:F2}   " +
                               $"×{m.lossyScale.y,6:F3}   " +
                               $"rot ({rotation.x,5:F1}, {rotation.y,4:F1}, {rotation.z,4:F1})   " +
                               $"{filter.sharedMesh.subMeshCount} submesh(es)");
            }

            log.AppendLine($"  {plausible}/{checkedCount} in the 1–60 m band, " +
                           $"{multiSubmesh} with more than one submesh");

            if (checkedCount == 0)
            {
                problems.Add("found no node named *_LOD0 in the model hierarchy — the meshes are " +
                             "there but their transforms are named differently, so nothing was measured");
                return;
            }

            // The submesh count is the other thing that decides whether this looks right: the
            // prefabs put bark on submesh 0 and needles on 1+, and if every tree reports a single
            // submesh then the trunk and the foliage are sharing one material and one of the two
            // is wrong.
            if (checkedCount > 0 && multiSubmesh == 0)
                problems.Add("no LOD0 mesh has more than one submesh — trunk and needles cannot " +
                             "both be right with a single material each");

            // Translations are dropped when the prefab is built — see BuildPrefabs, where the
            // reasons are. That is right for the tree root's placement and for the LOD lineup, but
            // it would be wrong for an LOD0 genuinely sitting away from its tree's origin, because
            // LOD0 is the mesh whose base becomes the trunk base. So this measures LOD0's own
            // offset rather than the tree's, and every node in this pack reads zero.
            if (offset > 0)
                problems.Add($"{offset} of {checkedCount} LOD0 nodes sit away from their tree's " +
                             "own origin — the prefab drops that offset, so those trees would be " +
                             "planted with the trunk base away from the root");

            if (checkedCount > 0 && plausible == 0)
                problems.Add("no LOD0 tree lands in the 1–60 m band even with the node scale " +
                             "applied — if the importer's Scale Factor was changed, the prefabs " +
                             "are wrong by the same factor");
        }

        /// <summary>
        /// The transform called <paramref name="name"/> anywhere in the model, or null.
        ///
        /// Searched by name at any depth rather than by path, because the FBX's own nesting is an
        /// exporter detail — today the tree roots sit directly under the scene root, and that is
        /// not a promise worth encoding.
        /// </summary>
        static Transform FindNode(GameObject model, string name)
        {
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
                if (t.name == name)
                    return t;

            return null;
        }

        /// <summary>
        /// A node's transform relative to the model root, composed by walking up.
        ///
        /// Transform.position, .rotation and .lossyScale are world-space, and a prefab ASSET has
        /// no world for Unity to have computed them against — so they cannot be read directly
        /// here. Multiplying local TRS up the chain is what the hierarchy already means.
        /// </summary>
        static Matrix4x4 AccumulatedTransform(Transform node)
        {
            var m = Matrix4x4.TRS(node.localPosition, node.localRotation, node.localScale);

            for (var t = node.parent; t != null; t = t.parent)
                m = Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale) * m;

            return m;
        }

        /// <summary>
        /// The Y extent of a mesh's bounds once the transform above it is applied, and where its
        /// lowest corner lands.
        ///
        /// Eight corners transformed and spanned, rather than scaling size.y, because a rotation
        /// would make size.y the wrong axis — and a tree measured on the wrong axis reports its
        /// own width as its height, which reads as a plausible number and is not one.
        ///
        /// <paramref name="baseY"/> answers a different question: whether the mesh's pivot is the
        /// trunk base. At zero the tree plants correctly; below zero it sinks into the terrain,
        /// above it floats, and neither is visible in the height alone.
        /// </summary>
        static float TransformedHeight(Bounds bounds, Matrix4x4 m, out float baseY)
        {
            Vector3 min = bounds.min, max = bounds.max;
            float lo = float.MaxValue, hi = float.MinValue;

            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? min.x : max.x,
                                         (i & 2) == 0 ? min.y : max.y,
                                         (i & 4) == 0 ? min.z : max.z);

                float y = m.MultiplyPoint3x4(corner).y;
                if (y < lo) lo = y;
                if (y > hi) hi = y;
            }

            baseY = lo;
            return hi - lo;
        }

        // ---------------------------------------------------------------------------------
        // Materials
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Bark: ordinary opaque URP Lit, base colour plus normal.
        ///
        /// There is deliberately no roughness map. The pack ships one, but URP wants roughness
        /// folded into the alpha of a metallic-gloss texture, and the terrain layers already set
        /// the precedent that M0 uses a scalar smoothness instead (see SOURCES.md, "There is no
        /// mask map"). Bark is a dielectric with a wet-forest sheen and one number describes it.
        /// </summary>
        static Material BuildBarkMaterial(StringBuilder log, List<string> problems)
        {
            var mat = LoadOrCreateMaterial("M_TreeBark", "Universal Render Pipeline/Lit", log);

            AssignTexture(mat, "_BaseMap", TextureFolder + "/Bark_basecolor.png", problems);
            AssignTexture(mat, "_BumpMap", TextureFolder + "/Bark_normal.png", problems);

            mat.SetColor("_BaseColor", Color.white);
            // Nearly matte, like the terrain. D35 means there is no sun to catch a highlight, so
            // a shiny trunk would be the one thing in the frame that looks lit from elsewhere.
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", 0.08f);
            mat.SetFloat("_AlphaClip", 0f);
            mat.SetFloat("_Cull", (float)CullMode.Back);

            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>
        /// Needles: alpha-clipped and two-sided.
        ///
        /// This is where the pack needed real work. Its opacity is a SEPARATE grayscale map, and
        /// URP Lit's alpha clipping reads the albedo's own alpha channel — so the two have to be
        /// combined into one texture first. Without that the foliage renders as solid cards,
        /// which reads as broken rather than as stylised, and nothing anywhere reports an error.
        ///
        /// Two-sided because a needle cluster is single-sided geometry. Left culled, roughly half
        /// the foliage vanishes depending on which way you approach the tree from.
        /// </summary>
        static Material BuildClusterMaterial(StringBuilder log, List<string> problems)
        {
            var mat = LoadOrCreateMaterial("M_TreeCluster", "Universal Render Pipeline/Lit", log);

            PackAlbedoWithOpacity(log, problems);
            AssignTexture(mat, "_BaseMap", DerivedFolder + "/Cluster_AlbedoCutout.png", problems);
            AssignTexture(mat, "_BumpMap", TextureFolder + "/Cluster_full_normal.png", problems);

            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", 0.05f);

            // The alpha-clip set. URP's shader reads _AlphaClip to decide whether to discard, but
            // it is the keyword that actually compiles the branch in, so setting the float alone
            // gives a material that silently does not clip.
            mat.SetFloat("_AlphaClip", 1f);
            mat.SetFloat("_Cutoff", 0.33f);
            mat.EnableKeyword("_ALPHATEST_ON");

            mat.SetFloat("_Cull", (float)CullMode.Off);
            mat.renderQueue = (int)RenderQueue.AlphaTest;

            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>
        /// Writes the needle albedo and its opacity into one RGBA texture.
        ///
        /// Read straight off disk with ImageConversion rather than through the texture importer,
        /// so this does not depend on the sources being marked Read/Write Enabled — which would
        /// otherwise cost runtime memory on every one of these textures forever, purely so a
        /// build-time step could read them once.
        /// </summary>
        static void PackAlbedoWithOpacity(StringBuilder log, List<string> problems)
        {
            const string OutPath = DerivedFolder + "/Cluster_AlbedoCutout.png";

            if (!AssetDatabase.IsValidFolder(DerivedFolder))
                AssetDatabase.CreateFolder(TreeFolder, "Derived");

            var albedo = ReadPng(TextureFolder + "/Cluster_full_basecolor.png");
            var opacity = ReadPng(TextureFolder + "/Cluster_full_Opacity.png");

            if (albedo == null || opacity == null)
            {
                problems.Add("could not read the cluster albedo or opacity source — " +
                             "the needle material will fall back to flat colour");
                return;
            }

            // The two are authored at the same size, but resampling defensively costs nothing and
            // a mismatch here would silently produce a texture with the wrong alpha on it.
            if (albedo.width != opacity.width || albedo.height != opacity.height)
            {
                problems.Add($"cluster albedo is {albedo.width}×{albedo.height} but opacity is " +
                             $"{opacity.width}×{opacity.height} — they must match to be packed");
                return;
            }

            var packed = new Texture2D(albedo.width, albedo.height, TextureFormat.RGBA32, false);
            var albedoPixels = albedo.GetPixels();
            var opacityPixels = opacity.GetPixels();

            for (int i = 0; i < albedoPixels.Length; i++)
            {
                var c = albedoPixels[i];
                // Opacity is grayscale, so any channel carries it. Red is taken rather than
                // luminance because the source is already a single-channel image — averaging
                // three copies of the same number would only cost time.
                c.a = opacityPixels[i].r;
                albedoPixels[i] = c;
            }

            packed.SetPixels(albedoPixels);
            packed.Apply();

            File.WriteAllBytes(OutPath, packed.EncodeToPNG());
            Object.DestroyImmediate(packed);

            AssetDatabase.ImportAsset(OutPath, ImportAssetOptions.ForceUpdate);

            var written = AssetDatabase.LoadAssetAtPath<Texture2D>(OutPath);
            log.AppendLine();
            log.AppendLine("[Derived]");
            log.AppendLine($"  {OutPath}");
            log.AppendLine($"  {albedo.width}×{albedo.height} RGBA — albedo with opacity moved into alpha");
            log.AppendLine($"  written as {written?.width}×{written?.height}, " +
                           $"format handled by TextureImportDefaults ('cutout' → BC7)");

            Object.DestroyImmediate(albedo);
            Object.DestroyImmediate(opacity);
        }

        static Texture2D ReadPng(string path)
        {
            if (!File.Exists(path))
                return null;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (tex.LoadImage(File.ReadAllBytes(path)))
                return tex;

            Object.DestroyImmediate(tex);
            return null;
        }

        static Material LoadOrCreateMaterial(string name, string shaderName, StringBuilder log)
        {
            if (!AssetDatabase.IsValidFolder(MaterialFolder))
                AssetDatabase.CreateFolder("Assets/Settings", "TreeMaterials");

            var path = $"{MaterialFolder}/{name}.asset";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (mat == null)
            {
                mat = new Material(Shader.Find(shaderName));
                AssetDatabase.CreateAsset(mat, path);
                log.AppendLine();
                log.AppendLine($"[Material]  created {path}  ({shaderName})");
            }

            // Re-assigned on every run so a Unity or URP upgrade that loses the shader reference
            // is repaired rather than silently rendering magenta.
            if (mat.shader == null || mat.shader.name != shaderName)
            {
                mat.shader = Shader.Find(shaderName);
                log.AppendLine($"[Material]  {name} shader re-bound to {shaderName}");
            }

            return mat;
        }

        static void AssignTexture(Material mat, string property, string path, List<string> problems)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex == null)
            {
                problems.Add($"{path} not found — {mat.name}.{property} left empty");
                return;
            }

            mat.SetTexture(property, tex);
        }

        // ---------------------------------------------------------------------------------
        // Prefabs
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// One prefab per tree, each a LOD chain: three meshes and a billboard.
        ///
        /// The billboard is a plain quad with the pack's own billboard texture on an unlit
        /// material. It is not Unity's BillboardRenderer, which would build its own atlas from a
        /// rendered view and throw away the one the creator already made.
        /// </summary>
        static List<GameObject> BuildPrefabs(GameObject model, Dictionary<string, Mesh> meshes,
                                             Material bark, Material cluster, StringBuilder log,
                                             List<string> problems)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Prefabs"))
                AssetDatabase.CreateFolder("Assets", "Prefabs");
            if (!AssetDatabase.IsValidFolder(PrefabFolder))
                AssetDatabase.CreateFolder("Assets/Prefabs", "Trees");

            log.AppendLine();
            log.AppendLine("[Prefabs]");

            var prefabs = new List<GameObject>();

            foreach (var size in Sizes)
            {
                for (int n = 1; n <= 3; n++)
                {
                    string id = $"Pine_{size}_{n}";
                    var root = new GameObject(id);

                    // The FBX carries the centimetre-to-metre conversion as a scale on the tree's
                    // root node instead of baking it into the vertices, so a prefab assembled from
                    // meshes alone is a hundredth of a tree. Reading the number off the source
                    // rather than hard-coding 100 means a re-export with different units still
                    // lands at the right size — and this is the line whose absence built 8 cm pines.
                    var node = FindNode(model, id);
                    if (node == null)
                    {
                        problems.Add($"{id}: no node of that name in the FBX — its scale is " +
                                     "unknown, so the prefab would be built at the wrong size");
                        Object.DestroyImmediate(root);
                        continue;
                    }

                    var nodeTransform = AccumulatedTransform(node);
                    Vector3 nodeScale = nodeTransform.lossyScale;

                    if (nodeScale.y <= 0f)
                    {
                        problems.Add($"{id}: its FBX node carries a zero or negative scale");
                        Object.DestroyImmediate(root);
                        continue;
                    }

                    // lossyScale cannot separate a non-uniform scale from a rotation — it has to
                    // pick which axis is which. Uniform x100 is the whole pack, so anything else
                    // is refused rather than quietly approximated into a tree of the wrong shape.
                    if (!Mathf.Approximately(nodeScale.x, nodeScale.y) ||
                        !Mathf.Approximately(nodeScale.y, nodeScale.z))
                    {
                        problems.Add($"{id}: its FBX node scales non-uniformly " +
                                     $"({nodeScale.x:0.###}, {nodeScale.y:0.###}, {nodeScale.z:0.###}) " +
                                     "and also rotates, so the two cannot be separated cleanly");
                        Object.DestroyImmediate(root);
                        continue;
                    }

                    // Rotation and scale live on an inner node, not the prefab root, so the root
                    // stays an identity pivot at the trunk base. Terrain planting, random yaw and
                    // per-tree size variation all want to write to an instance's transform without
                    // fighting a baked -90 degrees.
                    //
                    // That rotation is the whole reason the first build produced a forest lying on
                    // its side: the meshes are Z-up, and this is the node transform that stands
                    // them up. Unity keeps it in the hierarchy rather than baking it into the
                    // vertices, so a prefab assembled from meshes alone loses it.
                    //
                    // Position is dropped, and deliberately. Every node in this pack carries an
                    // authoring offset rather than a meaningful pivot: the tree roots are spread
                    // across the artist's scene 60 m and more apart, and each tree's LODs are
                    // lined up in a row beside it at 20 m intervals so they can be compared. Both
                    // are layout, not geometry — copying either in puts the tree tens of metres
                    // from its own prefab origin, where the sample drop then renders it into
                    // empty fog. LOD0 sits at zero, so dropping the rest lands every mesh back on
                    // the trunk base.
                    var pivot = new GameObject(PivotName);
                    pivot.transform.SetParent(root.transform, false);
                    pivot.transform.localRotation = nodeTransform.rotation;
                    pivot.transform.localScale = nodeScale;

                    var lods = new List<LOD>();

                    bool complete = true;
                    for (int level = 0; level < 3; level++)
                    {
                        if (!meshes.TryGetValue($"{id}_LOD{level}", out var mesh))
                        {
                            problems.Add($"{id}: no mesh named '{id}_LOD{level}' in the FBX");
                            complete = false;
                            break;
                        }

                        var go = new GameObject($"{id}_LOD{level}");
                        go.transform.SetParent(pivot.transform, false);
                        go.AddComponent<MeshFilter>().sharedMesh = mesh;

                        var renderer = go.AddComponent<MeshRenderer>();
                        renderer.sharedMaterials = AssignMaterials(mesh, bark, cluster);

                        // D35 — nothing in this game casts a shadow, trees included.
                        renderer.shadowCastingMode = ShadowCastingMode.Off;
                        renderer.receiveShadows = false;
                        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

                        lods.Add(new LOD { screenRelativeTransitionHeight = LodHeights[level], renderers = new Renderer[] { renderer } });
                    }

                    if (!complete)
                    {
                        Object.DestroyImmediate(root);
                        continue;
                    }

                    var billboard = BuildBillboard(id, meshes, log, problems);
                    if (billboard != null)
                    {
                        billboard.transform.SetParent(pivot.transform, false);
                        lods.Add(new LOD { screenRelativeTransitionHeight = LodHeights[3], renderers = new Renderer[] { billboard.GetComponent<Renderer>() } });
                    }

                    var group = root.AddComponent<LODGroup>();
                    // Zero cross-fade. A wide fade band renders two levels at once, which is the
                    // classic way a forest hitches when you turn around.
                    group.animateCrossFading = false;
                    group.SetLODs(lods.ToArray());
                    group.RecalculateBounds();

                    var path = $"{PrefabFolder}/{id}.prefab";
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    Object.DestroyImmediate(root);

                    prefabs.Add(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                    log.AppendLine($"  {id,-18} {lods.Count} levels, ×{nodeScale.y:0.###} → {path}");
                }
            }

            return prefabs;
        }

        /// <summary>
        /// The pack's meshes carry two submeshes — trunk and needles — and the FBX's embedded
        /// materials say which is which. Rather than trust that mapping, this reads the submesh
        /// count: one submesh is a trunk, two means the second is foliage.
        /// </summary>
        static Material[] AssignMaterials(Mesh mesh, Material bark, Material cluster)
        {
            int count = Mathf.Max(1, mesh.subMeshCount);
            var materials = new Material[count];

            materials[0] = bark;
            for (int i = 1; i < count; i++)
                materials[i] = cluster;

            return materials;
        }

        static GameObject BuildBillboard(string id, Dictionary<string, Mesh> meshes,
                                         StringBuilder log, List<string> problems)
        {
            if (!meshes.TryGetValue($"{id}_Billboard_LOD3", out var mesh))
            {
                problems.Add($"{id}: no billboard mesh — the tree will pop out of existence at distance");
                return null;
            }

            var path = $"{TextureFolder}/{id}_Billboard_Color.png";
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

            var matPath = $"{MaterialFolder}/M_{id}_Billboard.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);

            if (mat == null)
            {
                // Unlit, because a billboard's quad faces the camera rather than the light, so
                // lighting it would make the far forest darker than the near one for no reason.
                mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                AssetDatabase.CreateAsset(mat, matPath);
            }

            if (texture == null)
                problems.Add($"{path} not found — the {id} billboard will render untextured");
            else
                mat.SetTexture("_BaseMap", texture);

            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_AlphaClip", 1f);
            mat.SetFloat("_Cutoff", 0.33f);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.SetFloat("_Cull", (float)CullMode.Off);
            mat.SetFloat("_Surface", 0f);   // opaque + clip, not blended
            mat.renderQueue = (int)RenderQueue.AlphaTest;
            EditorUtility.SetDirty(mat);

            var go = new GameObject($"{id}_Billboard_LOD3");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = mat;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            return go;
        }

        // ---------------------------------------------------------------------------------
        // Sample placement
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Three trees beside the player, so the pack's look can be judged against Phase 2's
        /// terrain before a forest is authored around it.
        ///
        /// These are ordinary GameObjects, not terrain trees. Terrain planting is a separate,
        /// later step — and doing it before the look is settled would mean throwing away a
        /// splatmap's worth of placement decisions if the answer is "these do not fit".
        /// </summary>
        static void DropSamples(List<GameObject> prefabs, StringBuilder log, List<string> problems)
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var terrainGo = FindInScene(scene, "M0_Terrain");
            var player = FindAnywhere(scene, PlayerName);

            if (terrainGo == null || player == null)
            {
                problems.Add($"need both M0_Terrain and {PlayerName} in {ScenePath} to place samples");
                return;
            }

            var terrain = terrainGo.GetComponent<Terrain>();
            var root = FindInScene(scene, "TEMP_TreeSamples");
            if (root != null)
                Object.DestroyImmediate(root);

            root = new GameObject("TEMP_TreeSamples");

            log.AppendLine();
            log.AppendLine("[Samples]  three trees placed for a look, not a forest");

            for (int i = 0; i < SampleTrees.Length && i < SampleOffsets.Length; i++)
            {
                var prefab = FindPrefab(prefabs, SampleTrees[i]);
                if (prefab == null)
                {
                    problems.Add($"sample tree {SampleTrees[i]} was not built");
                    continue;
                }

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                instance.transform.SetParent(root.transform, true);

                // Offset along the player's forward so the trees are in front of the camera
                // whichever way Phase 3 left it pointing.
                var offset = player.transform.TransformDirection(SampleOffsets[i]);
                var at = player.transform.position + new Vector3(offset.x, 0f, offset.z);

                at.y = GroundHeight(terrain, at.x, at.z);
                instance.transform.position = at;

                log.AppendLine($"  {SampleTrees[i],-18} at ({at.x:F1}, {at.y:F1}, {at.z:F1})");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        static GameObject FindPrefab(List<GameObject> prefabs, string id)
        {
            foreach (var prefab in prefabs)
                if (prefab != null && prefab.name == id)
                    return prefab;
            return null;
        }

        static float GroundHeight(Terrain terrain, float x, float z)
        {
            var origin = terrain.transform.position;
            var size = terrain.terrainData.size;

            float u = Mathf.Clamp01((x - origin.x) / size.x);
            float v = Mathf.Clamp01((z - origin.z) / size.z);

            return origin.y + terrain.terrainData.GetInterpolatedHeight(u, v);
        }

        static GameObject FindInScene(UnityEngine.SceneManagement.Scene scene, string name)
        {
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == name)
                    return root;
            return null;
        }

        /// <summary>
        /// Any depth, because the player owns the camera as a child and the player itself sits
        /// wherever Phase 3 put it. A root-only search would also find M0_Player, but the two
        /// lookups in this file should not disagree about what "in the scene" means.
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
