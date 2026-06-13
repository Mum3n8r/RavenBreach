using UnityEngine;

namespace RavenbreachMod
{
    public enum ExplosionType { Grenade, Rocket, Artillery }

    public static class BlastParticleSystem
    {
        private static Texture2D _sandTex;
        private static Material  _sandMat;

        public static void Spawn(Vector3 blastPos, ExplosionType type)
        {
            // Ground particles — only if we hit terrain
            Vector3 rayOrigin = blastPos + Vector3.up * 2f;
            bool    hitGround = Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 10f)
                             && hit.collider.GetComponentInParent<Actor>() == null;
            Vector3 ground = hitGround ? hit.point : blastPos;

            try
            {
                EnsureMaterial();

                // Smoke/mist always spawns — it's the main visible blast effect
                // Size is BIG. Much bigger than you'd think.
                switch (type)
                {
                    case ExplosionType.Grenade:
                        if (hitGround) SpawnSandBurst(ground, 280, 8f, 1.4f, 45f, 0.8f);
                        SpawnDriftSmoke(blastPos, 2.2f);   // smoke at blast pos, not ground
                        break;
                    case ExplosionType.Rocket:
                        if (hitGround)
                        {
                            SpawnSandBurst(ground, 420, 14f, 2.2f, 35f, 1.2f);
                            SpawnGroundGrit(ground, 200, 10f, 1.0f);
                        }
                        SpawnDriftSmoke(blastPos, 4.5f);
                        SpawnSprinkle(blastPos, 2.2f, 0.45f);
                        break;
                    case ExplosionType.Artillery:
                        if (hitGround)
                        {
                            SpawnSandBurst(ground, 700, 22f, 3.5f, 20f, 1.8f);
                            SpawnGroundGrit(ground, 380, 16f, 1.4f);
                        }
                        SpawnDriftSmoke(blastPos, 8.0f);
                        SpawnSprinkle(blastPos, 3.5f, 0.65f);
                        break;
                }
            }
            catch { }
        }

        private static void SpawnSandBurst(Vector3 pos, int count, float speed,
                                            float lifetime, float coneAngle, float radius)
        {
            var go = MakeGO("SandBurst", pos, lifetime + 0.5f);
            var ps = go.AddComponent<ParticleSystem>();
            SetSandRenderer(ps);

            var main = ps.main;
            main.startLifetime   = new ParticleSystem.MinMaxCurve(lifetime * 0.4f, lifetime);
            main.startSpeed      = new ParticleSystem.MinMaxCurve(speed * 0.3f, speed);
            main.startSize       = new ParticleSystem.MinMaxCurve(0.006f, 0.028f);
            main.startColor      = SandGradient();
            main.gravityModifier = 2.2f;
            main.maxParticles    = count + 100;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.stopAction      = ParticleSystemStopAction.Destroy;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle     = coneAngle;
            shape.radius    = radius;
            shape.rotation  = new Vector3(-90f, 0f, 0f);

            var noise = ps.noise;
            noise.enabled     = true;
            noise.strength    = new ParticleSystem.MinMaxCurve(1.2f);
            noise.frequency   = 0.5f;
            noise.scrollSpeed = new ParticleSystem.MinMaxCurve(0.8f);
            noise.quality     = ParticleSystemNoiseQuality.Medium;

            SetBurst(ps, count);
            SetFade(ps);
            ps.Play();
        }

        private static void SpawnGroundGrit(Vector3 pos, int count, float speed, float radius)
        {
            var go = MakeGO("GroundGrit", pos, 1.8f);
            var ps = go.AddComponent<ParticleSystem>();
            SetSandRenderer(ps);

            var main = ps.main;
            main.startLifetime   = new ParticleSystem.MinMaxCurve(0.4f, 1.4f);
            main.startSpeed      = new ParticleSystem.MinMaxCurve(speed * 0.2f, speed);
            main.startSize       = new ParticleSystem.MinMaxCurve(0.004f, 0.018f);
            main.startColor      = SandGradient();
            main.gravityModifier = 3.0f;
            main.maxParticles    = count + 50;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.stopAction      = ParticleSystemStopAction.Destroy;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle     = 75f;
            shape.radius    = radius;
            shape.rotation  = new Vector3(-90f, 0f, 0f);

            SetBurst(ps, count);
            SetFade(ps);
            ps.Play();
        }

