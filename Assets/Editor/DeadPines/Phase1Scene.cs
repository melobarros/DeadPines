using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace DeadPines.EditorTools
{
    /// <summary>
    /// Phase 1 of M0 — the scene: sky, ambient, fog, volume profile, one camera.
    ///
    /// Phase 0 handled everything project-wide (TECHNICAL.md §11 "Project setup", "Quality
    /// settings", "Textures"). Everything left in §11 is per-scene, and this is where it lands:
    /// the skybox, Ambient Mode: Gradient, fog, and "post-processing: tonemapping + colour
    /// grading only. Nothing else in the volume."
    ///
    /// The lighting model this builds is the FINAL one, not a placeholder (D35). There is no
    /// sun, no moon, and nothing casts a shadow — hemispheric ambient is the primary light for
    /// the entire game, which is why the ambient colours below are the most important numbers
    /// in the file rather than decimals to skim past.
    ///
    /// Idempotent, in the same way Phase 0 is: it reuses the scene, materials and profile if
    /// they already exist, re-reads every value back, and reports anything it could not find.
    /// </summary>
    public static class Phase1Scene
    {
        const string MenuPath = "Dead Pines/Phase 1 — Build Sky Scene";

        const string ScenesFolder = "Assets/Scenes";
        const string ScenePath = ScenesFolder + "/M0.unity";
        const string SettingsFolder = "Assets/Settings";
        const string SkyFolder = "Assets/Art/Sky";

        const string SkyMaterialPath = SettingsFolder + "/Sky_Overcast.mat";
        const string GroundMaterialPath = SettingsFolder + "/M0_Ground.mat";
        const string VolumeProfilePath = SettingsFolder + "/M0_Volume.asset";

        /// §2 — a 4K HDRI is the source; there is no reason to hold it at a higher tier, and
        /// every reason not to on a 4 GB card. The skybox is resident for the whole session.
        const int SkyTextureSize = 4096;

        /// ExpSquared. At this density a surface 200 m out is ~70% fogged, 300 m is ~93%, and
        /// 1000 m is opaque to within float precision.
        ///
        /// The density is only half of hiding the draw distance — the other half is that
        /// whatever surface you are standing on has to EXTEND past the point where fog
        /// saturates. A 500 m plane ends at 250 m (72% fogged), which is why its edge was
        /// visible. The scaffolding below is sized against this number; keep the two in step.
        const float FogDensity = 0.0055f;

        [MenuItem(MenuPath, priority = 10)]
        public static void Build()
        {
            var log = new StringBuilder();
            var problems = new List<string>();

            log.AppendLine("=== Dead Pines — Phase 1 scene ===");

            // Never silently discard unsaved work in whatever scene is currently open.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.Log("[Dead Pines] Phase 1 cancelled — the open scene was not saved.");
                return;
            }

            var hdrPath = FindSkyHdr(log, problems);
            if (hdrPath == null)
            {
                // Nothing below this can work without a sky, and a half-built scene is worse
                // than no scene. Stop here and say exactly what is missing.
                log.AppendLine();
                log.AppendLine($"Put one CC0 overcast HDRI in {SkyFolder}/ and run this again.");
                log.AppendLine("Poly Haven, 4K HDR. See the notes at the bottom of this file.");
                Debug.LogWarning(log.ToString());
                return;
            }

            ConfigureSkyTexture(hdrPath, log, problems);
            var skyMaterial = BuildSkyMaterial(hdrPath, log, problems);
            var profile = BuildVolumeProfile(log, problems);
            var scene = OpenOrCreateScene(log);

            BuildCamera(scene, log, problems);
            BuildVolume(scene, profile, log, problems);
            BuildGroundPlane(scene, log);

            ApplyRenderSettings(skyMaterial, log);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();

            Verify(log, skyMaterial, profile);

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
                log.AppendLine("--- scene built ---");
                log.AppendLine();
                log.AppendLine("WHAT TO LOOK AT (I can't see the game — this part is yours):");
                log.AppendLine("  1. Is the sky overcast weather, or does it read as a bug?");
                log.AppendLine("  2. Does the ground plane look lit by the sky, or flat and dead?");
                log.AppendLine("  3. Can you see the far edge of the plane? You should not.");
                Debug.Log(log.ToString());
            }
        }

        // ---------------------------------------------------------------------------------
        // Sky
        // ---------------------------------------------------------------------------------

        static string FindSkyHdr(StringBuilder log, List<string> problems)
        {
            if (!AssetDatabase.IsValidFolder(SkyFolder))
            {
                problems.Add($"{SkyFolder} does not exist");
                return null;
            }

            // Scoped to SkyFolder on purpose. An unscoped FindAssets is what wrote into an
            // immutable URP package asset in Phase 0 — the search is only ever as safe as the
            // folder it is confined to.
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { SkyFolder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".hdr" || ext == ".exr")
                    return path;
            }

            problems.Add($"no .hdr or .exr found in {SkyFolder}");
            return null;
        }

        static void ConfigureSkyTexture(string path, StringBuilder log, List<string> problems)
        {
            log.AppendLine();
            log.AppendLine("[Sky texture]");

            // Guarantee the asset is imported before asking for its importer, so this works
            // whether or not Unity has already picked the file up.
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer))
            {
                problems.Add($"no TextureImporter for {path}");
                return;
            }

            // The pitfall this exists to prevent: Skybox/Panoramic wants a flat 2D lat-long
            // image. Importing it as a Cubemap is the classic way this breaks, and it fails
            // by rendering something wrong rather than by erroring.
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.maxTextureSize = SkyTextureSize;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.mipmapEnabled = false;
            // HDR means linear. The whole point of the file is range that the tonemapper then
            // compresses; an sRGB curve applied first would fight that.
            importer.sRGBTexture = false;
            importer.SaveAndReimport();

            log.AppendLine($"  source        {path}");
            log.AppendLine($"  shape         {importer.textureShape}");
            log.AppendLine($"  max size      {importer.maxTextureSize}");
            log.AppendLine($"  sRGB          {importer.sRGBTexture}  (must be False for HDR)");
        }

        static Material BuildSkyMaterial(string hdrPath, StringBuilder log, List<string> problems)
        {
            log.AppendLine();
            log.AppendLine("[Skybox material]");

            var shader = Shader.Find("Skybox/Panoramic");
            if (shader == null)
            {
                problems.Add("shader 'Skybox/Panoramic' not found — is this a URP project?");
                return null;
            }

            var texture = AssetDatabase.LoadAssetAtPath<Texture>(hdrPath);
            if (texture == null)
            {
                problems.Add($"could not load {hdrPath} as a Texture");
                return null;
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(SkyMaterialPath);
            bool created = material == null;
            if (created)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, SkyMaterialPath);
            }
            else
            {
                material.shader = shader;
            }

            material.SetTexture("_MainTex", texture);
            material.SetColor("_Tint", new Color(0.5f, 0.5f, 0.5f, 1f));
            material.SetFloat("_Exposure", 1.0f);
            material.SetFloat("_Rotation", 0f);

            EditorUtility.SetDirty(material);

            // _Mapping and _ImageType are KeywordEnums whose defaults are already what a
            // lat-long 360 panorama needs (Latitude Longitude Layout, 360 Degrees). Setting
            // the float alone does not reliably toggle a KeywordEnum's shader keyword, so
            // rather than guess at the generated keyword spelling these are read back and
            // reported instead — a wrong value here is visible in the log, not just on screen.
            var mapping = material.HasProperty("_Mapping") ? material.GetFloat("_Mapping") : -1f;
            var imageType = material.HasProperty("_ImageType") ? material.GetFloat("_ImageType") : -1f;

            log.AppendLine($"  material      {SkyMaterialPath}{(created ? "  (created)" : "  (reused)")}");
            log.AppendLine($"  shader        {shader.name}");
            log.AppendLine($"  texture       {texture.name}");
            log.AppendLine($"  _Mapping      {mapping}   (want 1 = Latitude Longitude Layout)");
            log.AppendLine($"  _ImageType    {imageType}   (want 0 = 360 Degrees)");
            log.AppendLine($"  _Exposure     {material.GetFloat("_Exposure")}");

            if (mapping != 1f)
                problems.Add($"_Mapping is {mapping}, expected 1 (Latitude Longitude) — set it by hand in the material inspector");
            if (imageType != 0f)
                problems.Add($"_ImageType is {imageType}, expected 0 (360 Degrees) — set it by hand in the material inspector");

            return material;
        }

        // ---------------------------------------------------------------------------------
        // Volume profile — §11: "tonemapping + colour grading only"
        // ---------------------------------------------------------------------------------

        static VolumeProfile BuildVolumeProfile(StringBuilder log, List<string> problems)
        {
            log.AppendLine();
            log.AppendLine("[Volume profile]");

            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);
            bool created = profile == null;
            if (created)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, VolumeProfilePath);
            }

            // Tonemapping. Neutral rather than ACES: ACES shifts hue as it desaturates, and
            // pushes greens toward yellow — which fights a game whose register is "70%
            // lighting, palette and silhouette". Neutral keeps the palette you grade.
            var tonemapping = Add<Tonemapping>(profile);
            // The field is `mode`, not `tonemappingMode` — verified against
            // Runtime/Overrides/Tonemapping.cs:186 in the URP package rather than assumed.
            tonemapping.mode.overrideState = true;
            tonemapping.mode.value = TonemappingMode.Neutral;

            // Colour grading. These start near-neutral on purpose — dusk is something to dial
            // in by eye against the actual sky, not something to bake in blind from an editor
            // script. Saturation is pulled down slightly because overcast desaturates.
            var color = Add<ColorAdjustments>(profile);
            color.postExposure.overrideState = true;
            color.postExposure.value = 0f;
            color.contrast.overrideState = true;
            color.contrast.value = 0f;
            color.saturation.overrideState = true;
            color.saturation.value = -8f;
            color.colorFilter.overrideState = true;
            color.colorFilter.value = new Color(1f, 0.99f, 0.96f);

            // White balance is the lever for D35's open question — how day/night is driven
            // without a moving light. It is colour grading, so it belongs here, and it is the
            // cheapest place for the eventual time-of-day shift to live.
            var whiteBalance = Add<WhiteBalance>(profile);
            whiteBalance.temperature.overrideState = true;
            whiteBalance.temperature.value = -6f;
            whiteBalance.tint.overrideState = true;
            whiteBalance.tint.value = 0f;

            EditorUtility.SetDirty(profile);

            log.AppendLine($"  profile       {VolumeProfilePath}{(created ? "  (created)" : "  (reused)")}");
            log.AppendLine($"  {tonemapping.GetType().Name,-20} mode {tonemapping.mode.value}");
            log.AppendLine($"  {color.GetType().Name,-20} saturation {color.saturation.value}, contrast {color.contrast.value}");
            log.AppendLine($"  {whiteBalance.GetType().Name,-20} temperature {whiteBalance.temperature.value}, tint {whiteBalance.tint.value}");
            log.AppendLine("  deliberately NOT added: bloom, DOF, motion blur,");
            log.AppendLine("  chromatic aberration, vignette, film grain, SSAO");

            return profile;
        }

        /// <summary>
        /// Adds an override and registers it as a sub-asset, so it survives a domain reload.
        /// A VolumeComponent created without AddObjectToAsset lives only in memory and the
        /// profile silently comes back empty on the next editor start.
        /// </summary>
        static T Add<T>(VolumeProfile profile) where T : VolumeComponent
        {
            foreach (var existing in profile.components)
                if (existing is T typed)
                    return typed;

            var component = profile.Add<T>(true);
            component.hideFlags = HideFlags.HideInHierarchy;
            AssetDatabase.AddObjectToAsset(component, profile);
            return component;
        }

        // ---------------------------------------------------------------------------------
        // Scene contents
        // ---------------------------------------------------------------------------------

        static UnityEngine.SceneManagement.Scene OpenOrCreateScene(StringBuilder log)
        {
            log.AppendLine();
            log.AppendLine("[Scene]");

            if (System.IO.File.Exists(ScenePath))
            {
                var opened = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                log.AppendLine($"  opened        {ScenePath}");
                return opened;
            }

            if (!AssetDatabase.IsValidFolder(ScenesFolder))
                AssetDatabase.CreateFolder("Assets", "Scenes");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            log.AppendLine($"  created       {ScenePath}  (empty scene — camera and volume built below)");
            return scene;
        }

        static void BuildCamera(UnityEngine.SceneManagement.Scene scene, StringBuilder log, List<string> problems)
        {
            log.AppendLine();
            log.AppendLine("[Camera]");

            // Searched at any depth, not just at the root. Phase 3 re-parents this camera onto
            // M0_Player, and a root-only search would then miss it and build a second
            // M0_Camera at the origin — two cameras rendering the same frame, which
            // TECHNICAL.md's "use one camera" exists to prevent.
            var go = FindAnywhere(scene, "M0_Camera");
            bool created = go == null;
            if (created)
                go = new GameObject("M0_Camera");

            // Never `??` on a UnityEngine.Object. GetComponent returns Unity's "fake null" —
            // a live managed reference pointing at nothing — and ?? uses reference equality,
            // so it would treat the missing component as present, skip AddComponent, and throw
            // on the next line. Unity overloads == to see through fake null; ?? does not.
            var camera = go.GetComponent<Camera>();
            if (camera == null)
                camera = go.AddComponent<Camera>();

            if (camera == null)
            {
                problems.Add("could not add a Camera to M0_Camera");
                return;
            }

            // Eye height, and only while the camera is still loose in the scene. 1.7 m is the
            // working assumption until Phase 3 exists and picks its own. Once Phase 3 has
            // adopted it the position belongs to M0_Player, and writing a world position here
            // would drag the player's head back to the origin.
            if (go.transform.parent == null)
            {
                go.transform.position = new Vector3(0f, 1.7f, 0f);
                go.transform.rotation = Quaternion.Euler(0f, 0f, 0f);
            }

            camera.clearFlags = CameraClearFlags.Skybox;
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 1400f;  // past the ground plane's 1 km edge — see BuildGroundPlane

            // The one that fails quietly: without this the volume does nothing at all and the
            // scene renders ungraded with no error anywhere.
            var data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.None;

            log.AppendLine($"  camera        {(created ? "created" : "reused")}");
            // Concatenated rather than interpolated: an interpolated string cannot span lines
            // at this project's C# 9, and the compiler says so only when it next builds.
            log.AppendLine("  position      " + (go.transform.parent == null
                ? go.transform.position.ToString()
                : "on " + go.transform.parent.name + " — left alone"));
            log.AppendLine($"  clear flags   {camera.clearFlags}");
            log.AppendLine($"  post-process  {data.renderPostProcessing}  (must be True)");
            log.AppendLine($"  antialiasing  {data.antialiasing}");
        }

        static void BuildVolume(UnityEngine.SceneManagement.Scene scene, VolumeProfile profile, StringBuilder log, List<string> problems)
        {
            log.AppendLine();
            log.AppendLine("[Global volume]");

            var go = FindInScene(scene, "M0_GlobalVolume");
            if (go == null)
                go = new GameObject("M0_GlobalVolume");

            // Same fake-null rule as the camera above.
            var volume = go.GetComponent<Volume>();
            if (volume == null)
                volume = go.AddComponent<Volume>();

            if (volume == null)
            {
                problems.Add("could not add a Volume to M0_GlobalVolume");
                return;
            }

            volume.isGlobal = true;
            volume.priority = 0f;
            volume.weight = 1f;
            volume.profile = profile;

            log.AppendLine($"  is global     {volume.isGlobal}");
            log.AppendLine($"  weight        {volume.weight}");
            log.AppendLine($"  profile       {(volume.profile != null ? volume.profile.name : "NONE")}");
        }

        /// <summary>
        /// Scaffolding, and named to say so. Fog needs a surface to act on and ambient light
        /// needs a surface to fall on — without geometry a screenshot of this scene proves
        /// nothing about either. Delete it the moment real terrain lands.
        /// </summary>
        static void BuildGroundPlane(UnityEngine.SceneManagement.Scene scene, StringBuilder log)
        {
            log.AppendLine();
            log.AppendLine("[Ground plane — TEMPORARY scaffolding]");

            // Phase 2 deletes this on purpose the moment real terrain lands. Re-running Phase 1
            // afterwards would otherwise put it straight back: a 2 km grey plane at y = 0,
            // underneath the heightmap and poking through wherever the terrain dips below zero.
            if (FindInScene(scene, "M0_Terrain") != null)
            {
                log.AppendLine("  skipped       M0_Terrain is in the scene — the scaffolding stays deleted");
                return;
            }

            var go = FindInScene(scene, "TEMP_GroundPlane");
            if (go == null)
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Plane);
                go.name = "TEMP_GroundPlane";
            }

            go.transform.position = Vector3.zero;
            // 2 km across, so the edge sits at 1 km — well past the point where fog is
            // opaque — and inside the camera's far clip, so the edge is occluded by fog
            // rather than by the frustum. Clipping the plane short would hide the edge for
            // the wrong reason and tell us nothing about whether the fog actually works.
            go.transform.localScale = new Vector3(200f, 1f, 200f);

            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
                renderer.sharedMaterial = BuildGroundMaterial();

            log.AppendLine($"  size          {go.transform.localScale.x * 10f} m across");
            log.AppendLine("  delete this once terrain exists");

            // A plane primitive arrives with a collider. Harmless now, but it is not scenery
            // and should not be walked on.
            var collider = go.GetComponent<Collider>();
            if (collider != null)
                Object.DestroyImmediate(collider);
        }

        static Material BuildGroundMaterial()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(GroundMaterialPath);
            if (material != null)
                return material;

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            material = new Material(shader);
            // Mid grey and nothing else. This is a measuring surface for the ambient light, so
            // any colour here would be contaminating the thing it exists to show.
            material.SetColor("_BaseColor", new Color(0.35f, 0.35f, 0.33f));
            AssetDatabase.CreateAsset(material, GroundMaterialPath);
            return material;
        }

        // ---------------------------------------------------------------------------------
        // Render settings — §11: "Ambient Mode: Gradient — this is the primary light"
        // ---------------------------------------------------------------------------------

        static void ApplyRenderSettings(Material skyMaterial, StringBuilder log)
        {
            log.AppendLine();
            log.AppendLine("[Render settings]");

            if (skyMaterial != null)
                RenderSettings.skybox = skyMaterial;

            // Gradient, not Skybox. §11 lists the HDRI as "the skybox and the ambient source"
            // but §7 names Gradient explicitly, and §11's own quality-settings line repeats it.
            // Gradient wins because the ground-bounce colour is the one knob that decides
            // whether the forest floor reads warm or dead — and a flat overcast HDRI has
            // almost nothing distinctive for Skybox mode to sample anyway.
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.42f, 0.47f, 0.55f);   // cool, from above
            RenderSettings.ambientEquatorColor = new Color(0.38f, 0.38f, 0.36f);
            RenderSettings.ambientGroundColor = new Color(0.18f, 0.17f, 0.13f); // dead leaf litter
            RenderSettings.ambientIntensity = 1f;

            // URP takes fog from RenderSettings. There is no fog override in the URP volume
            // system — that is HDRP — so this is the only place it can be set.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            // Slightly lighter than the sky's average and barely blue. Where the fogged ground
            // meets the sky it has to be the SAME colour, or the horizon shows as a hard seam
            // no amount of density will hide. This is an estimate from an overcast sky, not a
            // sample of yours — if a band lingers at the horizon, drag this toward whatever
            // the sky reads as in the Lighting window. That is a judgement made by eye, and
            // you are the one with eyes on it.
            RenderSettings.fogColor = new Color(0.60f, 0.62f, 0.65f);
            RenderSettings.fogDensity = FogDensity;

            log.AppendLine($"  skybox        {(RenderSettings.skybox != null ? RenderSettings.skybox.name : "NONE")}");
            log.AppendLine($"  ambient mode  {RenderSettings.ambientMode}");
            log.AppendLine($"  ambient sky   {RenderSettings.ambientSkyColor}");
            log.AppendLine($"  ambient ground{RenderSettings.ambientGroundColor}");
            log.AppendLine($"  fog           {RenderSettings.fog}, {RenderSettings.fogMode}, density {RenderSettings.fogDensity}");
            log.AppendLine($"  fog colour    {RenderSettings.fogColor}");
        }

        // ---------------------------------------------------------------------------------
        // Verification
        // ---------------------------------------------------------------------------------

        static void Verify(StringBuilder log, Material skyMaterial, VolumeProfile profile)
        {
            log.AppendLine();
            log.AppendLine("[Read back from the scene]");

            log.AppendLine($"  skybox        {(RenderSettings.skybox == skyMaterial && skyMaterial != null ? "assigned OK" : "MISMATCH")}");
            log.AppendLine($"  ambient       {RenderSettings.ambientMode} (want Trilight)");
            log.AppendLine($"  fog enabled   {RenderSettings.fog} (want True)");
            log.AppendLine($"  volume profile{(profile != null ? " " + profile.components.Count + " overrides" : " MISSING")}");
        }

        // ---------------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------------

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
        /// M0_Camera starts as a root object — this script leaves it loose — and becomes a child
        /// of M0_Player the first time Phase 3 runs. A root-only search would therefore find it
        /// once and then report it missing on every re-run, which builds a duplicate camera
        /// rather than reporting anything wrong.
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
