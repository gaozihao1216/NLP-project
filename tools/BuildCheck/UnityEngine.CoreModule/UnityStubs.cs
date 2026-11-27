// UnityEngine 离线编译桩（仅供 tools/BuildCheck 的 Presentation/Bootstrap 工程编译验证）。
//
// 用途：本机无 Unity 编辑器时，用真实编译检查表现层代码的类型与签名错误。
// 覆盖面 = 表现层实际用到的 API 子集，签名尽量与 Unity 2021.3 一致；
// 桩编译通过 ≠ Unity 内一定通过（尤其 Editor 脚本未覆盖），上编辑器仍需人工确认。
// 此桩绝不进入 Unity 工程，也不应被 game/ 引用。

using System;
using System.Collections.Generic;

namespace UnityEngine
{
    // ---- 数学 ----

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }

        public static Vector3 zero => new Vector3(0f, 0f, 0f);
        public static Vector3 one => new Vector3(1f, 1f, 1f);
        public static Vector3 up => new Vector3(0f, 1f, 0f);
        public static Vector3 forward => new Vector3(0f, 0f, 1f);

        public float magnitude => (float)Math.Sqrt(x * x + y * y + z * z);
        public float sqrMagnitude => x * x + y * y + z * z;
        public Vector3 normalized
        {
            get
            {
                float m = magnitude;
                return m > 1e-8f ? new Vector3(x / m, y / m, z / m) : zero;
            }
        }

        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 Cross(Vector3 a, Vector3 b) =>
            new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) =>
            new Vector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator *(float d, Vector3 a) => a * d;
        public static Vector3 operator /(Vector3 a, float d) => new Vector3(a.x / d, a.y / d, a.z / d);
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public static Quaternion identity => new Quaternion { x = 0f, y = 0f, z = 0f, w = 1f };

        public static Quaternion Euler(float pitch, float yaw, float roll)
        {
            double pr = pitch * Math.PI / 180.0, yr = yaw * Math.PI / 180.0, rr = roll * Math.PI / 180.0;
            double cy = Math.Cos(yr * 0.5), sy = Math.Sin(yr * 0.5);
            double cp = Math.Cos(pr * 0.5), sp = Math.Sin(pr * 0.5);
            double cr = Math.Cos(rr * 0.5), sr = Math.Sin(rr * 0.5);
            return new Quaternion
            {
                w = (float)(cy * cp * cr + sy * sp * sr),
                x = (float)(sy * cp * cr - cy * sp * sr),
                y = (float)(cy * cp * sr + sy * sp * cr),
                z = (float)(cy * sp * cr - sy * cp * sr)
            };
        }

        /// <summary>把 (0,0,1) 旋转到 from→to 的最短弧（近似实现，仅编译验证用）。</summary>
        public static Quaternion FromToRotation(Vector3 fromDirection, Vector3 toDirection)
        {
            Vector3 f = fromDirection.normalized, t = toDirection.normalized;
            float d = Vector3.Dot(f, t);
            if (d >= 0.9999f) return identity;
            Vector3 axis = Vector3.Cross(f, t);
            if (axis.sqrMagnitude < 1e-8f) axis = Vector3.up;
            float angle = (float)Math.Acos(Math.Min(1f, Math.Max(-1f, d))) * 57.29578f;
            return AngleAxis(angle, axis);
        }

        public static Quaternion AngleAxis(float degrees, Vector3 axis)
        {
            double r = degrees * Math.PI / 180.0;
            double half = r * 0.5;
            axis = axis.normalized;
            return new Quaternion
            {
                w = (float)Math.Cos(half),
                x = (float)(axis.x * Math.Sin(half)),
                y = (float)(axis.y * Math.Sin(half)),
                z = (float)(axis.z * Math.Sin(half))
            };
        }

        public static Vector3 operator *(Quaternion q, Vector3 v)
        {
            // 四元数旋转公式。
            float ix = q.w * v.x + q.y * v.z - q.z * v.y;
            float iy = q.w * v.y + q.z * v.x - q.x * v.z;
            float iz = q.w * v.z + q.x * v.y - q.y * v.x;
            float iw = -(q.x * v.x + q.y * v.y + q.z * v.z);
            return new Vector3(
                ix * q.w + iw * -q.x + iy * -q.z - iz * -q.y,
                iy * q.w + iw * -q.y + iz * -q.x - ix * -q.z,
                iz * q.w + iw * -q.z + ix * -q.y - iy * -q.x);
        }
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b) : this(r, g, b, 1f) { }
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }

        public static Color white => new Color(1f, 1f, 1f, 1f);
        public static Color black => new Color(0f, 0f, 0f, 1f);
        public static Color clear => new Color(0f, 0f, 0f, 0f);
        public static Color red => new Color(1f, 0f, 0f, 1f);
        public static Color green => new Color(0f, 1f, 0f, 1f);
        public static Color blue => new Color(0f, 0f, 1f, 1f);
        public static Color yellow => new Color(1f, 0.92f, 0.016f, 1f);
        public static Color gray => new Color(0.5f, 0.5f, 0.5f, 1f);

        public static Color Lerp(Color a, Color b, float t) => new Color(
            a.r + (b.r - a.r) * t,
            a.g + (b.g - a.g) * t,
            a.b + (b.b - a.b) * t,
            a.a + (b.a - a.a) * t);

        public static Color operator *(Color c, float s) => new Color(c.r * s, c.g * s, c.b * s, c.a);
    }

    public struct Color32
    {
        public byte r, g, b, a;
        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float width, float height)
        {
            this.x = x; this.y = y; this.width = width; this.height = height;
        }
    }

    public static class Mathf
    {
        public const float Deg2Rad = 0.0174532924f;
        public const float Rad2Deg = 57.29578f;

        public static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
        public static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        public static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float Min(float a, float b) => a < b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;
        public static float Abs(float v) => Math.Abs(v);
        public static float Sin(float v) => (float)Math.Sin(v);
        public static float Cos(float v) => (float)Math.Cos(v);
        public static float Pow(float v, float p) => (float)Math.Pow(v, p);
        public static float Sqrt(float v) => (float)Math.Sqrt(v);
        public static bool Approximately(float a, float b) => Math.Abs(a - b) < 1e-6f;
    }

    public static class Time
    {
        public static float fixedDeltaTime { get; set; } = 0.02f;
        public static float deltaTime { get; set; } = 0.016f;
        public static float time { get; set; }
        public static float realtimeSinceStartup { get; set; }
    }

    // ---- 对象模型 ----

    public class Object
    {
        public string name { get; set; }
        public static void Destroy(Object obj) { }
        public static void Destroy(Object obj, float t) { }
        public static void DontDestroyOnLoad(Object obj) { }
    }

    public class Component : Object
    {
        public GameObject gameObject { get; internal set; }
        public Transform transform { get; internal set; }

        public T GetComponent<T>() where T : Component => null;
        public T GetComponentInChildren<T>() where T : Component => null;
    }

    public class Behaviour : Component
    {
        public bool enabled { get; set; }
    }

    public class MonoBehaviour : Behaviour
    {
        public void StartCoroutine(object routine) { }
    }

    public class GameObject : Object
    {
        public GameObject(string name) { this.name = name; }

        public Transform transform { get; internal set; }
        public string tag { get; set; }
        public bool activeSelf { get; internal set; } = true;

        public void SetActive(bool value) { activeSelf = value; }
        public T AddComponent<T>() where T : Component, new()
        {
            T c = new T();
            c.gameObject = this;
            c.transform = this.transform;
            return c;
        }
        public T GetComponent<T>() where T : Component => null;
        public T GetComponentInChildren<T>() where T : Component => null;

        public static GameObject CreatePrimitive(PrimitiveType type) => new GameObject(type.ToString());
    }

    public enum PrimitiveType { Sphere, Capsule, Cylinder, Cube, Plane, Quad }

    public class Transform : Component
    {
        public Vector3 position { get; set; }
        public Quaternion rotation { get; set; }
        public Vector3 localPosition { get; set; }
        public Quaternion localRotation { get; set; }
        public Vector3 localScale { get; set; }
        public Transform parent { get; internal set; }
        public Vector3 forward => rotation * Vector3.forward;

        public void SetParent(Transform parent, bool worldPositionStays) { this.parent = parent; }
        public void SetParent(Transform parent) { this.parent = parent; }
        public void LookAt(Vector3 worldPosition) { }
        public int childCount => 0;
        public Transform GetChild(int index) => null;
    }

    // ---- 渲染 ----

    public class Renderer : Component
    {
        public Material material { get; set; }
        public Material sharedMaterial { get; set; }
    }

    public class MeshRenderer : Renderer { }

    public class Collider : Behaviour { }
    public class BoxCollider : Collider { }
    public class SphereCollider : Collider { }
    public class CapsuleCollider : Collider { }

    public enum LightType { Point, Directional, Spot }

    public class Light : Behaviour
    {
        public LightType type { get; set; }
        public Color color { get; set; }
        public float intensity { get; set; }
        public float range { get; set; }
    }

    public enum CameraClearFlags { Skybox, SolidColor, Depth, Nothing }

    public class Camera : Behaviour
    {
        public CameraClearFlags clearFlags { get; set; }
        public Color backgroundColor { get; set; }
        public float fieldOfView { get; set; }
        public float nearClipPlane { get; set; }
        public float farClipPlane { get; set; }
        public float aspect { get; set; }

        public static Camera main => null;
        public Vector3 WorldToScreenPoint(Vector3 world) => new Vector3(0f, 0f, 0f);
        public Ray ScreenPointToRay(Vector3 screenPos) =>
            new Ray { origin = new Vector3(0f, 0f, 0f), direction = Vector3.forward };
    }

    public struct Ray
    {
        public Vector3 origin;
        public Vector3 direction;
    }

    public class Shader
    {
        public static Shader Find(string name) => null;
    }

    public class Texture : Object { }

    public enum TextureFormat { RGBA32, ARGB32, RGB24 }
    public enum TextureWrapMode { Repeat, Clamp, Mirror }
    public enum FilterMode { Point, Bilinear, Trilinear }

    public class Texture2D : Texture
    {
        public Texture2D(int width, int height, TextureFormat format, bool mipmap) { }
        public TextureWrapMode wrapMode { get; set; }
        public FilterMode filterMode { get; set; }
        public void SetPixels32(Color32[] pixels) { }
        public void SetPixel(int x, int y, Color color) { }
        public void Apply() { }
    }

    public class Material
    {
        public Material(Shader shader) { }
        public Color color { get; set; }
        public Texture mainTexture { get; set; }
        public void SetColor(string name, Color value) { }
        public void SetFloat(string name, float value) { }
    }

    public static class RenderSettings
    {
        public static Material skybox { get; set; }
        public static Color ambientLight { get; set; }
        public static bool fog { get; set; }
    }

    // ---- 输入（Unity 里在 InputLegacyModule；桩合并进一个程序集，仅供编译验证） ----

    public enum KeyCode
    {
        None, Space, Return, Escape, Tab,
        W, A, S, D, Q, E, R, F, G, T, X, Z,
        Alpha0, Alpha1, Alpha2, Alpha3, Alpha4,
        Mouse0, Mouse1,
        UpArrow, DownArrow, LeftArrow, RightArrow,
        LeftShift, LeftControl
    }

    public static class Input
    {
        public static Vector2 mouseScrollDelta { get; set; }
        public static Vector3 mousePosition { get; set; }
        public static bool GetKey(KeyCode key) => false;
        public static bool GetKeyDown(KeyCode key) => false;
        public static bool GetKeyUp(KeyCode key) => false;
        public static bool GetMouseButton(int button) => false;
        public static bool GetMouseButtonDown(int button) => false;
        public static bool GetMouseButtonUp(int button) => false;
        public static float GetAxis(string axisName) => 0f;
    }

    // ---- GUI（Unity 里在 IMGUIModule；桩合并） ----

    public static class GUI
    {
        public static Color color { get; set; } = Color.white;
        public static void Box(Rect rect, string text) { }
        public static void Label(Rect rect, string text) { }
        public static void DrawTexture(Rect rect, Texture2D image) { }
    }

    public static class Screen
    {
        public static int width { get; set; } = 1280;
        public static int height { get; set; } = 720;
    }

    // ---- 其它 ----

    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
        public static void LogError(object message) { }
    }

    public static class Application
    {
        public static string dataPath { get; set; } = "";
        public static bool isEditor { get; set; }
    }

    public enum RuntimeInitializeLoadType
    {
        AfterAssembliesLoaded,
        BeforeSceneLoad,
        AfterSceneLoad
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        public RuntimeInitializeOnLoadMethodAttribute() { }
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType loadType) { }
    }
}
