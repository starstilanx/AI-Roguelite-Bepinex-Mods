using System;
using System.Collections.Generic;

namespace AIROG_SkillWeb
{
    /// <summary>
    /// Builds the whole passive web for a character in one pass, Path-of-Exile scale.
    ///
    /// Each sector is a wedge of rings. Every ring holds a handful of clusters, and each cluster
    /// takes one of four shapes: a wheel of small nodes around a Notable, a chain ending in a
    /// Notable, a ring of nodes around a Mastery, or (outermost ring only) a Keystone at the end
    /// of a short lead-in. Clusters join the ring inside them through strings of small attribute
    /// ("Travel") nodes; random side links between neighbouring clusters, plus Confluences on the
    /// borders between sectors, turn the spokes into a web with loops.
    ///
    /// The structure never changes after generation (allocations are keyed by node id). What grows
    /// during play is how much of it is charted: see WebGrower.
    ///
    /// Internally everything is laid out in "g" units (the design mockup's scale, y pointing down)
    /// and converted to UI units (y up, x <see cref="Scale"/>) at the end.
    /// </summary>
    public static class WebGenerator
    {
        public const int Version = 5;
        public const float Scale = 2.4f;

        const float GateR = 120f;
        const float BandBase = 120f;
        const float BandStep = 150f;

        public static int RingsFor(string size)
        {
            switch ((size ?? "").Trim().ToLowerInvariant())
            {
                case "compact": return 5;
                case "vast": return 9;
                default: return 7;
            }
        }

        /// <summary>Node footprint in UI units. SkillWebUI draws nodes at exactly this size.</summary>
        public static float NodeDiameter(WebNode n)
        {
            if (n == null) return 38f;
            if (n.IsOrigin) return 86f;
            switch (n.type)
            {
                case WebNodeType.Travel: return 29f;
                case WebNodeType.Notable: return 67f;
                case WebNodeType.Mastery: return 72f;
                case WebNodeType.Keystone: return 106f;
                case WebNodeType.Anchor: return 72f;
                case WebNodeType.Confluence: return 62f;
                default: return 38f;
            }
        }

        /// <summary>UI-unit radius of a ring's centre line, for labels and camera jumps.</summary>
        public static float RingRadius(float ring) => (BandBase + ring * BandStep) * Scale;

        sealed class Cluster
        {
            public List<WebNode> nodes = new List<WebNode>();
            public WebNode entry;
        }

        sealed class Gen
        {
            public System.Random R;
            public SkillWebData D;
            public int clusterSeq;

            public WebNode Add(WebNode n)
            {
                if (n.id == null) n.id = "n" + D.nextNodeIndex++;
                D.nodes.Add(n);
                return n;
            }

            public static void Link(WebNode a, WebNode b)
            {
                if (a == null || b == null || a == b || a.edges.Contains(b.id)) return;
                a.edges.Add(b.id);
                b.edges.Add(a.id);
            }

            public bool Clear(float x, float y, float d)
            {
                float d2 = d * d;
                foreach (var n in D.nodes)
                {
                    float dx = n.x - x, dy = n.y - y;
                    if (dx * dx + dy * dy < d2) return false;
                }
                return true;
            }

            public WebNode Nearest(float x, float y, Func<WebNode, bool> ok, IEnumerable<WebNode> pool = null)
            {
                WebNode best = null;
                float bd = float.MaxValue;
                foreach (var n in pool ?? D.nodes)
                {
                    if (!ok(n)) continue;
                    float dx = n.x - x, dy = n.y - y, d = dx * dx + dy * dy;
                    if (d < bd) { bd = d; best = n; }
                }
                return best;
            }

            public float Jitter(float amount) => (float)(R.NextDouble() - 0.5) * amount;
        }

        static bool Linkable(WebNode n) =>
            n.type != WebNodeType.Mastery && n.type != WebNodeType.Keystone && !n.IsOrigin;

        static WebNode Node(WebNodeType type, float x, float y, WebSector s, int ring, int cluster = -1) =>
            new WebNode { type = type, x = x, y = y, sectorId = s?.id, ring = ring, clusterId = cluster };

