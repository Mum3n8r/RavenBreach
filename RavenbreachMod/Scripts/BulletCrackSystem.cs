using System.Collections;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace RavenbreachMod
{
    public class BulletCrackSystem : MonoBehaviour
    {
        public static AudioClip Crack { get; private set; }
        public static AudioClip Whizz { get; private set; }
        public static bool      Ready { get; private set; }
        private static BulletCrackSystem _inst;
        private static readonly string ASSETS_DIR = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "RavenbreachAssets");

        private void Awake() { _inst = this; BulletAudioPool.Init(gameObject); StartCoroutine(LoadClips()); }
        private void OnDestroy() { if (_inst == this) _inst = null; Ready = false; if (Crack != null) { Destroy(Crack); Crack = null; } if (Whizz != null) { Destroy(Whizz); Whizz = null; } }

        private IEnumerator LoadClips()
        {
            string cp = Path.Combine(ASSETS_DIR, "bullet_crack.wav"), wp = Path.Combine(ASSETS_DIR, "bullet_whizz.wav");
            if (!File.Exists(cp) || !File.Exists(wp)) { Plugin.Log?.LogWarning("[BulletCrack] WAVs not found."); yield break; }
            var r1 = UnityWebRequestMultimedia.GetAudioClip("file://" + cp, AudioType.WAV); yield return r1.SendWebRequest();
            if (r1.result == UnityWebRequest.Result.Success) { Crack = DownloadHandlerAudioClip.GetContent(r1); Crack.name = "bullet_crack"; } r1.Dispose();
            var r2 = UnityWebRequestMultimedia.GetAudioClip("file://" + wp, AudioType.WAV); yield return r2.SendWebRequest();
            if (r2.result == UnityWebRequest.Result.Success) { Whizz = DownloadHandlerAudioClip.GetContent(r2); Whizz.name = "bullet_whizz"; } r2.Dispose();
            if (Crack != null && Whizz != null) { Ready = true; Plugin.Log?.LogInfo("[BulletCrack] Ready."); }
        }
    }

    public static class BulletAudioPool
    {
        private static AudioSource[] _crackSrcs, _whizzSrcs;
        private const int POOL = 6;
        public static void Init(GameObject go)
        {
            _crackSrcs = new AudioSource[POOL];
            _whizzSrcs = new AudioSource[POOL];
            for (int i = 0; i < POOL; i++)
            {
                var c = go.AddComponent<AudioSource>(); c.playOnAwake=false; c.loop=false; c.spatialBlend=0f; c.reverbZoneMix=0f; c.dopplerLevel=0f; _crackSrcs[i]=c;
                var w = go.AddComponent<AudioSource>(); w.playOnAwake=false; w.loop=false; w.spatialBlend=0f; w.reverbZoneMix=0f; w.dopplerLevel=0f; _whizzSrcs[i]=w;
            }
        }
        private static int _crackIdx, _whizzIdx;
        public static void PlayCrack(float v)
        {
            if (BulletCrackSystem.Crack == null) return;
            var src = _crackSrcs[_crackIdx++ % POOL];
            // Pitch variation: ±8% so rapid fire doesn't sound like one repeated sample
            src.pitch  = UnityEngine.Random.Range(0.92f, 1.08f);
            // EQ approximation: boost highs by raising volume and slightly cutting low-end
            // via pitch — crack is a transient, we just want it punchy and bright
            src.volume = Mathf.Clamp(v * 1.15f, 0f, 1f);
            src.PlayOneShot(BulletCrackSystem.Crack, Mathf.Clamp(v * 1.15f, 0f, 1f));
        }
        public static void PlayWhizz(float v)
        {
            if (BulletCrackSystem.Whizz == null) return;
            var src = _whizzSrcs[_whizzIdx++ % POOL];
            // Whisper-level whizz — should be barely audible, just directional awareness
            src.pitch  = UnityEngine.Random.Range(0.85f, 1.18f); // wider pitch range = more variety
            src.volume = Mathf.Clamp(v * 0.18f, 0f, 0.22f);     // hard cap at 0.22 regardless of input
            src.PlayOneShot(BulletCrackSystem.Whizz, src.volume);
        }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.PlayOneShot), new[] { typeof(AudioClip), typeof(float) })]
    public static class BulletFlybySoundPatch
    {
        private const float CLOSE_THRESHOLD = 0.35f;
        static bool Prefix(ref AudioClip clip, ref float volumeScale)
        {
            if (clip == null) return true;
            if (!clip.name.StartsWith("Bullet Flyby", System.StringComparison.OrdinalIgnoreCase)) return true;
            if (!BulletCrackSystem.Ready) { volumeScale = Mathf.Clamp(volumeScale * 2.2f, 0f, 1f); return true; }
            bool isClose = volumeScale >= CLOSE_THRESHOLD;
            if (isClose)
            {
                BulletAudioPool.PlayCrack(Mathf.Clamp(volumeScale * 3.8f, 0f, 1f));
                BulletAudioPool.PlayWhizz(volumeScale); // pool handles the whisper cap internally
            }
            else
            {
                BulletAudioPool.PlayWhizz(volumeScale * 0.6f); // distant pass, even quieter
            }
            return false;
        }
    }
}