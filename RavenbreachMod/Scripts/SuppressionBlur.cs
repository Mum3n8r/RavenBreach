using UnityEngine;

namespace RavenbreachMod
{
    [RequireComponent(typeof(Camera))]
    public class SuppressionBlur : MonoBehaviour
    {
        public float BlurAmount { get; set; } = 0f;

        private void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            if (BlurAmount < 0.01f)
            {
                Graphics.Blit(src, dst);
                return;
            }

            // Two-pass pyramid blur: downsample twice, upsample once
            // Pass 1: full res → mid res
            // Pass 2: mid res → low res
            // Pass 3: low res → full res (bilinear upscale = blur)
            float scale1 = Mathf.Lerp(1f, 0.50f, BlurAmount);   // mid res
            float scale2 = Mathf.Lerp(1f, 0.25f, BlurAmount);   // low res

            int w1 = Mathf.Max(4, Mathf.RoundToInt(src.width  * scale1));
            int h1 = Mathf.Max(4, Mathf.RoundToInt(src.height * scale1));
            int w2 = Mathf.Max(2, Mathf.RoundToInt(src.width  * scale2));
            int h2 = Mathf.Max(2, Mathf.RoundToInt(src.height * scale2));

            var mid = RenderTexture.GetTemporary(w1, h1, 0, src.format);
            var low = RenderTexture.GetTemporary(w2, h2, 0, src.format);
            mid.filterMode = FilterMode.Bilinear;
            low.filterMode = FilterMode.Bilinear;

            Graphics.Blit(src, mid);   // downsample to mid
            Graphics.Blit(mid, low);   // downsample to low
            Graphics.Blit(low, dst);   // upsample to full — bilinear = blur

            RenderTexture.ReleaseTemporary(mid);
            RenderTexture.ReleaseTemporary(low);
        }
    }
}