        /// <summary>
        /// Generates structure and offline content for <paramref name="data"/>.sectors, which must
        /// already carry name/attr/altAttr/colorHex. Clears any existing nodes.
        /// </summary>
        public static void Generate(SkillWebData data, int rings)
        {
            var g = new Gen { R = new System.Random((int)(data.layoutSeed ^ (data.layoutSeed >> 32))), D = data };
            data.nodes.Clear();
            data.InvalidateIndex();
            data.nextNodeIndex = 0;
            data.maxRing = rings;

            int k = data.sectors.Count;
            float span = (float)(Math.PI * 2 / Math.Max(1, k));

            var heart = g.Add(new WebNode
            {
                id = "origin", type = WebNodeType.Basic, name = "The Heart", ring = 0, unlocked = true,
                description = "Every path through the web begins here.",
            });

            // g-space angle per sector: first sector points straight up (y is down in g-space).
            var angle = new Dictionary<string, float>();
            for (int i = 0; i < k; i++) angle[data.sectors[i].id] = (float)(-Math.PI / 2 + i * span);

            foreach (var sec in data.sectors)
            {
                float a = angle[sec.id];
                var gate = g.Add(new WebNode
                {
                    id = "gate:" + sec.id, type = WebNodeType.Anchor, sectorId = sec.id, ring = 0, unlocked = true,
                    x = GateR * (float)Math.Cos(a), y = GateR * (float)Math.Sin(a),
                    name = sec.name,
                    description = "The Anchor Star of " + sec.name + ". Perks you learn in its native perk tree feed it.",
                });
                Gen.Link(heart, gate);

                int keysPlaced = 0;
                for (int b = 1; b <= rings; b++)
                {
                    float r = BandBase + b * BandStep;
                    int count = Math.Max(1, (int)Math.Floor(r * span * 0.82f / 200f));
                    float lo = a - span / 2 * 0.8f, hi = a + span / 2 * 0.8f;
                    Cluster prev = null;

                    for (int j = 0; j < count; j++)
                    {
                        float cx = 0, cy = 0, t = 0;
                        bool ok = false;
                        for (int tries = 0; tries < 5 && !ok; tries++)
                        {
                            t = lo + (hi - lo) * (j + 0.5f) / count + g.Jitter((hi - lo) / count * 0.5f);
                            float rr = r + g.Jitter(50f);
                            cx = rr * (float)Math.Cos(t);
                            cy = rr * (float)Math.Sin(t);
                            ok = g.Clear(cx, cy, 100f);
                        }
                        if (!ok) continue;

                        bool isKey = b == rings && keysPlaced < 2 && (count == 1 || j == 0 || j == count - 1);
                        string kind = isKey ? "key"
                            : (b >= 2 && g.R.NextDouble() < 0.28) ? "mastery"
                            : g.R.NextDouble() < 0.58 ? "wheel" : "chain";
                        if (isKey) keysPlaced++;

                        var cl = BuildCluster(g, kind, cx, cy, t, sec, b);
                        int ringBelow = b - 1;
                        var from = g.Nearest(cl.entry.x, cl.entry.y,
                            n => n.sectorId == sec.id && n.ring == ringBelow && Linkable(n)) ?? gate;
                        Travel(g, from, cl.entry, sec, b);

                        // Side links between neighbouring clusters are what make it a web, not spokes.
                        if (prev != null && g.R.NextDouble() < 0.5)
                        {
                            var p = g.Nearest(cx, cy, Linkable, prev.nodes);
                            var q = p == null ? null : g.Nearest(p.x, p.y, Linkable, cl.nodes);
                            if (q != null) Travel(g, p, q, sec, b);
                        }
                        prev = cl;
                    }
                }
            }

            // Confluences on the border between each pair of neighbouring sectors.
            if (k > 1)
            {
                for (int i = 0; i < k; i++)
                {
                    var A = data.sectors[i];
                    var B = data.sectors[(i + 1) % k];
                    for (int b = 2; b < rings; b += 2)
                    {
                        float ang = angle[A.id] + span / 2, r = BandBase + b * BandStep + 60f;
                        float x = r * (float)Math.Cos(ang), y = r * (float)Math.Sin(ang);
                        if (!g.Clear(x, y, 50f)) continue;
                        var c = g.Add(Node(WebNodeType.Confluence, x, y, A, b));
                        c.sectorId2 = B.id;
                        foreach (var S in new[] { A, B })
                        {
                            var nn = g.Nearest(x, y, n => n.sectorId == S.id && n != c && Linkable(n) && Math.Abs(n.ring - b) <= 1);
                            if (nn != null) Travel(g, nn, c, S, b);
                        }
                    }
                }
            }

            // Content: deterministic in creation order.
            var usedNames = new HashSet<string>();
            var usedKeys = new HashSet<string>();
            foreach (var n in data.nodes)
            {
                if (n.ring == 0) continue;
                var s = data.GetSector(n.sectorId);
                if (s == null) continue;
                NodeGenOffline.Fill(n, s, data.GetSector(n.sectorId2), g.R, usedNames, usedKeys);
            }

            // g-space -> UI space.
            foreach (var n in data.nodes)
            {
                n.x *= Scale;
                n.y *= -Scale;
                n.radius = (float)Math.Sqrt(n.x * n.x + n.y * n.y);
                n.angle = (float)Math.Atan2(n.y, n.x);
            }
            foreach (var s in data.sectors)
            {
                s.angleCenter = -angle[s.id];
                s.angleSpan = span;
            }
            data.worldRadius = (BandBase + rings * BandStep + 140f) * Scale;
            data.layoutVersion = Version;
            data.InvalidateIndex();
        }

