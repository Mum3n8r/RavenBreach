using HarmonyLib;
using UnityEngine;

namespace RavenbreachMod
{
    [HarmonyPatch(typeof(DecalManager), "StartGame")]
    public static class DecalManagerStartPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            BloodParticle.BLOOD_PARTICLE_SETTING = BloodParticle.BloodParticleType.None;
            var blood = new Color(0.22f, 0.015f, 0.015f, 1f);
            try { DecalManager.SetBloodDecalColor(0, blood); } catch { }
            try { DecalManager.SetBloodDecalColor(1, blood); } catch { }
        }
    }

    [HarmonyPatch(typeof(DecalManager), "EmitBloodEffect")]
    public static class SuppressVanillaBloodEffectPatch
    {
        [HarmonyPrefix]
        public static bool Prefix() => false;
    }

    // Suppress vanilla blood particle spheres without destroying the GameObject.
    // Previously called Object.Destroy(__instance) which could destroy GameObjects
    // that Ravenfield reuses for parachute/reinforcement drop sequences, breaking spawns.
    // Now we just disable the component — no Update calls, no side effects.
    [HarmonyPatch(typeof(BloodParticle), "Update")]
    public static class SuppressBloodParticleSpherePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(BloodParticle __instance)
        {
            if (__instance != null)
                __instance.enabled = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Actor), "Damage")]
    public static class ActorDamagePatch
    {
        [HarmonyPostfix]
        public static void Postfix(Actor __instance, DamageInfo info)
        {
            if (info.type == DamageInfo.DamageSourceType.FallDamage) return;
            if (info.type == DamageInfo.DamageSourceType.DamageZone) return;
            if (info.type == DamageInfo.DamageSourceType.Scripted)   return;
            if (info.healthDamage < 0.5f) return;
            try { GoreSystem.OnHit(__instance, info); } catch { }
        }
    }

    [HarmonyPatch(typeof(Actor), "Kill")]
    public static class ActorKillPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Actor __instance, DamageInfo info)
        {
            if (info.type == DamageInfo.DamageSourceType.Scripted)   return;
            if (info.type == DamageInfo.DamageSourceType.DamageZone) return;
            if (info.type == DamageInfo.DamageSourceType.Exception)  return;
            try { GoreSystem.OnDeath(__instance); } catch { }
        }
    }

    public static class GoreSystem
    {
        private static Texture2D _dropletTex;
        private static Material  _particleMat;
        private static Material  _poolMat;

        public static void PlaceDecal(Vector3 point, Vector3 normal, int team, float size)
        {
            DecalManager.AddDecal(point, normal, size,
                team == 0 ? DecalManager.DecalType.BloodBlue : DecalManager.DecalType.BloodRed);
        }

        public static void OnHit(Actor actor, DamageInfo info)
        {
            EnsureMat();

            Vector3 pos = info.point != Vector3.zero ? info.point : actor.Position() + Vector3.up;

            Vector3 hitDir = info.direction.normalized;
            if (hitDir.magnitude < 0.01f && info.sourceActor != null)
                hitDir = (pos - info.sourceActor.Position()).normalized;
            if (hitDir.magnitude < 0.01f)
                hitDir = Random.onUnitSphere;

            float scale = Mathf.Clamp01(info.healthDamage / 60f);

            SpawnBloodBurst(pos, hitDir, Mathf.RoundToInt(Mathf.Lerp(80f, 160f, scale)), scale);

            int decalDrops = Mathf.RoundToInt(Mathf.Lerp(4f, 12f, scale));
            for (int i = 0; i < decalDrops; i++)
            {
                Vector3 dropDir = (hitDir + Random.insideUnitSphere * 0.55f).normalized;
                RaycastHit dropHit;
                if (Physics.Raycast(pos + hitDir * 0.15f, dropDir, out dropHit, Random.Range(0.8f, 3.5f)) &&
                    dropHit.collider.GetComponentInParent<Actor>()   == null &&
                    dropHit.collider.GetComponentInParent<Vehicle>() == null &&
                    Vector3.Dot(dropHit.normal, -dropDir) > 0.25f)
                    PlaceDecal(dropHit.point, dropHit.normal, actor.team, Random.Range(0.05f, 0.22f));
            }

            RaycastHit wallHit;
            if (Physics.Raycast(pos + hitDir * 0.15f, hitDir, out wallHit, 5f) &&
                wallHit.collider.GetComponentInParent<Actor>()   == null &&
                wallHit.collider.GetComponentInParent<Vehicle>() == null &&
                Vector3.Dot(wallHit.normal, Vector3.up) < 0.7f &&
                Vector3.Dot(wallHit.normal, -hitDir) > 0.25f)
            {
                int wallSplats = Mathf.RoundToInt(Mathf.Lerp(1f, 4f, scale));
                for (int i = 0; i < wallSplats; i++)
                    PlaceDecal(wallHit.point + Random.insideUnitSphere * 0.18f * scale,
                               wallHit.normal, actor.team, Random.Range(0.06f, 0.28f));
            }
        }

        public static void OnDeath(Actor actor)
        {
            EnsureMat();
            Vector3 pos = actor.Position() + Vector3.up * 0.8f;

            SpawnBloodBurst(pos, Vector3.up * 0.5f, 120, 1f);

            for (int i = 0; i < 10; i++)
            {
                Vector3 dir = Random.onUnitSphere;
                dir.y = dir.y * 0.3f - 0.4f; dir.Normalize();
                RaycastHit dHit;
                if (Physics.Raycast(pos, dir, out dHit, 5f) &&
                    dHit.collider.GetComponentInParent<Actor>()   == null &&
                    dHit.collider.GetComponentInParent<Vehicle>() == null &&
                    Vector3.Dot(dHit.normal, -dir) > 0.2f)
                    PlaceDecal(dHit.point, dHit.normal, actor.team, Random.Range(0.08f, 0.32f));
            }

            SpawnPool(actor);
        }

        private static void SpawnBloodBurst(Vector3 pos, Vector3 dir, int count, float scale)
        {
            if (_particleMat == null) return;
            var go = new GameObject("BloodBurst");
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(dir.magnitude > 0.01f ? dir : Vector3.up);
            Object.Destroy(go, 0.5f);

            var ps   = go.AddComponent<ParticleSystem>();
            var rend = go.GetComponent<ParticleSystemRenderer>();
            rend.renderMode = ParticleSystemRenderMode.Billboard;
            rend.material   = new Material(_particleMat);

            var main = ps.main;
            main.startLifetime   = new ParticleSystem.MinMaxCurve(0.06f, 0.22f);
            main.startSpeed      = new ParticleSystem.MinMaxCurve(1.5f, Mathf.Lerp(4f, 10f, scale));
            main.startSize       = new ParticleSystem.MinMaxCurve(0.006f, 0.018f);
            main.startColor      = new ParticleSystem.MinMaxGradient(
                new Color(0.55f, 0.010f, 0.010f, 0.95f),
                new Color(0.35f, 0.005f, 0.005f, 0.85f));
            main.gravityModifier = 4.0f;
            main.maxParticles    = count;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.stopAction      = ParticleSystemStopAction.Destroy;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle     = 20f;
            shape.radius    = 0.02f;
            shape.rotation  = new Vector3(-90f, 0f, 0f);

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.60f, 0.012f, 0.012f), 0f),
                    new GradientColorKey(new Color(0.25f, 0.005f, 0.005f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0.95f, 0f),
                    new GradientAlphaKey(0.60f, 0.5f),
                    new GradientAlphaKey(0f,    1f)
                });
            col.color = new ParticleSystem.MinMaxGradient(grad);

            var emission = ps.emission;
            emission.enabled = true;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, count) });
            ps.Play();
        }

        private static void SpawnPool(Actor actor)
        {
            if (_poolMat == null) return;
            Vector3 origin = actor.transform.position + Vector3.up * 0.05f;
            RaycastHit hit;
            if (Physics.Raycast(origin, Vector3.down, out hit, 2f))
                origin = hit.point + Vector3.up * 0.003f;
            BloodPoolGrow.Spawn(origin);
        }

        public static void EnsureMat()
        {
            if (_particleMat != null && _poolMat != null) return;

            if (_dropletTex == null)
            {
                _dropletTex = new Texture2D(32, 32, TextureFormat.RGBA32, false);
                for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    float dx = (x - 15.5f) / 15.5f, dy = (y - 15.5f) / 15.5f;
                    float a  = Mathf.Clamp01(1f - Mathf.Pow(Mathf.Sqrt(dx*dx + dy*dy), 1.4f));
                    _dropletTex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
                _dropletTex.filterMode = FilterMode.Bilinear;
                _dropletTex.Apply();
            }

            if (_particleMat == null)
            {
                var sh = Shader.Find("Particles/Alpha Blended")
                      ?? Shader.Find("Particles/Standard Unlit")
                      ?? Shader.Find("Mobile/Particles/Alpha Blended")
                      ?? Shader.Find("Unlit/Transparent");
                if (sh != null)
                {
                    _particleMat             = new Material(sh);
                    _particleMat.mainTexture = _dropletTex;
                    _particleMat.renderQueue = 3000;
                }
            }

            if (_poolMat == null)
            {
                var sh = Shader.Find("Unlit/Transparent") ?? Shader.Find("Sprites/Default");
                if (sh != null)
                {
                    _poolMat             = new Material(sh);
                    _poolMat.color       = new Color(0.10f, 0.004f, 0.004f, 0.88f);
                    _poolMat.renderQueue = 3000;
                }
            }
        }
    }

    // Stub — full implementation deferred until spawn/AI issues are resolved
    public class BleedingSystem : MonoBehaviour
    {
        public static void RegisterWound(Actor actor, float intensity, BodyPart part) { }
        public static void RemoveActor(Actor actor) { }
        public static bool IsActorBleeding(Actor actor, BodyPart part) => false;
    }

    public class BloodPoolGrow : MonoBehaviour
    {
        private static readonly System.Collections.Generic.Queue<BloodPoolGrow> _pool
            = new System.Collections.Generic.Queue<BloodPoolGrow>();
        private const int MAX_POOLS = 24;

        public static void Spawn(Vector3 origin)
        {
            if (_pool.Count >= MAX_POOLS)
            {
                var oldest = _pool.Dequeue();
                if (oldest != null) Destroy(oldest.gameObject);
            }
            var go = new GameObject("BloodPool");
            go.transform.position = origin;
            go.transform.rotation = Quaternion.Euler(90f, Random.Range(0f, 360f), 0f);
            var bg = go.AddComponent<BloodPoolGrow>();
            _pool.Enqueue(bg);
        }
        private MeshRenderer _rend;
        private float _t, _age;

        private void Start()
        {
            var mf = gameObject.AddComponent<MeshFilter>();
            mf.mesh = BuildCircle(32);
            _rend   = gameObject.AddComponent<MeshRenderer>();
            var sh  = Shader.Find("Unlit/Transparent") ?? Shader.Find("Sprites/Default");
            if (sh != null)
            {
                _rend.material             = new Material(sh);
                _rend.material.color       = new Color(0.10f, 0.004f, 0.004f, 0.88f);
                _rend.material.renderQueue = 3000;
            }
            _rend.receiveShadows    = false;
            _rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            transform.localScale    = Vector3.one * 0.04f;
        }

        private void Update()
        {
            _t += Time.deltaTime; _age += Time.deltaTime;
            if (_t < 4f)
            {
                float r = Mathf.Lerp(0.04f, 0.60f, Mathf.Sqrt(_t / 4f));
                transform.localScale = new Vector3(r, r, 1f);
            }
            if (_age > 30f && _rend != null && _rend.material != null)
            {
                float a = Mathf.Lerp(0.88f, 0f, (_age - 30f) / 6f);
                var c = _rend.material.color;
                _rend.material.color = new Color(c.r, c.g, c.b, a);
                if (a <= 0f) Destroy(gameObject);
            }
        }

        private static Mesh BuildCircle(int seg)
        {
            var mesh = new Mesh();
            var v = new Vector3[seg + 1]; var t = new int[seg * 3]; var u = new Vector2[seg + 1];
            v[0] = Vector3.zero; u[0] = new Vector2(0.5f, 0.5f);
            for (int i = 0; i < seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                v[i+1] = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                u[i+1] = new Vector2(v[i+1].x * 0.5f + 0.5f, v[i+1].y * 0.5f + 0.5f);
                t[i*3] = 0; t[i*3+1] = i+1; t[i*3+2] = (i+1) % seg + 1;
            }
            mesh.vertices = v; mesh.triangles = t; mesh.uv = u;
            mesh.RecalculateNormals();
            return mesh;
        }
    }
}
