using System.Collections.Generic;
using UnityEngine;

// ──────────────────────────────────────────────────────────────────────────────
// OverpressureSystem
//
// Simulates blast pressure venting through geometry openings.
// On explosion, rays are cast outward to find surfaces. Where a ray passes
// through an opening (hits geometry on one side, open air behind), pressure
// vents outward — spawning volumetric smoke and throwing nearby actors.
//
// Called from ExplosionSuppressionPatch.Postfix via:
//   OverpressureSystem.Simulate(position, blastRadius, type)
// ──────────────────────────────────────────────────────────────────────────────

namespace RavenbreachMod
{
    public static class OverpressureSystem
    {
        // ── Tuning ────────────────────────────────────────────────────────────
        private const int   RAY_COUNT          = 32;    // rays cast per explosion
        private const float OPEN_AIR_CHECK     = 2.5f;  // meters behind hit to check for opening
        private const float MAX_VENTS          = 5;     // cap vents per explosion
        private const float VENT_ACTOR_RADIUS  = 6f;    // throw actors within this of vent point
        private const float VENT_THROW_BASE    = 280f;  // base knockback force at vent

        // Smoke texture decoded once and reused
        private static Texture2D _smokeTex;
        private static Material  _smokeMat;
        private static bool      _matReady = false;

        // ── Public entry point ────────────────────────────────────────────────

        public static void Simulate(Vector3 blastPos, float blastRadius, ExplosionType type)
        {
            EnsureMat();
            if (!_matReady) return;

            float typeScale = type == ExplosionType.Grenade ? 1f
                            : type == ExplosionType.Rocket  ? 1.8f
                            : 3.0f;  // artillery

            var vents = FindVentPoints(blastPos, blastRadius);
            if (vents.Count == 0) return;

            foreach (var vent in vents)
            {
                SpawnVolumetricSmoke(vent.point, vent.direction, blastPos, typeScale);
                ThrowActorsNearVent(vent.point, vent.direction, typeScale, blastPos);
            }
        }

        // ── Vent detection ────────────────────────────────────────────────────

        private struct VentPoint
        {
            public Vector3 point;
            public Vector3 direction;
        }

        private static List<VentPoint> FindVentPoints(Vector3 origin, float radius)
        {
            var vents = new List<VentPoint>(8);

            // Fibonacci sphere distribution for even ray coverage
            float goldenRatio = (1f + Mathf.Sqrt(5f)) / 2f;
            float angleIncr   = Mathf.PI * 2f * (goldenRatio - 1f);

            for (int i = 0; i < RAY_COUNT && vents.Count < MAX_VENTS; i++)
            {
                float t      = (float)i / RAY_COUNT;
                float inclIn = Mathf.Acos(1f - 2f * t);
                float azimut = angleIncr * i;

                Vector3 dir = new Vector3(
                    Mathf.Sin(inclIn) * Mathf.Cos(azimut),
                    Mathf.Cos(inclIn),
                    Mathf.Sin(inclIn) * Mathf.Sin(azimut));

                // Skip downward rays — no venting into the ground
                if (dir.y < -0.3f) continue;

                RaycastHit hit;
                if (!Physics.Raycast(origin, dir, out hit, radius)) continue;

                // Skip if we hit an actor
                if (hit.collider.GetComponentInParent<Actor>() != null) continue;

                // Check if there's open air BEHIND the hit surface
                // i.e. cast a short ray in the same direction past the hit point
                Vector3 behindOrigin = hit.point + dir * 0.15f;
                RaycastHit behindHit;
                bool isOpen = !Physics.Raycast(behindOrigin, dir, out behindHit, OPEN_AIR_CHECK);

                if (!isOpen) continue;  // solid geometry, pressure doesn't vent

                // This is a vent — pressure escapes through this opening
                // Check we haven't already added a very close vent
                bool tooClose = false;
                foreach (var v in vents)
                {
                    if (Vector3.Distance(v.point, hit.point) < 1.5f)
                    { tooClose = true; break; }
                }
                if (tooClose) continue;

                vents.Add(new VentPoint
                {
                    point     = hit.point,
                    direction = dir
                });
            }

            return vents;
        }

        // ── Volumetric smoke ──────────────────────────────────────────────────
        // 4 layered particle systems using the smokepuff1 Alpha texture.
        // Each layer has different size, speed, lifetime, alpha.
        // Together they read as a rolling volumetric cloud.

