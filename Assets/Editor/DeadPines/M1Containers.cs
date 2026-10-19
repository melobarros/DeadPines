using System.IO;
using System.Text;
using DeadPines.Interaction;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace DeadPines.EditorTools
{
    /// <summary>
    /// Builds the M1 search loop: a small item catalogue, four containers inside the cabin,
    /// and the components that connect the player, the containers and the HUD.
    ///
    /// The item catalogue is six kinds of thing with hand-drawn 64×64 icons. The icons are
    /// written from pixels here rather than imported, because the repo must not carry
    /// third-party art that cannot be redistributed, and six flat silhouettes in the game's
    /// bone-on-black palette are enough to read at 40 px in a corner strip. They are written
    /// over on every run, so the drawing code stays the single source of truth.
    ///
    /// The containers are placed against the cabin's interior walls in the cabin's local space,
    /// whose origin is the floor top. The front of the cabin is −Z, the doorway side. Three of
    /// them hold two items each, and one is empty — the empty one matters as much as the full
    /// ones, because an empty crate is what teaches the player that searching is finite.
    ///
    /// Each container is a plain box carcass with trim, slats and handles built from smaller
    /// boxes, and its interaction collider is one box covering the whole silhouette. That split
    /// matters: the visible parts carry no colliders, so the aim sphere resolves against the
    /// prop's bounds no matter which 2 cm slat the player happens to be looking at.
    ///
    /// Re-running this item replaces the Interior group wholesale and rewrites the item assets;
    /// it does not duplicate anything. The player's Inventory, PlayerInteractor and the M1_HUD
    /// root are reused if present.
    /// </summary>
    public static class M1Containers
    {
        const string MenuPath = "Dead Pines/M1 — Containers and the Search";

        const string ScenePath = "Assets/Scenes/M0.unity";
        const string PlayerName = "M0_Player";
        const string CameraName = "M0_Camera";
        const string CabinName = "M1_Cabin";
        const string InteriorName = "Interior";
        const string HudName = "M1_HUD";
        const string InputPath = "Assets/InputSystem_Actions.inputactions";

        const string ItemsFolder = "Assets/Settings/Items";
        const string IconsFolder = "Assets/Art/UI/Icons";

        const string WoodMaterialPath = "Assets/Settings/M1_Prop_Wood.mat";
        const string TrimMaterialPath = "Assets/Settings/M1_Prop_Trim.mat";
        const string MetalMaterialPath = "Assets/Settings/M1_Prop_Metal.mat";

        const float HoldSeconds = 0.6f;
        const float Reach = 2.5f;
        const float AimRadius = 0.15f;

        // Catalogue order. The names are also the asset file names and the ids.
        static readonly string[] ItemIds =
        {
            "canned_food", "water_bottle", "bandage",
            "matches", "painkillers", "batteries"
        };

        static readonly string[] ItemNames =
        {
            "Canned food", "Water bottle", "Bandage",
            "Matches", "Painkillers", "Batteries"
        };

        [MenuItem(MenuPath, priority = 101)]
        public static void Build()
        {
            var log = new StringBuilder();
            log.AppendLine("=== Dead Pines — M1, containers and the search ===");

            if (EditorApplication.isPlaying)
            {
                Debug.LogWarning("[Dead Pines] Stop Play mode first — this writes the scene.");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.Log("[Dead Pines] Containers were not built — the open scene was not saved.");
                return;
            }

            if (!File.Exists(ScenePath))
            {
                log.AppendLine();
                log.AppendLine("  !! " + ScenePath + " does not exist — run Phase 1 first.");
                Debug.LogWarning(log.ToString());
                return;
            }

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var cabin = FindAnywhere(scene, CabinName);
            if (cabin == null)
            {
                log.AppendLine();
                log.AppendLine("  !! " + CabinName + " is not in M0 — run");
                log.AppendLine("     'Dead Pines/M1 — The Place (the shell)' first.");
                Debug.LogWarning(log.ToString());
                return;
            }

            var player = FindAnywhere(scene, PlayerName);
            if (player == null)
            {
                log.AppendLine();
                log.AppendLine("  !! " + PlayerName + " is not in M0 — run Phase 3 first.");
                Debug.LogWarning(log.ToString());
                return;
            }

            var cameraObject = FindAnywhere(scene, CameraName);

            Camera view = cameraObject != null ? cameraObject.GetComponent<Camera>() : null;

            if (view == null && cameraObject != null)
                view = cameraObject.GetComponentInChildren<Camera>(true);

            if (view == null)
                view = player.GetComponentInChildren<Camera>(true);

            // ---- assets -------------------------------------------------------------

            EnsureFolder(ItemsFolder);
            EnsureFolder(IconsFolder);

            var items = new ItemDefinition[ItemIds.Length];
            var iconPaths = new string[ItemIds.Length];

            for (int i = 0; i < ItemIds.Length; i++)
            {
                iconPaths[i] = IconsFolder + "/item_" + ItemIds[i] + ".png";

                WriteIcon(iconPaths[i], ItemIds[i]);
                ConfigureIcon(iconPaths[i]);

                var icon = AssetDatabase.LoadAssetAtPath<Sprite>(iconPaths[i]);

                items[i] = EnsureItem(ItemIds[i], ItemNames[i], icon);

                if (icon == null)
                    log.AppendLine("  !! icon did not import as a Sprite: " + iconPaths[i]);
            }

            AssetDatabase.SaveAssets();

            // ---- containers ---------------------------------------------------------

            var existing = cabin.transform.Find(InteriorName);

            if (existing != null)
                Object.DestroyImmediate(existing.gameObject);

            var interior = new GameObject(InteriorName);
            interior.transform.SetParent(cabin.transform, false);

            // Prop materials are deliberately not the cabin's. The shell's walls, floor and
            // roof sit at 0.06–0.16 value, and a container that shares them disappears into
            // the wall it stands against. The props get their own three values — wood above
            // the wall, trim below it, metal above both — so a box reads as a box in a dim
            // room without a texture or an extra light.
            var wood = EnsureMaterial(WoodMaterialPath, new Color(0.27f, 0.20f, 0.13f, 1f));
            var trim = EnsureMaterial(TrimMaterialPath, new Color(0.13f, 0.10f, 0.07f, 1f));
            var metal = EnsureMaterial(MetalMaterialPath, new Color(0.34f, 0.34f, 0.31f, 1f));

            var cupboard = BuildCupboard(interior.transform, "Cupboard",
                new Vector3(1.6f, 0.95f, 2.05f), wood, trim, metal);

            var crateA = BuildCrate(interior.transform, "Crate_A",
                new Vector3(1.8f, 0.275f, -1.2f), 0.55f, wood, trim);

            var shelf = BuildShelf(interior.transform, "Shelf",
                new Vector3(-2.6f, 0.8f, 0.8f), wood, trim);

            var crateB = BuildCrate(interior.transform, "Crate_B",
                new Vector3(-1.9f, 0.225f, -1.5f), 0.45f, wood, trim);

            AddContents(cupboard, items[0], items[3]);
            AddContents(crateA, items[2], items[4]);
            AddContents(shelf, items[1], items[5]);

            // ---- player and HUD -----------------------------------------------------

            var inventory = player.GetComponent<Inventory>();
            if (inventory == null)
                inventory = player.AddComponent<Inventory>();

            var interactor = player.GetComponent<PlayerInteractor>();
            if (interactor == null)
                interactor = player.AddComponent<PlayerInteractor>();

            interactor.view = view;
            interactor.actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputPath);
            interactor.inventory = inventory;
            interactor.reach = Reach;
            interactor.aimRadius = AimRadius;
            EditorUtility.SetDirty(interactor);

            var hudRoot = FindRoot(scene, HudName);
            if (hudRoot == null)
                hudRoot = new GameObject(HudName);

            var hud = hudRoot.GetComponent<InteractionHUD>();
            if (hud == null)
                hud = hudRoot.AddComponent<InteractionHUD>();

            hud.interactor = interactor;
            hud.inventory = inventory;
            EditorUtility.SetDirty(hud);

            // ---- save and report ----------------------------------------------------

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();

            log.AppendLine();
            log.AppendLine("  items         " + items.Length + " definitions in " + ItemsFolder);

            for (int i = 0; i < items.Length; i++)
                log.AppendLine("                " + items[i].displayName + "  ("
                               + items[i].id + ", " + Path.GetFileName(iconPaths[i]) + ")");

            log.AppendLine();
            log.AppendLine("  containers    " + InteriorName + " under " + CabinName
                           + " at " + cabin.transform.position.ToString("F1"));
            log.AppendLine("  prop mats     wood / trim / metal, own values rather than the");
            log.AppendLine("                cabin's near-black palette");

            ReportContainer(log, cupboard);
            ReportContainer(log, crateA);
            ReportContainer(log, shelf);
            ReportContainer(log, crateB);

            log.AppendLine();
            log.AppendLine("  player        " + PlayerName + ": Inventory + PlayerInteractor");
            log.AppendLine("  camera        " + (view != null
                               ? view.name
                               : "!! none found — the search ray will not work"));
            log.AppendLine("  input         " + (interactor.actions != null
                               ? InputPath + " ('Player' map, 'Interact')"
                               : "!! " + InputPath + " did not load"));
            log.AppendLine("  reach         " + Reach.ToString("F1") + " m, aim sphere "
                           + AimRadius.ToString("F2") + " m, hold "
                           + HoldSeconds.ToString("F1") + " s per item");
            log.AppendLine("  HUD           " + HudName + " (Canvas built at runtime)");

            log.AppendLine();
            log.AppendLine("WHAT TO DO:");
            log.AppendLine("  1. Press Play and walk in through the cabin doorway.");
            log.AppendLine("  2. Look at the cupboard against the back wall. A prompt appears");
            log.AppendLine("     at the bottom of the screen; hold E until the thin bar fills.");
            log.AppendLine("  3. Each hold takes one item. Let go before starting the next.");
            log.AppendLine("  4. Found items stack along the bottom-left; a toast names each one.");
            log.AppendLine("  5. The small crate by the door is empty on purpose. It reads");
            log.AppendLine("     'Empty' when you look at it.");
            log.AppendLine();
            log.AppendLine("  note          the prompt key is read from the 'Interact' binding,");
            log.AppendLine("                so rebinding the action relabels it.");
            Debug.Log(log.ToString());
        }

        // ---------------------------------------------------------------- containers

        /// <summary>
        /// The interaction root for a prop: an empty object with one box collider covering the
        /// whole silhouette and the Interactable on it. The visible parts are children with
        /// their colliders removed, so the aim sphere always resolves against the prop's
        /// bounding box rather than against a 2 cm trim edge.
        /// </summary>
        static Interactable PropRoot(Transform parent, string name, string displayName,
                                     Vector3 centre, Vector3 bounds)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = centre;

            var box = go.AddComponent<BoxCollider>();
            box.center = Vector3.zero;
            box.size = bounds;

            var interactable = go.AddComponent<Interactable>();
            interactable.displayName = displayName;
            interactable.holdSeconds = HoldSeconds;
            return interactable;
        }

        static GameObject Part(Transform parent, string name, Vector3 centre, Vector3 size,
                               Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = centre;
            go.transform.localScale = size;

            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var collider = go.GetComponent<Collider>();

            if (collider != null)
                Object.DestroyImmediate(collider);

            return go;
        }

        static void AddContents(Interactable interactable, params ItemDefinition[] contents)
        {
            interactable.contents.Clear();

            for (int i = 0; i < contents.Length; i++)
                if (contents[i] != null)
                    interactable.contents.Add(contents[i]);
        }

        static Interactable BuildCrate(Transform parent, string name, Vector3 centre,
                                       float size, Material wood, Material trim)
        {
            var root = PropRoot(parent, name, "crate", centre, Vector3.one * size);
            var t = root.transform;

            Part(t, "Body", Vector3.zero, Vector3.one * size, wood);

            float edge = size * 0.5f;
            float post = Mathf.Max(0.035f, size * 0.08f);
            float bandY = size * 0.18f;
            float bandHeight = Mathf.Max(0.03f, size * 0.09f);
            float bandDepth = Mathf.Max(0.02f, size * 0.05f);

            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    Part(t, "Post", new Vector3(sx * edge, 0f, sz * edge),
                         new Vector3(post, size + 0.01f, post), trim);

            for (int sz = -1; sz <= 1; sz += 2)
                Part(t, "Band", new Vector3(0f, bandY, sz * edge),
                     new Vector3(size + post, bandHeight, bandDepth), trim);

            for (int sx = -1; sx <= 1; sx += 2)
                Part(t, "Band", new Vector3(sx * edge, bandY, 0f),
                     new Vector3(bandDepth, bandHeight, size + post), trim);

            Part(t, "Lid", new Vector3(0f, edge - 0.01f, 0f),
                 new Vector3(size + 0.03f, 0.035f, size + 0.03f), trim);

            return root;
        }

        static Interactable BuildCupboard(Transform parent, string name, Vector3 centre,
                                          Material wood, Material trim, Material metal)
        {
            var size = new Vector3(1.2f, 1.9f, 0.4f);
            var root = PropRoot(parent, name, "cupboard", centre, size);
            var t = root.transform;

            Part(t, "Carcass", Vector3.zero, size, trim);

            float front = -size.z * 0.5f;
            float doorWidth = 0.55f;
            float doorHeight = 1.78f;
            float doorThickness = 0.03f;

            for (int i = -1; i <= 1; i += 2)
                Part(t, "Door", new Vector3(i * 0.29f, -0.02f, front - doorThickness * 0.5f),
                     new Vector3(doorWidth, doorHeight, doorThickness), wood);

            for (int i = -1; i <= 1; i += 2)
                Part(t, "Handle", new Vector3(i * 0.075f, 0.05f, front - 0.05f),
                     new Vector3(0.035f, 0.16f, 0.035f), metal);

            Part(t, "Top", new Vector3(0f, size.y * 0.5f - 0.02f, 0f),
                 new Vector3(size.x + 0.06f, 0.06f, size.z + 0.06f), trim);

            Part(t, "Plinth", new Vector3(0f, -size.y * 0.5f + 0.055f, 0f),
                 new Vector3(size.x + 0.06f, 0.11f, size.z + 0.06f), trim);

            return root;
        }

        static Interactable BuildShelf(Transform parent, string name, Vector3 centre,
                                       Material wood, Material trim)
        {
            var size = new Vector3(0.3f, 1.6f, 1.1f);
            var root = PropRoot(parent, name, "shelf", centre, size);
            var t = root.transform;

            float halfX = size.x * 0.5f;
            float halfY = size.y * 0.5f;

            for (int i = -1; i <= 1; i += 2)
                Part(t, "Side", new Vector3(i * (halfX - 0.03f), 0f, 0f),
                     new Vector3(0.06f, size.y, size.z), trim);

            // The wall is at -X, so the back panel is on that side and the shelf opens into
            // the room at +X.
            Part(t, "Back", new Vector3(-halfX + 0.015f, 0f, 0f),
                 new Vector3(0.03f, size.y, size.z), trim);

            float[] levels = { -halfY + 0.035f, -0.27f, 0.22f, halfY - 0.025f };

            for (int i = 0; i < levels.Length; i++)
                Part(t, "Board", new Vector3(0f, levels[i], 0f),
                     new Vector3(size.x - 0.12f, 0.05f, size.z), wood);

            return root;
        }

        static void ReportContainer(StringBuilder log, Interactable container)
        {
            string contents = container.contents.Count == 0
                ? "empty"
                : container.contents.Count + " item(s): " + Names(container);

            log.AppendLine("                " + container.name.PadRight(12)
                           + container.displayName.PadRight(10) + contents);
        }

        static string Names(Interactable container)
        {
            var names = new StringBuilder();

            for (int i = 0; i < container.contents.Count; i++)
            {
                if (i > 0)
                    names.Append(", ");

                names.Append(container.contents[i] != null
                    ? container.contents[i].displayName
                    : "!!missing");
            }

            return names.ToString();
        }

        // ---------------------------------------------------------------- prop materials

        static Material EnsureMaterial(string path, Color color)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (material == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");

                if (shader == null)
                {
                    Debug.LogError("[Dead Pines] Could not find the URP Lit shader — "
                                   + "check the render pipeline.");
                    return null;
                }

                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", 0.05f);
            material.SetFloat("_Metallic", 0f);

            EditorUtility.SetDirty(material);
            return material;
        }

        // ---------------------------------------------------------------- item assets

        static ItemDefinition EnsureItem(string id, string displayName, Sprite icon)
        {
            string path = ItemsFolder + "/" + id + ".asset";
            var item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);

            if (item == null)
            {
                item = ScriptableObject.CreateInstance<ItemDefinition>();
                AssetDatabase.CreateAsset(item, path);
            }

            item.id = id;
            item.displayName = displayName;
            item.icon = icon;

            EditorUtility.SetDirty(item);
            return item;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;

            if (path == "Assets" || !path.StartsWith("Assets/"))
                return;

            string parent = Path.GetDirectoryName(path);
            string name = Path.GetFileName(path);

            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
                return;

            parent = parent.Replace('\\', '/');

            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        // ---------------------------------------------------------------- icons

        static void WriteIcon(string path, string kind)
        {
            var canvas = new IconCanvas();
            DrawIcon(canvas, kind);

            var texture = canvas.ToTexture();
            byte[] png = texture.EncodeToPNG();
            Object.DestroyImmediate(texture);

            File.WriteAllBytes(path, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        static void ConfigureIcon(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;

            if (importer == null)
                return;

            bool changed = false;

            if (importer.textureType != TextureImporterType.Sprite)
            {
                importer.textureType = TextureImporterType.Sprite;
                changed = true;
            }

            if (importer.spriteImportMode != SpriteImportMode.Single)
            {
                importer.spriteImportMode = SpriteImportMode.Single;
                changed = true;
            }

            if (!importer.alphaIsTransparency)
            {
                importer.alphaIsTransparency = true;
                changed = true;
            }

            if (importer.mipmapEnabled)
            {
                importer.mipmapEnabled = false;
                changed = true;
            }

            if (importer.textureCompression != TextureImporterCompression.Uncompressed)
            {
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                changed = true;
            }

            if (importer.filterMode != FilterMode.Point)
            {
                importer.filterMode = FilterMode.Point;
                changed = true;
            }

            if (importer.wrapMode != TextureWrapMode.Clamp)
            {
                importer.wrapMode = TextureWrapMode.Clamp;
                changed = true;
            }

            if (changed)
                importer.SaveAndReimport();
        }

        static void DrawIcon(IconCanvas c, string kind)
        {
            switch (kind)
            {
                case "canned_food":
                    c.Rect(19, 16, 45, 48, IconCanvas.Bone);
                    c.Ellipse(32, 16, 13, 4, IconCanvas.Bone);
                    c.Rect(19, 25, 45, 37, IconCanvas.Dim);
                    c.Ellipse(32, 48, 13, 4, IconCanvas.Dim);
                    break;

                case "water_bottle":
                    c.Rect(20, 12, 44, 50, IconCanvas.Bone);
                    c.Ellipse(32, 12, 12, 5, IconCanvas.Bone);
                    c.Rect(20, 12, 44, 28, IconCanvas.Steel);
                    c.Ellipse(32, 12, 12, 5, IconCanvas.Steel);
                    c.Rect(20, 28, 44, 30, IconCanvas.Dim);
                    c.Rect(27, 50, 37, 56, IconCanvas.Bone);
                    c.Rect(25, 56, 39, 62, IconCanvas.Steel);
                    break;

                case "bandage":
                    c.Ellipse(28, 32, 18, 18, IconCanvas.Bone);
                    c.Ring(28, 32, 12, 12, 0.80f, IconCanvas.Dim);
                    c.Capsule(38, 42, 52, 54, 5f, IconCanvas.Bone);
                    break;

                case "matches":
                    c.Rect(10, 20, 36, 46, IconCanvas.Dim);
                    c.Rect(10, 20, 36, 26, IconCanvas.Steel);
                    c.Capsule(30, 26, 52, 8, 2.4f, IconCanvas.Bone);
                    c.Ellipse(52, 8, 4f, 4f, IconCanvas.Rust);
                    c.Capsule(36, 28, 58, 12, 2.4f, IconCanvas.Bone);
                    c.Ellipse(58, 12, 4f, 4f, IconCanvas.Rust);
                    break;

                case "painkillers":
                    c.Capsule(20, 20, 44, 44, 9f, IconCanvas.Bone);
                    c.Capsule(23, 41, 41, 23, 1.3f, IconCanvas.Dim);
                    break;

                case "batteries":
                    c.Rect(16, 14, 28, 46, IconCanvas.Bone);
                    c.Rect(19, 46, 25, 52, IconCanvas.Steel);
                    c.Rect(16, 38, 28, 42, IconCanvas.Steel);
                    c.Rect(36, 14, 48, 46, IconCanvas.Bone);
                    c.Rect(39, 46, 45, 52, IconCanvas.Steel);
                    c.Rect(36, 38, 48, 42, IconCanvas.Steel);
                    break;
            }
        }

        sealed class IconCanvas
        {
            public const int Size = 64;

            public static readonly Color32 Bone = new Color32(214, 205, 184, 255);
            public static readonly Color32 Dim = new Color32(146, 136, 116, 255);
            public static readonly Color32 Steel = new Color32(112, 120, 118, 255);
            public static readonly Color32 Rust = new Color32(176, 86, 52, 255);

            readonly Color32[] pixels = new Color32[Size * Size];

            public void Rect(int x0, int y0, int x1, int y1, Color32 color)
            {
                int left = Mathf.Max(0, x0);
                int right = Mathf.Min(Size, x1);
                int bottom = Mathf.Max(0, y0);
                int top = Mathf.Min(Size, y1);

                for (int y = bottom; y < top; y++)
                    for (int x = left; x < right; x++)
                        pixels[y * Size + x] = color;
            }

            public void Ellipse(float cx, float cy, float rx, float ry, Color32 color)
            {
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                        if (Inside(x, y, cx, cy, rx, ry))
                            pixels[y * Size + x] = color;
            }

            public void Ring(float cx, float cy, float rx, float ry, float innerScale,
                             Color32 color)
            {
                float inner = innerScale * innerScale;

                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                    {
                        float dx = (x + 0.5f - cx) / rx;
                        float dy = (y + 0.5f - cy) / ry;
                        float q = dx * dx + dy * dy;

                        if (q <= 1f && q >= inner)
                            pixels[y * Size + x] = color;
                    }
            }

            public void Capsule(float ax, float ay, float bx, float by, float radius,
                                Color32 color)
            {
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                        if (SegmentDistance(x + 0.5f, y + 0.5f, ax, ay, bx, by) <= radius)
                            pixels[y * Size + x] = color;
            }

            static bool Inside(int x, int y, float cx, float cy, float rx, float ry)
            {
                float dx = (x + 0.5f - cx) / rx;
                float dy = (y + 0.5f - cy) / ry;
                return dx * dx + dy * dy <= 1f;
            }

            static float SegmentDistance(float px, float py,
                                         float ax, float ay, float bx, float by)
            {
                float vx = bx - ax;
                float vy = by - ay;
                float wx = px - ax;
                float wy = py - ay;

                float lengthSq = vx * vx + vy * vy;
                float t = lengthSq > 0.0001f
                    ? Mathf.Clamp01((wx * vx + wy * vy) / lengthSq)
                    : 0f;

                float dx = px - (ax + vx * t);
                float dy = py - (ay + vy * t);

                return Mathf.Sqrt(dx * dx + dy * dy);
            }

            public Texture2D ToTexture()
            {
                var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
                texture.SetPixels32(pixels);
                texture.Apply();
                return texture;
            }
        }

        // ---------------------------------------------------------------- scene lookups

        static GameObject FindRoot(UnityEngine.SceneManagement.Scene scene, string name)
        {
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == name)
                    return root;

            return null;
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
