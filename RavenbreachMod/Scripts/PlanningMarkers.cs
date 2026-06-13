using System.Collections.Generic;
using UnityEngine;

namespace RavenbreachMod
{
    public static class PlanningMarkers
    {
        public struct Marker
        {
            public Vector2 normPos;
            public int     colorIdx;
            public string  label;
        }

        public static readonly List<Marker> All = new List<Marker>();

        public static readonly string[] Names  = { "BLUE", "RED", "OBJ" };
        public static readonly Color[]  Colors =
        {
            new Color(0.25f, 0.65f, 1.00f, 0.90f),
            new Color(0.90f, 0.22f, 0.22f, 0.90f),
            new Color(0.90f, 0.80f, 0.10f, 0.90f)
        };

        public static void Clear() => All.Clear();

        public static Vector2 ScreenToNorm(Vector2 screen, Rect mapRect)
            => new Vector2((screen.x - mapRect.x) / mapRect.width,
                           (screen.y - mapRect.y) / mapRect.height);

        public static Vector2 NormToScreen(Vector2 norm, Rect mapRect)
            => new Vector2(mapRect.x + norm.x * mapRect.width,
                           mapRect.y + norm.y * mapRect.height);

        // Convert normalised map coords to a world position, sampled on terrain
        public static Vector3 NormToWorld(Vector2 norm, Vector3 terrainPos, Vector3 terrainSize)
        {
            float wx = terrainPos.x + norm.x * terrainSize.x;
            float wz = terrainPos.z + norm.y * terrainSize.z;
            float wy = terrainPos.y;
            if (Terrain.activeTerrain != null)
                wy += Terrain.activeTerrain.SampleHeight(new Vector3(wx, 0, wz));
            return new Vector3(wx, wy, wz);
        }

        public static Vector2[] GenerateDotLayout(string mapName, int count)
        {
            var rng  = new System.Random(mapName?.GetHashCode() ?? 0);
            var dots = new Vector2[count];
            float cell = 1f / Mathf.Ceil(Mathf.Sqrt(count));
            int   cols = Mathf.CeilToInt(1f / cell);
            for (int i = 0; i < count; i++)
            {
                int   cx = i % cols, cy = i / cols;
                float jx = (float)(rng.NextDouble() * 0.6 + 0.2) * cell;
                float jy = (float)(rng.NextDouble() * 0.6 + 0.2) * cell;
                dots[i]  = new Vector2(
                    Mathf.Clamp01(cx * cell + jx),
                    Mathf.Clamp01(cy * cell + jy));
            }
            return dots;
        }
    }
}
