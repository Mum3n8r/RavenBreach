using System.Collections.Generic;
using UnityEngine;

namespace RavenbreachMod
{
    // ── RavenbreachAssetLoader ─────────────────────────────────────────────────
    // Loads the ravenbreach_anims asset bundle at runtime and caches clips.
    // Bundle is built by D:\RavenbreachEditor and deployed to:
    //   D:\SteamLibrary\...\BepInEx\plugins\RavenbreachAssets\ravenbreach_anims
    //
    // Usage:
    //   var clip = RavenbreachAssetLoader.GetClip("rb_reload");
    // ──────────────────────────────────────────────────────────────────────────

    public static class RavenbreachAssetLoader
    {
        private static AssetBundle _animBundle;
        private static readonly Dictionary<string, AnimationClip> _clips
            = new Dictionary<string, AnimationClip>();

        private static bool _loaded   = false;
        private static bool _attempted = false;

        private static readonly string BUNDLE_PATH =
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location),
                "RavenbreachAssets", "ravenbreach_anims");

        public static void Init()
        {
            if (_attempted) return;
            _attempted = true;

            if (!System.IO.File.Exists(BUNDLE_PATH))
            {
                Plugin.Log?.LogWarning("[AssetLoader] Bundle not found at: " + BUNDLE_PATH);
                Plugin.Log?.LogWarning("[AssetLoader] Run D:\\RavenbreachEditor\\run_anim_job.bat build_all to generate.");
                return;
            }

            try
            {
                _animBundle = AssetBundle.LoadFromFile(BUNDLE_PATH);
                if (_animBundle == null)
                {
                    Plugin.Log?.LogError("[AssetLoader] Failed to load bundle.");
                    return;
                }

                foreach (var clip in _animBundle.LoadAllAssets<AnimationClip>())
                {
                    _clips[clip.name] = clip;
                    Plugin.Log?.LogInfo("[AssetLoader] Loaded clip: " + clip.name
                        + " (" + clip.length.ToString("F2") + "s)");
                }

                _loaded = true;
                Plugin.Log?.LogInfo("[AssetLoader] Bundle loaded. " + _clips.Count + " clips.");
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogError("[AssetLoader] Exception: " + ex.Message);
            }
        }

        public static AnimationClip GetClip(string name)
        {
            if (!_loaded) return null;
            _clips.TryGetValue(name, out var clip);
            return clip;
        }

        public static bool IsLoaded => _loaded;

        public static void Unload()
        {
            _clips.Clear();
            _animBundle?.Unload(true);
            _animBundle = null;
            _loaded = false;
            _attempted = false;
        }
    }
}