        static Cluster BuildCluster(Gen g, string kind, float cx, float cy, float ang, WebSector sec, int b)
        {
            var cl = new Cluster();
            int id = g.clusterSeq++;
            float dx = (float)Math.Cos(ang), dy = (float)Math.Sin(ang);

            if (kind == "wheel")
            {
                var hub = g.Add(Node(WebNodeType.Notable, cx, cy, sec, b, id));
                cl.nodes.Add(hub);
                int m = 5 + g.R.Next(3);
                double st = g.R.NextDouble() * Math.PI * 2;
                var ring = new List<WebNode>();
                for (int i = 0; i < m; i++)
                {
                    double t = st + i * Math.PI * 2 / m;
                    ring.Add(g.Add(Node(WebNodeType.Basic, cx + 54f * (float)Math.Cos(t), cy + 54f * (float)Math.Sin(t), sec, b, id)));
                }
                for (int i = 0; i < m - 1; i++) Gen.Link(ring[i], ring[i + 1]);
                Gen.Link(hub, ring[1]);
                Gen.Link(hub, ring[m - 2]);
                cl.nodes.AddRange(ring);
            }
            else if (kind == "chain")
            {
                int len = 3 + g.R.Next(2);
                float px = -dy, py = dx, bend = g.Jitter(40f);
                WebNode prev = null;
                for (int i = 0; i <= len; i++)
                {
                    float f = (float)i / len, off = (float)Math.Sin(f * Math.PI) * bend;
                    float x = cx + (f - 0.5f) * 150f * dx + off * px;
                    float y = cy + (f - 0.5f) * 150f * dy + off * py;
                    var n = g.Add(Node(i == len ? WebNodeType.Notable : WebNodeType.Basic, x, y, sec, b, id));
                    cl.nodes.Add(n);
                    if (prev != null) Gen.Link(prev, n);
                    prev = n;
                }
            }
            else if (kind == "mastery")
            {
                cl.nodes.Add(g.Add(Node(WebNodeType.Mastery, cx, cy, sec, b, id)));
                double s0 = g.R.NextDouble() * Math.PI * 2;
                var rr = new List<WebNode>();
                for (int i = 0; i < 6; i++)
                {
                    double t = s0 + i * Math.PI / 3;
                    var type = (i == 1 || i == 4) ? WebNodeType.Notable : WebNodeType.Basic;
                    rr.Add(g.Add(Node(type, cx + 62f * (float)Math.Cos(t), cy + 62f * (float)Math.Sin(t), sec, b, id)));
                }
                for (int i = 0; i < 5; i++) Gen.Link(rr[i], rr[i + 1]);
                cl.nodes.AddRange(rr);
            }
            else
            {
                var key = g.Add(Node(WebNodeType.Keystone, cx, cy, sec, b, id));
                var l1 = g.Add(Node(WebNodeType.Basic, cx - 48f * dx, cy - 48f * dy, sec, b, id));
                var l2 = g.Add(Node(WebNodeType.Basic, cx - 94f * dx, cy - 94f * dy, sec, b, id));
                Gen.Link(l2, l1);
                Gen.Link(l1, key);
                cl.nodes.Add(key); cl.nodes.Add(l1); cl.nodes.Add(l2);
                cl.entry = l2;
                return cl;
            }

            // Enter the cluster from whichever linkable node faces the Heart.
            cl.entry = g.Nearest(0, 0, Linkable, cl.nodes);
            return cl;
        }

        /// <summary>Joins a to b with a string of Travel nodes spaced ~62g apart.</summary>
        static void Travel(Gen g, WebNode a, WebNode b, WebSector sec, int ring)
        {
            float dx = b.x - a.x, dy = b.y - a.y;
            float d = (float)Math.Sqrt(dx * dx + dy * dy);
            int steps = Math.Max(1, (int)Math.Round(d / 62f));
            var prev = a;
            for (int i = 1; i < steps; i++)
            {
                float x = a.x + dx * i / steps + g.Jitter(10f);
                float y = a.y + dy * i / steps + g.Jitter(10f);
                if (!g.Clear(x, y, 26f)) continue;
                var t = g.Add(Node(WebNodeType.Travel, x, y, sec, ring));
                Gen.Link(prev, t);
                prev = t;
            }
            Gen.Link(prev, b);
        }
    }
}
