using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DeadPines.EditorTools
{
    /// <summary>
    /// TECHNICAL.md §11 "Textures" — default import size 1024², with 2048² for terrain and
    /// hero assets only; albedo → BC1, normals → BC5, packed masks → BC4.
    ///
    /// Unity has no project-wide default-texture-import asset, so this is the mechanism: an
    /// AssetPostprocessor that stamps every texture on the way in. Textures that were already
    /// imported when this landed are picked up by the menu item below.
    ///
    /// §2 owns the numbers: "1024² is the default texture size for this game. 2048² for
    /// terrain layers and hero assets only. 512² for clutter." About 750 material sets fit at
    /// 1024² and only ~190 at 2048², out of ~1.7–2.3 GB of available texture memory.
    /// </summary>
    public class TextureImportDefaults : AssetPostprocessor
    {
        /// Bump this when the rules below change, or already-stamped textures will keep the
        /// old settings forever.
        const string Marker = "deadpines.tex.v1";

        const int DefaultSize = 1024;
        const int HeroSize = 2048;

        [MenuItem("Dead Pines/Phase 0 — Reimport All Textures", priority = 1)]
        public static void ReimportAll()
        {
            var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets" });
            var log = new StringBuilder();

            log.AppendLine($"Reimporting {guids.Length} textures through the Dead Pines rules…");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }

            AssetDatabase.Refresh();
            log.AppendLine("Done. Anything already stamped was left alone — see the marker note.");
            Debug.Log(log.ToString());
        }

        void OnPreprocessTexture()
        {
            if (!(assetImporter is TextureImporter importer))
                return;

            // Only unstamped textures. Once a texture carries the marker it is never touched
            // again, so a deliberate manual override in the inspector survives both this
            // postprocessor and the reimport menu item.
            if (importer.userData == Marker)
                return;

            // Leave sprites, cursors and anything already declared a Normal Map alone. Unity
            // compresses those correctly on its own, and overriding them here would be wrong
            // in ways that are hard to see.
            if (importer.textureType != TextureImporterType.Default)
                return;

            var path = assetPath.Replace('\\', '/');
            var lower = path.ToLowerInvariant();
            var name = System.IO.Path.GetFileNameWithoutExtension(lower);

            // HDR sources stay HDR. The overcast sky HDRI is load-bearing (§7) and its whole
            // point is range the SDR pipeline then tonemaps — compressing it to 8-bit BC1
            // would flatten exactly the thing it is there to provide. §2 gives the sky its
            // own format (BC7) for this reason, and it is a decision to make deliberately
            // when the sky lands, not one to have made by a filename convention.
            var extension = System.IO.Path.GetExtension(lower);
            if (extension == ".hdr" || extension == ".exr")
                return;

            // §11 — 2048² for terrain and hero only. Everything else is 1024².
            bool hero = lower.Contains("/terrain/") || lower.Contains("/hero/");
            int maxSize = hero ? HeroSize : DefaultSize;

            var format = ChooseFormat(name, out var formatName);

            importer.maxTextureSize = maxSize;
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.crunchedCompression = false;
            // §2 — mipmap streaming is on project-wide, so textures need mips to stream.
            importer.mipmapEnabled = true;
            importer.streamingMipmaps = true;

            // §2 — "set the platform override now, not later". A per-platform override is what
            // actually decides the shipped format; the base settings above only seed it.
            var platform = new TextureImporterPlatformSettings
            {
                name = "Standalone",
                overridden = true,
                maxTextureSize = maxSize,
                format = format,
                textureCompression = TextureImporterCompression.Compressed,
                crunchedCompression = false,
            };
            importer.SetPlatformTextureSettings(platform);
            importer.userData = Marker;

            Debug.Log($"[Dead Pines] {path} → {maxSize}² {formatName}");
        }

        /// <summary>
        /// Picks a format from the file name. There is no metadata in a PNG saying which slot
        /// it belongs in, so this is a naming convention — and naming conventions are exactly
        /// the kind of assumption that should be visible rather than buried. Every texture it
        /// touches is logged by name, so a wrong guess is easy to spot.
        /// </summary>
        static TextureImporterFormat ChooseFormat(string nameLower, out string usedName)
        {
            if (ContainsAny(nameLower,
                    "_normal", "_nrm", "_nor", "_normalmap") || nameLower.EndsWith("_n"))
                return Resolve("BC5", out usedName);

            if (ContainsAny(nameLower,
                    "_mask", "_maskmap", "_metallic", "_metal", "_roughness", "_rough",
                    "_ao", "_occlusion", "_specular", "_smoothness", "_gloss"))
                return Resolve("BC4", out usedName);

            // §2 — albedo to BC1. DXT1 is the older spelling of the same thing, kept as a
            // fallback because the enum has been renamed across Unity versions.
            return Resolve(new[] { "BC1", "DXT1" }, out usedName);
        }

        static bool ContainsAny(string haystack, params string[] needles)
        {
            foreach (var needle in needles)
                if (haystack.Contains(needle))
                    return true;
            return false;
        }

        static TextureImporterFormat Resolve(string name, out string usedName)
        {
            return Resolve(new[] { name }, out usedName);
        }

        /// <summary>
        /// Looks the enum member up by name instead of referencing it directly, so a Unity
        /// version that spells it differently degrades to Automatic with a note rather than
        /// failing to compile.
        /// </summary>
        static TextureImporterFormat Resolve(string[] candidates, out string usedName)
        {
            foreach (var candidate in candidates)
            {
                if (!System.Enum.IsDefined(typeof(TextureImporterFormat), candidate))
                    continue;

                usedName = candidate;
                return (TextureImporterFormat)System.Enum.Parse(
                    typeof(TextureImporterFormat), candidate);
            }

            usedName = "Automatic (no match!)";
            return TextureImporterFormat.Automatic;
        }
    }
}
