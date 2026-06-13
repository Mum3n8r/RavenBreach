using UnityEngine;

namespace RavenbreachMod
{
    public class ProceduralSoundBank : MonoBehaviour
    {
        private void Start()
        {
            // Thump disabled for testing — re-enable once real clips are sourced
            // var sys = SoundPropagationSystem.Instance;
            // if (sys == null) return;
            // if (sys.DistantThumpClip == null)
            //     sys.DistantThumpClip = BuildThump();
        }
    }
}
