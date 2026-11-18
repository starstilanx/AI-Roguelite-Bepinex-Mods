using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_SkillWeb
{
    /// <summary>
    /// Procedurally drawn, anti-aliased node sprites. Generated once per session so the web needs
    /// no art files: discs, rings of a few weights, rounded squares for Anchor Stars, a soft glow,
    /// and a tiling starfield for the background.
    /// </summary>
    public static class WebSprites
    {
        const int Size = 128;

        static Sprite _disc, _ring, _ringThick, _ringDouble, _glow, _square, _squareRing;
        static Texture2D _discTex, _starfield;

        public static Sprite Disc => _disc ? _disc : (_disc = Make(DiscAlpha));
        public static Sprite Ring => _ring ? _ring : (_ring = Make((r) => RingAlpha(r, 0.90f, 1.00f)));
        public static Sprite RingThick => _ringThick ? _ringThick : (_ringThick = Make((r) => RingAlpha(r, 0.84f, 1.00f)));
        public static Sprite RingDouble => _ringDouble ? _ringDouble : (_ringDouble = Make((r) => Mathf.Max(RingAlpha(r, 0.92f, 1.00f), RingAlpha(r, 0.74f, 0.82f))));
        public static Sprite Glow => _glow ? _glow : (_glow = Make((r) => { float t = Mathf.Clamp01(1f - r); return t * t; }));
        public static Sprite Square => _square ? _square : (_square = MakeSquare(false));
        public static Sprite SquareRing => _squareRing ? _squareRing : (_squareRing = MakeSquare(true));

        /// <summary>Plain disc texture for WebDotGraphic's batched dots.</summary>
        public static Texture2D DiscTexture { get { if (_discTex == null) _discTex = Disc.texture; return _discTex; } }

        public static Texture2D Starfield
        {
            get
            {
                if (_starfield != null) return _starfield;
                const int s = 512;
                var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
                var px = new Color32[s * s];
                var bg = new Color32(11, 10, 8, 255);
                for (int i = 0; i < px.Length; i++) px[i] = bg;
                var r = new System.Random(1307);
                for (int i = 0; i < 260; i++)
                {
                    int x = r.Next(s), y = r.Next(s);
                    bool gold = r.NextDouble() < 0.6;
                    byte a = (byte)(40 + r.Next(90));
                    var c = gold ? new Color32(230, 196, 106, a) : new Color32(216, 208, 191, a);
                    Blend(px, s, x, y, c);
                    if (r.NextDouble() < 0.15) { Blend(px, s, (x + 1) % s, y, c); Blend(px, s, x, (y + 1) % s, c); }
                }
                tex.SetPixels32(px);
                tex.Apply();
                return _starfield = tex;
            }
        }

        static void Blend(Color32[] px, int s, int x, int y, Color32 c)
        {
            var d = px[y * s + x];
            float a = c.a / 255f;
            px[y * s + x] = new Color32((byte)(d.r + (c.r - d.r) * a), (byte)(d.g + (c.g - d.g) * a), (byte)(d.b + (c.b - d.b) * a), 255);
        }

        static float DiscAlpha(float r) => Mathf.Clamp01((1f - r) * Size * 0.5f);

        static float RingAlpha(float r, float inner, float outer)
        {
            float px = Size * 0.5f;
            float a = Mathf.Clamp01((outer - r) * px);   // fade out past the outer edge
            float b = Mathf.Clamp01((r - inner) * px);   // fade in past the inner edge
            return Mathf.Min(a, b);
        }

        /// <summary>Builds a sprite from alpha as a function of normalized radius (0 centre, 1 edge).</summary>
        static Sprite Make(System.Func<float, float> alphaAt)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[Size * Size];
            float c = (Size - 1) * 0.5f;
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float dx = (x - c) / c, dy = (y - c) / c;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    px[y * Size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alphaAt(r)) * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), 100f);
        }

        static Sprite MakeSquare(bool ringOnly)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[Size * Size];
            float half = (Size - 1) * 0.5f, radius = Size * 0.14f, inset = Size * 0.10f;
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    // Signed distance to a rounded square (negative inside).
                    float qx = Mathf.Abs(x - half) - (half - radius), qy = Mathf.Abs(y - half) - (half - radius);
                    float outside = new Vector2(Mathf.Max(qx, 0), Mathf.Max(qy, 0)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0) - radius;
                    float a = Mathf.Clamp01(-outside);
                    if (ringOnly) a = Mathf.Min(a, Mathf.Clamp01(outside + inset));
                    px[y * Size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), 100f);
        }
    }

    /// <summary>
    /// Draws every edge of the web as one mesh (one draw call) instead of one GameObject per line.
    /// Call <see cref="SetSegments"/> after changing the list.
    /// </summary>
    public class WebLineGraphic : MaskableGraphic
    {
        public struct Segment
        {
            public Vector2 a, b;
            public float width;
            public Color32 color;
        }

        readonly List<Segment> _segments = new List<Segment>();

        public List<Segment> Segments => _segments;
        public void SetSegments() => SetVerticesDirty();

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var v = UIVertex.simpleVert;
            foreach (var s in _segments)
            {
                Vector2 d = s.b - s.a;
                float len = d.magnitude;
                if (len < 0.01f) continue;
                Vector2 n = new Vector2(-d.y, d.x) / len * (s.width * 0.5f);
                int i = vh.currentVertCount;
                v.color = s.color;
                v.position = s.a - n; vh.AddVert(v);
                v.position = s.a + n; vh.AddVert(v);
                v.position = s.b + n; vh.AddVert(v);
                v.position = s.b - n; vh.AddVert(v);
                vh.AddTriangle(i, i + 1, i + 2);
                vh.AddTriangle(i, i + 2, i + 3);
            }
        }
    }

    /// <summary>
    /// Draws many round dots as one textured mesh. Used for small nodes when zoomed far out and for
    /// the uncharted rings, so the full web stays cheap to render.
    /// </summary>
    public class WebDotGraphic : MaskableGraphic
    {
        public struct Dot
        {
            public Vector2 pos;
            public float size;
            public Color32 color;
        }

        readonly List<Dot> _dots = new List<Dot>();

        public List<Dot> Dots => _dots;
        public void SetDots() => SetVerticesDirty();

        public override Texture mainTexture => WebSprites.DiscTexture;

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var v = UIVertex.simpleVert;
            foreach (var d in _dots)
            {
                float h = d.size * 0.5f;
                int i = vh.currentVertCount;
                v.color = d.color;
                v.position = d.pos + new Vector2(-h, -h); v.uv0 = new Vector2(0, 0); vh.AddVert(v);
                v.position = d.pos + new Vector2(-h, h);  v.uv0 = new Vector2(0, 1); vh.AddVert(v);
                v.position = d.pos + new Vector2(h, h);   v.uv0 = new Vector2(1, 1); vh.AddVert(v);
                v.position = d.pos + new Vector2(h, -h);  v.uv0 = new Vector2(1, 0); vh.AddVert(v);
                vh.AddTriangle(i, i + 1, i + 2);
                vh.AddTriangle(i, i + 2, i + 3);
            }
        }
    }
}
