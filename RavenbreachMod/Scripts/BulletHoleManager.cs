using System.Collections.Generic;
using UnityEngine;

namespace RavenbreachMod
{
    /// <summary>
    /// Scans for bullet hole decals and:
    ///   1. Destroys ones on horizontal surfaces (floor/ceiling)
    ///   2. Replaces opaque black ones on walls with a subtle dark-brown tint
    ///
    /// Also logs DecalManager methods on first run so we can patch the source
    /// directly in a future session.
    /// </summary>
    public class BulletHoleManager : MonoBehaviour
    {
        private const float SCAN_INTERVAL = 0.15f;
        private float _scanTimer;
        private readonly HashSet<int> _processed = new HashSet<int>();

        private void Start()
        {
            // Diagnostic: enumerate DecalManager methods for future direct patching
            try
            {
                var dmType = HarmonyLib.AccessTools.TypeByName("DecalManager");
                if (dmType != null)
                {
                    Plugin.Log?.LogInfo("[Ravenbreach] DecalManager methods:");
                    foreach (var m in dmType.GetMethods(
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Static))
                    {
                        Plugin.Log?.LogInfo($"  {m.ReturnType.Name} {m.Name}(" +
                            string.Join(", ", System.Array.ConvertAll(
                                m.GetParameters(), p => p.ParameterType.Name + " " + p.Name)) + ")");
                    }
                }
            }
            catch { }
        }

        private void Update()
        {
            _scanTimer -= Time.deltaTime;
            if (_scanTimer > 0f) return;
            _scanTimer = SCAN_INTERVAL;
            ScanForBulletHoles();
        }

        private void ScanForBulletHoles()
        {
            var renderers = Object.FindObjectsOfType<MeshRenderer>();
            foreach (var r in renderers)
            {
                if (r == null) continue;
                int id = r.GetInstanceID();
                if (_processed.Contains(id)) continue;
                if (!LooksBulletHole(r)) continue;
                _processed.Add(id);
                ProcessBulletHole(r);
            }
        }

        private static bool LooksBulletHole(MeshRenderer r)
        {
            string name = r.gameObject.name.ToLower();

            bool nameMatch = name.Contains("bullet") || name.Contains("hole") ||
                             name.Contains("decal")  || name.Contains("impact");
            if (!nameMatch && r.sharedMaterial != null)
            {
                string mat = r.sharedMaterial.name.ToLower();
                nameMatch = mat.Contains("bullet") || mat.Contains("hole") ||
                            mat.Contains("decal")  || mat.Contains("impact");
            }
            if (!nameMatch) return false;

            // Must be small
            Vector3 s = r.transform.lossyScale;
            if (Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)) > 1.5f) return false;

            // FIX: Skip physics objects (grenades, projectiles, actors)
            // Bullet hole decals are purely static — no Rigidbody anywhere in hierarchy
            if (r.GetComponentInParent<Rigidbody>()          != null) return false;
            if (r.GetComponentInParent<Actor>()              != null) return false;
            if (r.GetComponentInParent<ExplodingProjectile>()!= null) return false;

            // No collider on the object itself (decals are visual-only)
            if (r.GetComponent<Collider>() != null) return false;

            return true;
        }

        private static void ProcessBulletHole(MeshRenderer r)
        {
            Vector3 normal = r.transform.forward;

            // Floor or ceiling → remove entirely
            if (Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.6f)
            {
                Object.Destroy(r.gameObject);
                return;
            }

            // Wall → replace black with subtle dark-brown tint
            try
            {
                var mat = r.material;
                if (mat != null)
                    mat.color = new Color(0.18f, 0.13f, 0.08f, 0.55f);
            }
            catch { }
        }
    }
}