        private static void SpawnVolumetricSmoke(Vector3 ventPos, Vector3 ventDir,
                                                  Vector3 blastPos, float scale)
        {
            // Spawn slightly outside the surface
            Vector3 spawnPos = ventPos + ventDir * 0.3f;

            // Layer 0: dense inner core — slow, large, warm tint, rises
            SpawnSmokeLayer(spawnPos, ventDir, scale,
                count:     8,
                sizeMin:   scale * 0.9f,
                sizeMax:   scale * 2.2f,
                speedMin:  scale * 0.8f,
                speedMax:  scale * 2.5f,
                lifetime:  Mathf.Lerp(2.5f, 5.5f, scale / 3f),
                colorA:    new Color(0.72f, 0.60f, 0.42f, 0.55f),
                colorB:    new Color(0.65f, 0.55f, 0.38f, 0.35f),
                gravity:   -0.08f,
                spread:    18f);

            // Layer 1: rolling middle cloud — medium, neutral grey, drifts
            SpawnSmokeLayer(spawnPos, ventDir, scale,
                count:     12,
                sizeMin:   scale * 0.5f,
                sizeMax:   scale * 1.6f,
                speedMin:  scale * 1.5f,
                speedMax:  scale * 4.0f,
                lifetime:  Mathf.Lerp(2.0f, 4.0f, scale / 3f),
                colorA:    new Color(0.68f, 0.65f, 0.60f, 0.45f),
                colorB:    new Color(0.75f, 0.73f, 0.70f, 0.25f),
                gravity:   -0.05f,
                spread:    28f);

            // Layer 2: fast outer wisps — thin, light, dissipate quickly
            SpawnSmokeLayer(spawnPos, ventDir, scale,
                count:     16,
                sizeMin:   scale * 0.2f,
                sizeMax:   scale * 0.9f,
                speedMin:  scale * 3.0f,
                speedMax:  scale * 7.0f,
                lifetime:  Mathf.Lerp(0.8f, 2.0f, scale / 3f),
                colorA:    new Color(0.82f, 0.80f, 0.76f, 0.30f),
                colorB:    new Color(0.88f, 0.87f, 0.85f, 0.10f),
                gravity:   0.0f,
                spread:    40f);

            // Layer 3: debris specks — fast, dark, heavy gravity
            SpawnDebrisLayer(spawnPos, ventDir, scale);
        }

