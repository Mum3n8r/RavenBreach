using UnityEngine;
using RavenbreachMod;  // Fixed: was missing — SuppressionSystem lives in this namespace

/// <summary>
/// Utility component that adds a SuppressionSystem to any actor
/// that doesn't already have one. Drop on actor prefabs or use
/// from Plugin's Harmony spawn patches.
/// </summary>
public class RavenfieldSuppression : MonoBehaviour
{
    [Header("Defaults applied when adding SuppressionSystem")]
    public float decayRate  = 10f;
    public float decayDelay = 1.5f;

    private void Awake()
    {
        if (GetComponent<SuppressionSystem>() != null) return;

        var sys = gameObject.AddComponent<SuppressionSystem>();
        sys.decayRate  = decayRate;
        sys.decayDelay = decayDelay;
    }
}
