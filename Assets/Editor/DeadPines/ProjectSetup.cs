using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace DeadPines.EditorTools
{
    /// <summary>
    /// Phase 0 of M0 — everything in TECHNICAL.md §11 that is project-wide or lives on a
    /// settings asset.
    ///
    /// Scene-level settings (ambient mode, fog, skybox, the volume profile) are stored per
    /// scene and are deliberately NOT here — they belong with the scene, in Phase 1.
    ///
    /// Idempotent: run it as often as you like. It prints what it changed, re-reads each
    /// value back to prove the change stuck, and reports anything it could not find rather
    /// than failing silently.
    /// </summary>
    public static class ProjectSetup
    {
        const string MenuPath = "Dead Pines/Phase 0 — Apply Project Settings";

        /// TECHNICAL.md §2: "Enable mipmap streaming with a hard budget around 1.5-2.0 GB."
        const float MipStreamingBudgetMb = 1792f;

        [MenuItem(MenuPath, priority = 0)]
        public static void Apply()
        {
            var log = new StringBuilder();
            var problems = new List<string>();
            var applied = new List<Applied>();

            log.AppendLine("=== Dead Pines — Phase 0 project settings ===");

            ApplyGraphicsApi(log);
            ApplyQualityLevels(log, problems);
            ApplyPipelineAssets(log, problems, applied);
            ApplyGlobalSettings(log, problems, applied);
            StripStaleSettings(log);

            AssetDatabase.SaveAssets();
            Verify(log, applied);

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
                log.AppendLine("--- all settings applied ---");
                Debug.Log(log.ToString());
            }
        }

        // ---------------------------------------------------------------------------------
        // Graphics API
        // ---------------------------------------------------------------------------------

        static void ApplyGraphicsApi(StringBuilder log)
        {
            // TECHNICAL.md §5: measured DX11 = 3.5 ms CPU / 5 GB VRAM against DX12 = 6.0 ms /
            // 7 GB at near-identical frame rate. On a 4 GB card the 2 GB alone decides it.
            // Unity 6.1+ makes DX12 the default for new projects, so this must be forced back.
            var target = BuildTarget.StandaloneWindows64;
            var wanted = new[] { GraphicsDeviceType.Direct3D11 };

            PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
            PlayerSettings.SetGraphicsAPIs(target, wanted);

            var actual = PlayerSettings.GetGraphicsAPIs(target);
            log.AppendLine();
            log.AppendLine($"[Graphics API] {target} -> {string.Join(", ", actual)}");

            if (PlayerSettings.colorSpace != ColorSpace.Linear)
            {
                PlayerSettings.colorSpace = ColorSpace.Linear;
                log.AppendLine("[Color space]  set to Linear");
            }
            else
            {
                log.AppendLine("[Color space]  Linear (already correct)");
            }
        }

        // ---------------------------------------------------------------------------------
        // Quality levels
        // ---------------------------------------------------------------------------------

        static void ApplyQualityLevels(StringBuilder log, List<string> problems)
        {
            log.AppendLine();
            log.AppendLine("[Quality levels]");

            var pc = FindAsset<UniversalRenderPipelineAsset>("PC_RPAsset");
            if (pc == null)
            {
                problems.Add("PC_RPAsset not found — quality levels left alone");
                return;
            }

            // The URP template ships PC and Mobile variants. This game has exactly one
            // target (PC, DX11), so every level is repointed at the PC asset. Once nothing
            // references the Mobile assets they can be deleted — see the note in the log.
            int original = QualitySettings.GetQualityLevel();
            var names = QualitySettings.names;

            for (int i = 0; i < names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = pc;
                QualitySettings.vSyncCount = 0;                     // §11: VSync off while measuring
                QualitySettings.antiAliasing = 0;                   // §11: MSAA off, TAA instead
                QualitySettings.streamingMipmapsActive = true;      // §2
                QualitySettings.streamingMipmapsMemoryBudget = MipStreamingBudgetMb;

                log.AppendLine($"  [{i}] {names[i]} -> PC_RPAsset, VSync off, MSAA off, " +
                               $"mipmap streaming {MipStreamingBudgetMb:0} MB");
            }

            QualitySettings.SetQualityLevel(Mathf.Clamp(original, 0, names.Length - 1), false);
            QualitySettings.renderPipeline = pc;
            log.AppendLine($"  active level restored to [{original}] {names[Mathf.Clamp(original, 0, names.Length - 1)]}");
        }

        // ---------------------------------------------------------------------------------
        // URP pipeline + renderer assets
        // ---------------------------------------------------------------------------------

        static void ApplyPipelineAssets(StringBuilder log, List<string> problems, List<Applied> applied)
        {
            log.AppendLine();
            log.AppendLine("[Render pipeline assets]");

            // Configure every URP asset in the project, not just the PC one. After the
            // quality-level pass only PC_RPAsset is referenced, but configuring all of them
            // means the settings are correct whichever asset ends up active.
            foreach (var asset in FindProjectAssets<UniversalRenderPipelineAsset>(
                         "t:UniversalRenderPipelineAsset"))
            {
                ConfigurePipelineAsset(asset, log, applied);
            }

            log.AppendLine();
            log.AppendLine("[Renderer assets]");

            foreach (var data in FindProjectAssets<UniversalRendererData>("t:UniversalRendererData"))
            {
                ConfigureRendererData(data, log, problems, applied);
            }
        }

        static void ConfigurePipelineAsset(UniversalRenderPipelineAsset asset, StringBuilder log,
                                           List<Applied> applied)
        {
            log.AppendLine($"  {asset.name}");

            var so = new SerializedObject(asset);
            so.Update();
            var set = new Setter(so, log, applied);

            // D35 / §6 — no shadows anywhere in this game. The single largest performance
            // decision in the project: roughly 20% of GPU frame time, plus a large slice of
            // the draw-call ceiling, comes back.
            set.Bool("main light shadows", new[] { "m_MainLightShadowsSupported" }, false);
            set.Bool("additional light shadows", new[] { "m_AdditionalLightShadowsSupported" }, false);
            set.Int("shadow cascade count", new[] { "m_ShadowCascadeCount" }, 0);

            // §11 — MSAA off; TAA or FSR/STP instead. (1 = no multisampling.)
            set.Int("MSAA samples", new[] { "m_MSAA" }, 1);

            // §2 — "Turn Depth Texture and Opaque Texture off unless a shader samples them."
            set.Bool("depth texture", new[] { "m_RequireDepthTexture" }, false);
            set.Bool("opaque texture", new[] { "m_RequireOpaqueTexture" }, false);

            // §5 — the only batching win that is free here.
            set.Bool("SRP Batcher", new[] { "m_UseSRPBatcher" }, true);

            // §5 — "measure, don't enable" for GPU Resident Drawer. Leave it off at M0.
            // Declared here and nowhere else: on the pipeline asset only, and it was called
            // m_MacroBatcherMode before 6.0. Intermediate Texture Mode is NOT here — that one
            // belongs to the renderer data, and lives there.
            set.Enum("GPU Resident Drawer", new[] { "m_GPUResidentDrawerMode", "m_MacroBatcherMode" },
                     "Disabled");

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
        }

        static void ConfigureRendererData(UniversalRendererData data, StringBuilder log,
                                          List<string> problems, List<Applied> applied)
        {
            log.AppendLine($"  {data.name}");

            var so = new SerializedObject(data);
            so.Update();
            var set = new Setter(so, log, applied);

            // §11 — Forward+. The better path for many small lights, and a prerequisite for
            // GPU Resident Drawer if it is ever enabled. Matched by name rather than by enum
            // index: RenderingMode is Forward = 0, Deferred = 1, ForwardPlus = 2,
            // DeferredPlus = 3, so the numbers are not in the order you would guess.
            set.Enum("rendering path", new[] { "m_RenderingMode" }, "Forward+");

            // §2 — "Set Intermediate Texture = Auto." This lives on the renderer data, not on
            // the pipeline asset. Auto lets URP skip the intermediate texture entirely when no
            // feature needs one, which is most of the saving.
            set.Enum("intermediate texture mode", new[] { "m_IntermediateTextureMode" }, "Auto");

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(data);

            DisableCutFeatures(so, log);
        }

        /// <summary>
        /// Turns off the renderer features that D35 and §7 take out of the frame.
        ///
        /// The URP template wires ScreenSpaceAmbientOcclusion into the PC renderer with
        /// <c>m_Active: 1</c> — an ambient-occlusion pass that §7 explicitly cuts, running by
        /// default on a 4 GB card.
        ///
        /// Features are disabled, not deleted. The renderer keeps a feature-hash map beside
        /// the list, and array surgery risks desyncing it — and a disabled feature costs
        /// nothing at runtime. Remove the empty slot by hand in the Renderer inspector if it
        /// bothers you.
        /// </summary>
        static void DisableCutFeatures(SerializedObject rendererSo, StringBuilder log)
        {
            var cut = new HashSet<string> { "ScreenSpaceAmbientOcclusion", "ScreenSpaceShadows" };

            var features = rendererSo.FindProperty("m_RendererFeatures");
            if (features == null || !features.isArray)
                return;

            if (features.arraySize == 0)
            {
                log.AppendLine("      renderer features: none");
                return;
            }

            log.AppendLine($"      renderer features: {features.arraySize}");

            for (int i = 0; i < features.arraySize; i++)
            {
                var reference = features.GetArrayElementAtIndex(i).objectReferenceValue;
                if (reference == null)
                {
                    log.AppendLine($"        [{i}] (null)");
                    continue;
                }

                var name = reference.GetType().Name;
                if (!cut.Contains(name))
                {
                    log.AppendLine($"        [{i}] {name} — left alone, not on the cut list");
                    continue;
                }

                var featureSo = new SerializedObject(reference);
                var active = featureSo.FindProperty("m_Active");
                if (active == null)
                {
                    log.AppendLine($"        [{i}] {name} — NO m_Active FIELD, disable it by hand");
                    continue;
                }

                bool wasActive = active.boolValue;
                active.boolValue = false;
                featureSo.ApplyModifiedProperties();
                EditorUtility.SetDirty(reference);

                log.AppendLine($"        [{i}] {name} — " +
                               (wasActive ? "WAS ACTIVE, now disabled" : "already disabled"));
            }
        }

        // ---------------------------------------------------------------------------------
        // Global settings
        // ---------------------------------------------------------------------------------

        static void ApplyGlobalSettings(StringBuilder log, List<string> problems, List<Applied> applied)
        {
            log.AppendLine();
            log.AppendLine("[Global settings]");

            // Make the PC asset the project default, so a newly created scene gets the right
            // pipeline without being told.
            var pc = FindAsset<UniversalRenderPipelineAsset>("PC_RPAsset");
            if (pc != null && GraphicsSettings.defaultRenderPipeline != pc)
            {
                GraphicsSettings.defaultRenderPipeline = pc;
                log.AppendLine("  default render pipeline -> PC_RPAsset");
            }
            else if (pc != null)
            {
                log.AppendLine("  default render pipeline already PC_RPAsset");
            }

            // Nothing on the global settings asset needs touching at M0.
            //
            // An earlier version of this script looked for GPU Resident Drawer here and
            // reported NOT FOUND. It was looking in the wrong place: that setting is declared
            // on UniversalRenderPipelineAsset and nowhere else, and the pipeline-asset set
            // above is the one that counts. Left deliberately empty rather than reaching for
            // something to do.
        }

        // ---------------------------------------------------------------------------------
        // Leftovers from removed packages
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Removes the scripting defines and build config objects that the Unity AI packages
        /// left behind.
        ///
        /// Installing a package writes into ProjectSettings; uninstalling does not clean up
        /// after itself. Taking ai.assistant and ai.inference out of the manifest removed them
        /// from packages-lock.json, but left two orphaned define symbols and a build config
        /// object pointing at a GUID that no longer resolves.
        /// </summary>
        static void StripStaleSettings(StringBuilder log)
        {
            log.AppendLine();
            log.AppendLine("[Leftovers from removed packages]");

            StripDefines(new HashSet<string> { "SENTIS_ANALYTICS_ENABLED", "APP_UI_EDITOR_ONLY" }, log);
            StripConfigObjects(new HashSet<string> { "com.unity.dt.app-ui" }, log);
        }

        static void StripDefines(HashSet<string> stale, StringBuilder log)
        {
            var asset = LoadFirstAsset("ProjectSettings/ProjectSettings.asset");
            if (asset == null)
            {
                log.AppendLine("  scripting defines: ProjectSettings.asset not loadable — skipped");
                return;
            }

            var so = new SerializedObject(asset);
            so.Update();

            var dictionary = so.FindProperty("scriptingDefineSymbols");
            if (dictionary == null || !dictionary.isArray)
            {
                log.AppendLine("  scripting defines: layout not recognised — skipped");
                return;
            }

            bool changed = false;
            for (int i = 0; i < dictionary.arraySize; i++)
            {
                var entry = dictionary.GetArrayElementAtIndex(i);
                var target = entry.FindPropertyRelative("first");
                var defines = entry.FindPropertyRelative("second");
                if (target == null || defines == null)
                {
                    log.AppendLine("  scripting defines: unexpected entry layout — skipped");
                    return;
                }

                var kept = new List<string>();
                foreach (var part in defines.stringValue.Split(';'))
                {
                    var trimmed = part.Trim();
                    if (trimmed.Length > 0 && !stale.Contains(trimmed))
                        kept.Add(trimmed);
                }

                var rebuilt = string.Join(";", kept);
                if (rebuilt == defines.stringValue)
                    continue;

                log.AppendLine($"  {target.stringValue} defines -> '{rebuilt}'");
                defines.stringValue = rebuilt;
                changed = true;
            }

            if (!changed)
                log.AppendLine("  scripting defines: nothing stale");

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
        }

        static void StripConfigObjects(HashSet<string> stale, StringBuilder log)
        {
            var asset = LoadFirstAsset("ProjectSettings/EditorBuildSettings.asset");
            if (asset == null)
            {
                log.AppendLine("  build config objects: EditorBuildSettings.asset not loadable — skipped");
                return;
            }

            var so = new SerializedObject(asset);
            so.Update();

            var dictionary = so.FindProperty("m_configObjects");
            if (dictionary == null || !dictionary.isArray)
            {
                log.AppendLine("  build config objects: layout not recognised — skipped");
                return;
            }

            bool changed = false;
            for (int i = dictionary.arraySize - 1; i >= 0; i--)
            {
                var entry = dictionary.GetArrayElementAtIndex(i);
                var key = entry.FindPropertyRelative("first");
                if (key == null)
                {
                    log.AppendLine("  build config objects: unexpected entry layout — skipped");
                    break;
                }

                if (!stale.Contains(key.stringValue))
                    continue;

                log.AppendLine($"  removed config object '{key.stringValue}'");
                int before = dictionary.arraySize;
                dictionary.DeleteArrayElementAtIndex(i);
                // Entries holding an object reference are cleared first and only removed by a
                // second call, so the array has to be checked rather than assumed.
                if (dictionary.arraySize == before)
                    dictionary.DeleteArrayElementAtIndex(i);
                changed = true;
            }

            if (!changed)
                log.AppendLine("  build config objects: nothing stale");

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
        }

        static Object LoadFirstAsset(string path)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(path);
            return assets != null && assets.Length > 0 ? assets[0] : null;
        }

        // ---------------------------------------------------------------------------------
        // Verification
        // ---------------------------------------------------------------------------------

        static void Verify(StringBuilder log, List<Applied> applied)
        {
            if (applied.Count == 0)
                return;

            log.AppendLine();
            log.AppendLine("[Verification — re-read from the assets]");

            Object lastAsset = null;
            foreach (var entry in applied)
            {
                if (entry.asset != lastAsset)
                {
                    log.AppendLine($"  {entry.asset.name}");
                    lastAsset = entry.asset;
                }

                var so = new SerializedObject(entry.asset);
                var property = so.FindProperty(entry.path);
                string value;

                if (property == null)
                {
                    value = "<gone>";
                }
                else
                {
                    switch (property.propertyType)
                    {
                        case SerializedPropertyType.Boolean:
                            value = property.boolValue ? "on" : "off";
                            break;
                        case SerializedPropertyType.Integer:
                            value = property.intValue.ToString();
                            break;
                        case SerializedPropertyType.Enum:
                            value = property.enumDisplayNames[property.enumValueIndex];
                            break;
                        default:
                            value = property.propertyType.ToString();
                            break;
                    }
                }

                log.AppendLine($"      {entry.label,-26} = {value}");
            }
        }

        // ---------------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Finds assets that live in <c>Assets/</c> only.
        ///
        /// Packages ship their own copies of these asset types — URP in particular ships a
        /// template <c>UniversalRendererData</c> — and <c>AssetDatabase.FindAssets</c> returns
        /// them. Writing to them makes Unity report that "asset(s) located in immutable
        /// packages were unexpectedly altered", and the change can be silently reverted by any
        /// later Package Manager operation. Never touch anything outside <c>Assets/</c>.
        /// </summary>
        static IEnumerable<T> FindProjectAssets<T>(string filter) where T : Object
        {
            foreach (var guid in AssetDatabase.FindAssets(filter))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/", System.StringComparison.Ordinal))
                    continue;

                var asset = AssetDatabase.LoadAssetAtPath<T>(path);
                if (asset != null)
                    yield return asset;
            }
        }

        static T FindAsset<T>(string exactName) where T : Object
        {
            foreach (var guid in AssetDatabase.FindAssets($"t:{typeof(T).Name} {exactName}"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(path) == exactName)
                    return AssetDatabase.LoadAssetAtPath<T>(path);
            }
            return null;
        }

        static string Normalize(string s)
        {
            return s.Replace(" ", string.Empty)
                    .Replace("_", string.Empty)
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
        }

        struct Applied
        {
            public Object asset;
            public string path;
            public string label;
        }

        /// <summary>
        /// Writes properties by serialized name, trying each candidate in turn.
        ///
        /// Unity keeps most pipeline settings in private serialized fields with no public
        /// setter, and field names move between URP versions. Going through SerializedObject
        /// means a renamed field produces a legible failure listing what was tried, instead
        /// of a compile error or — worse — a silent no-op.
        /// </summary>
        sealed class Setter
        {
            readonly SerializedObject so;
            readonly StringBuilder log;
            readonly List<Applied> applied;

            public Setter(SerializedObject so, StringBuilder log, List<Applied> applied)
            {
                this.so = so;
                this.log = log;
                this.applied = applied;
            }

            public void Bool(string label, string[] names, bool value)
            {
                Write(label, names, p => p.boolValue = value);
            }

            public void Int(string label, string[] names, int value)
            {
                Write(label, names, p => p.intValue = value);
            }

            public void Enum(string label, string[] names, string valueName)
            {
                Write(label, names, p =>
                {
                    if (p.propertyType != SerializedPropertyType.Enum)
                    {
                        log.AppendLine($"      {label}: expected an enum, found {p.propertyType}");
                        return;
                    }

                    var displayNames = p.enumDisplayNames;
                    for (int i = 0; i < displayNames.Length; i++)
                    {
                        if (Normalize(displayNames[i]) == Normalize(valueName))
                        {
                            p.enumValueIndex = i;
                            return;
                        }
                    }

                    log.AppendLine($"      {label}: no value matching '{valueName}'. " +
                                   $"Available: {string.Join(" | ", displayNames)}");
                });
            }

            void Write(string label, string[] names, System.Action<SerializedProperty> apply)
            {
                foreach (var name in names)
                {
                    var property = so.FindProperty(name);
                    if (property == null)
                        continue;

                    apply(property);
                    log.AppendLine($"      {label,-26} set via {name}");
                    applied.Add(new Applied
                    {
                        asset = so.targetObject,
                        path = property.propertyPath,
                        label = label
                    });
                    return;
                }

                log.AppendLine($"      {label}: NOT FOUND — tried {string.Join(", ", names)}");
            }
        }
    }
}