        /// <summary>
        /// Blast smoke/mist — ALWAYS spawns, very large.
        /// scale: grenade ~2, rocket ~4.5, artillery ~8
        /// This is the primary visual indicator of a big explosion.
        /// Does NOT replace the game's own fire/smoke — these are additive particles.
        /// </summary>
        private static void SpawnDriftSmoke(Vector3 pos, float scale)
        {
            float lifetime = Mathf.Lerp(3.0f, 7.0f, Mathf.InverseLerp(2f, 8f, scale));
            int   count    = Mathf.RoundToInt(Mathf.Lerp(8f, 40f, Mathf.InverseLerp(2f, 8f, scale)));
            float radius   = scale * 1.2f;

            var go = MakeGO("DriftSmoke", pos, lifetime + 2f);
            var ps = go.AddComponent<ParticleSystem>();

            var rend = go.GetComponent<ParticleSystemRenderer>();
            rend.renderMode = ParticleSystemRenderMode.Billboard;
            if (_sandMat != null)
            {
                var smokeMat = new Material(_sandMat);
                // Alpha 0.40 — noticeably visible. Big blasts deserve big smoke.
                smokeMat.color = new Color(0.62f, 0.55f, 0.42f, 0.40f);
                rend.material  = smokeMat;
            }

            var main = ps.main;
            main.startLifetime   = new ParticleSystem.MinMaxCurve(lifetime * 0.6f, lifetime);
            main.startSpeed      = new ParticleSystem.MinMaxCurve(scale * 0.3f, scale * 1.4f);
            // Particle size scales with explosion — artillery smoke fills the sky
            main.startSize       = new ParticleSystem.MinMaxCurve(scale * 0.8f, scale * 2.8f);
            main.startColor      = new ParticleSystem.MinMaxGradient(
                new Color(0.70f, 0.62f, 0.48f, 0.40f),
                new Color(0.55f, 0.50f, 0.38f, 0.25f));
            main.gravityModifier = -0.10f;  // rises slightly
            main.maxParticles    = count + 10;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.stopAction      = ParticleSystemStopAction.Destroy;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius    = radius;

            SetBurst(ps, count);

            // Color over lifetime — rises from brown dirt to grey smoke
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new GradientColorKey[] {
                    new GradientColorKey(new Color(0.65f, 0.55f, 0.38f), 0f),
                    new GradientColorKey(new Color(0.72f, 0.70f, 0.65f), 0.5f),
                    new GradientColorKey(new Color(0.80f, 0.80f, 0.78f), 1f)
                },
                new GradientAlphaKey[] {
                    new GradientAlphaKey(0f,    0f),
                    new GradientAlphaKey(0.40f, 0.08f),
                    new GradientAlphaKey(0.30f, 0.5f),
                    new GradientAlphaKey(0f,    1f)
                });
            col.color = new ParticleSystem.MinMaxGradient(grad);

            ps.Play();
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static void EnsureMaterial()
        {
            if (_sandMat != null) return;

            _sandTex = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                float dx = (x - 7.5f) / 7.5f;
                float dy = (y - 7.5f) / 7.5f;
                float d  = Mathf.Sqrt(dx * dx + dy * dy);
                float a  = Mathf.Clamp01(1f - Mathf.Pow(d, 1.4f));
                _sandTex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
            _sandTex.filterMode = FilterMode.Bilinear;
            _sandTex.Apply();

            var shader = Shader.Find("Sprites/Default")
                      ?? Shader.Find("Unlit/Transparent")
                      ?? Shader.Find("Unlit/Color");

            if (shader != null)
            {
                _sandMat             = new Material(shader);
                _sandMat.mainTexture = _sandTex;
                _sandMat.color       = new Color(0.52f, 0.40f, 0.26f, 0.90f);
            }
        }

        private static void SetSandRenderer(ParticleSystem ps)
        {
            var rend = ps.GetComponent<ParticleSystemRenderer>();
            rend.renderMode = ParticleSystemRenderMode.Billboard;
            if (_sandMat != null)
                rend.material = _sandMat;
        }

        private static GameObject MakeGO(string name, Vector3 pos, float ttl)
        {
            var go = new GameObject(name);
            go.transform.position = pos;
            Object.Destroy(go, ttl);
            return go;
        }

        private static ParticleSystem.MinMaxGradient SandGradient() =>
            new ParticleSystem.MinMaxGradient(
                new Color(0.56f, 0.43f, 0.28f, 0.95f),
                new Color(0.38f, 0.30f, 0.20f, 0.75f));

        private static void SetBurst(ParticleSystem ps, int count)
        {
            var emission = ps.emission;
            emission.enabled = true;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, count) });
        }

        private static void SetFade(ParticleSystem ps)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new GradientColorKey[] {
                    new GradientColorKey(new Color(0.56f, 0.43f, 0.28f), 0f),
                    new GradientColorKey(new Color(0.40f, 0.32f, 0.22f), 1f)
                },
                new GradientAlphaKey[] {
                    new GradientAlphaKey(0.95f, 0f),
                    new GradientAlphaKey(0.70f, 0.4f),
                    new GradientAlphaKey(0f,    1f)
                });
            col.color = new ParticleSystem.MinMaxGradient(grad);

            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 1f), new Keyframe(0.7f, 0.5f), new Keyframe(1f, 0f)));
        }

        private static void SpawnSprinkle(Vector3 pos, float duration, float vol)
        {
            try
            {
                var go = new GameObject("SprinkleAudio");
                go.transform.position = pos;
                Object.Destroy(go, duration + 0.5f);

                var src = go.AddComponent<AudioSource>();
                src.spatialBlend = 1f;
                src.minDistance  = 5f;
                src.maxDistance  = 40f;
                src.volume       = vol;
                src.rolloffMode  = AudioRolloffMode.Linear;

                int     sr    = 44100;
                int     samps = Mathf.CeilToInt(sr * duration * 0.55f);
                float[] data  = new float[samps];
                for (int i = 0; i < samps; i++)
                {
                    float t   = (float)i / samps;
                    float env = (1f - t) * (1f - t) * 0.55f;
                    data[i]   = (Random.value * 2f - 1f) * env;
                }
                var clip = AudioClip.Create("sprinkle", samps, 1, sr, false);
                clip.SetData(data, 0);
                src.PlayOneShot(clip);
            }
            catch { }
        }
    }
}
