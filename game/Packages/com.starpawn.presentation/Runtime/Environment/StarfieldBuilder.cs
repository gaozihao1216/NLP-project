// 程序化星空环境：天空盒、光照、环境光。
//
// 零资产依赖——天空盒贴图在运行时逐像素生成（银盘星带 + 稀疏亮星），
// 光照用一枚定向光模拟恒星。不引任何外部资源，也不写 .mat 文件。

using System;
using UnityEngine;
using Random = System.Random;

namespace StarPawn.Presentation
{
    public static class StarfieldBuilder
    {
        /// <summary>固定种子：每次进场景星空一致，方便对比调参截图。</summary>
        private const int StarSeed = 20261122;

        public static void Build()
        {
            Texture2D sky = MakeStarTexture(2048, 1024);

            Shader shader = Shader.Find("Skybox/Panoramic");
            if (shader != null)
            {
                Material mat = new Material(shader);
                mat.mainTexture = sky;
                RenderSettings.skybox = mat;
            }
            else
            {
                Debug.LogWarning("找不到 Skybox/Panoramic 着色器，星空退化为纯色背景");
            }

            // 深空环境光：偏蓝的暗底色，避免背光面死黑。
            RenderSettings.ambientLight = new Color(0.07f, 0.08f, 0.13f);
            RenderSettings.fog = false;

            // 主光 = 场景里的恒星，决定场景主光方向与色调。
            GameObject sun = new GameObject("SunLight");
            Light light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.96f, 0.88f);
            light.intensity = 1.05f;
            sun.transform.rotation = Quaternion.Euler(35f, 305f, 0f);
        }

        /// <summary>
        /// 全景星图：背景深空渐变 + 一条倾斜的银盘星带 + 随机亮星。
        /// 亮度分布用平方衰减——大多数星很暗，少数星亮，才像真实星空。
        /// </summary>
        private static Texture2D MakeStarTexture(int width, int height)
        {
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            Color32[] pixels = new Color32[width * height];
            Random rng = new Random(StarSeed);

            // 背景与星带：星带沿一条正弦倾斜的横轴，亮度按到轴距离高斯衰减。
            double bandCenter = height * 0.5;
            double bandSigma = height * 0.16;
            double bandAngle = 0.22; // 星带在全景图里的倾斜角（弧度）

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    double v = (double)y / height;
                    Color c = new Color(
                        0.012f + 0.02f * (float)(1.0 - v),
                        0.012f + 0.025f * (float)(1.0 - v),
                        0.03f + 0.05f * (float)(1.0 - v), 1f);

                    double axis = bandCenter + Math.Sin((double)x / width * Math.PI * 2.0 + bandAngle) * height * 0.12;
                    double d = (y - axis) / bandSigma;
                    double band = Math.Exp(-0.5 * d * d);
                    if (band > 0.001)
                    {
                        c.r += 0.028f * (float)band;
                        c.g += 0.02f * (float)band;
                        c.b += 0.05f * (float)band;
                    }
                    pixels[y * width + x] = ToByte(c);
                }
            }

            // 银盘内的密集暗星。
            for (int i = 0; i < 6000; i++)
            {
                int x = (int)(rng.NextDouble() * width) % width;
                double u = rng.NextDouble();
                double axis = bandCenter + Math.Sin((double)x / width * Math.PI * 2.0 + bandAngle) * height * 0.12;
                int y = (int)Math.Round(axis + Gauss(rng) * bandSigma * 0.55);
                if (y < 0 || y >= height) continue;
                float b = 0.05f + 0.18f * (float)(u * u);
                Blend(ref pixels[y * width + x], x, y, 1, b, rng);
            }

            // 全天散布的普通星。
            for (int i = 0; i < 1600; i++)
            {
                int x = (int)(rng.NextDouble() * width) % width;
                int y = (int)(rng.NextDouble() * height);
                float u = (float)rng.NextDouble();
                float b = 0.15f + 0.85f * u * u * u; // 平方偏暗，少数很亮
                Color tint = StarTint(rng);
                Blend(ref pixels[y * width + x], x, y, 1, b, rng, tint);
            }

            // 少数大亮星（2x2）。
            for (int i = 0; i < 40; i++)
            {
                int x = (int)(rng.NextDouble() * width) % width;
                int y = (int)(rng.NextDouble() * height);
                Color tint = StarTint(rng);
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int px = (x + dx) % width;
                        int py = y + dy;
                        if (py >= height) continue;
                        Blend(ref pixels[py * width + px], px, py, 2, 0.95f, rng, tint);
                    }
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }

        private static double Gauss(Random rng)
        {
            double u1 = Math.Max(rng.NextDouble(), 1e-9);
            double u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        /// <summary>恒星色温：偏蓝 / 白 / 黄 / 橙随机。</summary>
        private static Color StarTint(Random rng)
        {
            double pick = rng.NextDouble();
            if (pick < 0.25) return new Color(0.75f, 0.83f, 1f);
            if (pick < 0.6) return new Color(1f, 0.98f, 0.92f);
            if (pick < 0.85) return new Color(1f, 0.9f, 0.7f);
            return new Color(1f, 0.78f, 0.6f);
        }

        /// <summary>把星点以加法混入背景（支持 2x2 的软边）。</summary>
        private static void Blend(ref Color32 back, int x, int y, int size, float brightness, Random rng)
        {
            Blend(ref back, x, y, size, brightness, rng, Color.white);
        }

        private static void Blend(ref Color32 back, int x, int y, int size, float brightness, Random rng, Color tint)
        {
            float edge = size > 1 ? 0.75f + 0.25f * (float)rng.NextDouble() : 1f;
            float b = brightness * edge;
            Color c = new Color(
                back.r / 255f + tint.r * b,
                back.g / 255f + tint.g * b,
                back.b / 255f + tint.b * b, 1f);
            back = ToByte(c);
        }

        private static Color32 ToByte(Color c)
        {
            return new Color32(
                (byte)(Mathf.Clamp01(c.r) * 255f),
                (byte)(Mathf.Clamp01(c.g) * 255f),
                (byte)(Mathf.Clamp01(c.b) * 255f),
                255);
        }
    }
}