        private static void SpawnSmokeLayer(
            Vector3 pos, Vector3 dir, float scale,
            int count, float sizeMin, float sizeMax,
            float speedMin, float speedMax,
            float lifetime, Color colorA, Color colorB,
            float gravity, float spread)
        {
            var go = new GameObject("OPSmoke");
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(dir);
            Object.Destroy(go, lifetime + 1.5f);

            var ps   = go.AddComponent<ParticleSystem>();
            var rend = go.GetComponent<ParticleSystemRenderer>();
            rend.renderMode = ParticleSystemRenderMode.Billboard;
            rend.material   = new Material(_smokeMat);

            // Random per-particle rotation for organic look
            rend.enableGPUInstancing = false;

            var main = ps.main;
            main.startLifetime      = new ParticleSystem.MinMaxCurve(lifetime * 0.5f, lifetime);
            main.startSpeed         = new ParticleSystem.MinMaxCurve(speedMin, speedMax);
            main.startSize          = new ParticleSystem.MinMaxCurve(sizeMin, sizeMax);
            main.startColor         = new ParticleSystem.MinMaxGradient(colorA, colorB);
            main.startRotation      = new ParticleSystem.MinMaxCurve(0f, 360f * Mathf.Deg2Rad);
            main.gravityModifier    = gravity;
            main.maxParticles       = count + 4;
            main.simulationSpace    = ParticleSystemSimulationSpace.World;
            main.stopAction         = ParticleSystemStopAction.Destroy;

            // Cone shape pointing in vent direction
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle     = spread;
            shape.radius    = 0.1f;
            shape.rotation  = new Vector3(-90f, 0f, 0f);  // cone opens forward (+Z)

            // Rotation over lifetime for turbulent swirl
            var rol = ps.rotationOverLifetime;
            rol.enabled = true;
            rol.z       = new ParticleSystem.MinMaxCurve(
                -45f * Mathf.Deg2Rad, 45f * Mathf.Deg2Rad);

            // Size over lifetime — expand then slowly shrink
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size    = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.2f),
                new Keyframe(0.15f, 1.0f),
                new Keyframe(0.7f,  1.1f),
                new Keyframe(1f,    0.6f)));

            // Alpha fade: punch in fast, hold, fade out
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(colorA.r, colorA.g, colorA.b), 0f),
                    new GradientColorKey(new Color(colorB.r * 1.1f, colorB.g * 1.1f, colorB.b * 1.1f), 0.5f),
                    new GradientColorKey(new Color(0.85f, 0.85f, 0.83f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0f,          0f),
                    new GradientAlphaKey(colorA.a,    0.06f),
                    new GradientAlphaKey(colorA.a,    0.4f),
                    new GradientAlphaKey(colorB.a,    0.7f),
                    new GradientAlphaKey(0f,          1f)
                });
            col.color = new ParticleSystem.MinMaxGradient(grad);

            // Noise for organic turbulence
            var noise = ps.noise;
            noise.enabled   = true;
            noise.strength  = new ParticleSystem.MinMaxCurve(scale * 0.3f);
            noise.frequency = 0.4f;
            noise.quality   = ParticleSystemNoiseQuality.Medium;

            var emission = ps.emission;
            emission.enabled = true;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, count) });

            ps.Play();
        }

        private static void SpawnDebrisLayer(Vector3 pos, Vector3 dir, float scale)
        {
            int count = Mathf.RoundToInt(Mathf.Lerp(12f, 35f, scale / 3f));

            var go = new GameObject("OPDebris");
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(dir);
            Object.Destroy(go, 1.5f);

            var ps   = go.AddComponent<ParticleSystem>();
            var rend = go.GetComponent<ParticleSystemRenderer>();
            rend.renderMode = ParticleSystemRenderMode.Billboard;

            // Reuse smokeMat with dark tint for debris
            var debrisMat   = new Material(_smokeMat);
            debrisMat.color = new Color(0.22f, 0.18f, 0.12f, 0.9f);
            rend.material   = debrisMat;

            var main = ps.main;
            main.startLifetime   = new ParticleSystem.MinMaxCurve(0.3f, 1.2f);
            main.startSpeed      = new ParticleSystem.MinMaxCurve(scale * 4f, scale * 12f);
            main.startSize       = new ParticleSystem.MinMaxCurve(0.02f, 0.12f);
            main.startColor      = new ParticleSystem.MinMaxGradient(
                new Color(0.28f, 0.22f, 0.14f, 0.95f),
                new Color(0.18f, 0.14f, 0.09f, 0.80f));
            main.gravityModifier = 3.5f;
            main.maxParticles    = count + 10;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.stopAction      = ParticleSystemStopAction.Destroy;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle     = 35f;
            shape.radius    = 0.05f;
            shape.rotation  = new Vector3(-90f, 0f, 0f);

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.28f, 0.22f, 0.14f), 0f),
                    new GradientColorKey(new Color(0.15f, 0.12f, 0.08f), 1f)
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

        // ── Actor throwing ─────────────────────────────────────────────────────
        // Actors near a vent point get thrown in the vent direction.
        // More force = closer to vent, larger explosion.

        private static void ThrowActorsNearVent(Vector3 ventPos, Vector3 ventDir,
                                                 float scale, Vector3 blastPos)
        {
            var actors = ActorManager.instance?.actors;
            if (actors == null) return;

            foreach (var actor in actors)
            {
                if (actor == null || actor.dead) continue;

                float distToVent = Vector3.Distance(actor.Position(), ventPos);
                if (distToVent > VENT_ACTOR_RADIUS) continue;

                // Force falls off with distance from vent
                float falloff = 1f - distToVent / VENT_ACTOR_RADIUS;
                float force   = VENT_THROW_BASE * scale * falloff;

                // Direction is a mix of vent direction and upward — actors get
                // thrown through the opening and up, not just sideways
                Vector3 throwDir = (ventDir * 0.7f + Vector3.up * 0.3f).normalized;

                try
                {
                    if (actor.aiControlled)
                    {
                        // Bots: apply suppression spike + KnockOver
                        var sup = actor.GetComponentInParent<SuppressionSystem>()
                               ?? actor.GetComponent<SuppressionSystem>();
                        sup?.AddSuppression(25f * scale);
                        actor.KnockOver(throwDir * force);
                    }
                    else
                    {
                        // Player: ragdoll
                        var fps = FpsActorController.instance;
                        if (fps != null && fps.actor == actor)
                        {
                            float dur = Mathf.Lerp(0.6f, 2.0f, falloff * scale / 3f);
                            SprintStumbleSystem.TriggerPlayerRagdoll(fps, dur);
                        }
                    }
                }
                catch { }
            }
        }

        // ── Material setup ────────────────────────────────────────────────────
        // Uses the captured smokepuff1 Alpha texture from MistTextureData.

        private static void EnsureMat()
        {
            if (_matReady) return;

            try
            {
                byte[] pngBytes = System.Convert.FromBase64String(MistTextureData.PNG_B64);
                _smokeTex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                ImageConversion.LoadImage(_smokeTex, pngBytes);
                _smokeTex.filterMode = FilterMode.Bilinear;
                _smokeTex.name       = "OPSmokeTex";

                var shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended")
                          ?? Shader.Find("Particles/Alpha Blended")
                          ?? Shader.Find("Sprites/Default");

                if (shader == null)
                {
                    Plugin.Log?.LogWarning("[Overpressure] No smoke shader found");
                    return;
                }

                _smokeMat             = new Material(shader);
                _smokeMat.mainTexture = _smokeTex;
                _smokeMat.color       = Color.white;
                _smokeMat.renderQueue = 3000;

                _matReady = true;
                Plugin.Log?.LogInfo("[Overpressure] Smoke material ready.");
            }
            catch (System.Exception e)
            {
                Plugin.Log?.LogError("[Overpressure] EnsureMat failed: " + e.Message);
            }
        }
    }
}
