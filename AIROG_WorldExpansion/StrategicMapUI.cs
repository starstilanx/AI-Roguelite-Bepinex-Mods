using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_WorldExpansion
{
    // ─── Grand Strategy: Political Map Lens ──────────────────────────────────────
    // Overlays the game's world map (MapModal, WORLD mode) with faction territory:
    // each top-level place gets a Voronoi cell tinted by its owner's faction color,
    // war fronts are drawn as thick red borders between belligerent territories,
    // and a legend panel summarizes every faction's standing, population and wars.
    // Toggled by a "Political" button on the map, or the WORLD_MAP console command.
    public static class StrategicMapUI
    {
        private const string OVERLAY_NAME = "PoliticalOverlay_Mod";
        private const string LEGEND_NAME  = "PoliticalLegend_Mod";
        private const string BUTTON_NAME  = "PoliticalLensButton_Mod";

        private const float BBOX_PAD        = 250f;  // map units around outermost places
        private const float BORDER_WIDTH    = 4f;    // borders of owned territory
        private const float WILD_WIDTH      = 2f;    // borders between two unowned cells
        private const float WAR_FRONT_WIDTH = 18f;   // borders between factions at war
        private const float PLAYER_WIDTH    = 8f;    // border of the cell the player stands in

        private static readonly Color UNOWNED_FILL   = new Color(0.5f, 0.5f, 0.5f, 0.06f);
        private static readonly Color BORDER_COLOR   = new Color(0.08f, 0.08f, 0.12f, 0.5f);
        private static readonly Color WILD_COLOR     = new Color(0.1f, 0.1f, 0.1f, 0.12f);
        private static readonly Color WAR_FRONT_COLOR = new Color(1f, 0.25f, 0.15f, 0.9f);
        private static readonly Color PLAYER_COLOR   = new Color(1f, 0.85f, 0.2f, 0.9f);

        private static bool _lensOn;

        public static bool LensRequested; // set by the WORLD_MAP console command

        // ─── MapModal hooks ───────────────────────────────────────────────────────

        [HarmonyPatch(typeof(MapModal), "ShowWorldView")]
        [HarmonyPostfix]
        public static void Postfix_ShowWorldView(MapModal __instance, VoronoiWorld vw)
        {
            try
            {
                if (LensRequested) { _lensOn = true; LensRequested = false; }
                EnsureLensButton(__instance);
                if (_lensOn) BuildOverlay(__instance, vw);
                else ClearOverlay(__instance);
            }
            catch (Exception e)
            {
                Debug.LogError($"[WorldExpansion] Political lens failed: {e}");
            }
        }

        [HarmonyPatch(typeof(MapModal), "ShowDetachedView")]
        [HarmonyPostfix]
        public static void Postfix_ShowDetachedView(MapModal __instance)
        {
            // Detached places have no territory; the map redraw already destroyed the
            // overlay polys, but the legend/button live outside mapLocationsParent
            SetLegendVisible(__instance, false);
            SetButtonVisible(__instance, false);
        }

        [HarmonyPatch(typeof(MapModal), "ShowUniv")]
        [HarmonyPostfix]
        public static void Postfix_ShowUniv(MapModal __instance)
        {
            SetLegendVisible(__instance, false);
            SetButtonVisible(__instance, false);
        }

        [HarmonyPatch(typeof(MapModal), "HideMapModal")]
        [HarmonyPostfix]
        public static void Postfix_HideMapModal(MapModal __instance)
        {
            SetLegendVisible(__instance, false);
        }

        // ─── Toggle button ────────────────────────────────────────────────────────

        private static void EnsureLensButton(MapModal modal)
        {
            if (modal.jumpToCurrentLocationButton == null) return;
            Transform parent = modal.jumpToCurrentLocationButton.transform.parent;
            Transform existing = parent.Find(BUTTON_NAME);
            if (existing != null)
            {
                existing.gameObject.SetActive(true);
                UpdateButtonLabel(existing.gameObject);
                return;
            }

            GameObject btnObj = UnityEngine.Object.Instantiate(
                modal.jumpToCurrentLocationButton.gameObject, parent);
            btnObj.name = BUTTON_NAME;

            // ButtonPressEffect caches its child Graphics in Awake (which already ran on
            // Instantiate); destroying the cloned children below would leave it holding
            // dead references and NRE on every press — remove it outright
            var pressFx = btnObj.GetComponent<ButtonPressEffect>();
            if (pressFx != null) UnityEngine.Object.DestroyImmediate(pressFx);

            // The map's utility buttons stack vertically — moving up puts us behind a
            // sibling, so step out to the LEFT of the column instead
            RectTransform rt = (RectTransform)btnObj.transform;
            RectTransform srcRt = (RectTransform)modal.jumpToCurrentLocationButton.transform;
            rt.anchoredPosition = srcRt.anchoredPosition + new Vector2(-(srcRt.rect.width + 12f), 0f);

            // Strip the cloned icon/text so we don't look identical to the jump button;
            // keep only the root frame Image and give it our own face
            foreach (var img in btnObj.GetComponentsInChildren<Image>(true))
                if (img.gameObject != btnObj) UnityEngine.Object.DestroyImmediate(img.gameObject);
            foreach (var raw in btnObj.GetComponentsInChildren<RawImage>(true))
                if (raw != null && raw.gameObject != btnObj) UnityEngine.Object.DestroyImmediate(raw.gameObject);
            foreach (var t in btnObj.GetComponentsInChildren<TMP_Text>(true))
                if (t != null) UnityEngine.Object.DestroyImmediate(t.gameObject);

            GameObject txtObj = new GameObject("PolLabel", typeof(RectTransform));
            txtObj.layer = btnObj.layer;
            txtObj.transform.SetParent(btnObj.transform, false);
            var trt = (RectTransform)txtObj.transform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;
            var lbl = txtObj.AddComponent<TextMeshProUGUI>();
            if (modal.voronoiWorldTitle != null) lbl.font = modal.voronoiWorldTitle.font;
            lbl.text = "POL";
            lbl.fontSize = 15;
            lbl.fontStyle = FontStyles.Bold;
            lbl.alignment = TextAlignmentOptions.Center;
            lbl.raycastTarget = false;

            Button btn = btnObj.GetComponent<Button>();
            btn.onClick = new Button.ButtonClickedEvent(); // drops cloned persistent listeners too
            btn.onClick.AddListener(() => OnLensToggled(modal));

            UpdateButtonLabel(btnObj);
        }

        // State feedback: gold when the lens is on, muted when off
        private static void UpdateButtonLabel(GameObject btnObj)
        {
            var txt = btnObj.GetComponentInChildren<TMP_Text>();
            if (txt != null) txt.color = _lensOn ? new Color(1f, 0.85f, 0.3f) : new Color(0.78f, 0.78f, 0.83f);
            var frame = btnObj.GetComponent<Image>();
            if (frame != null) frame.color = _lensOn ? new Color(1f, 0.95f, 0.7f) : Color.white;
        }

        private static void OnLensToggled(MapModal modal)
        {
            _lensOn = !_lensOn;
            modal.manager?.soundManager?.smallClickSoundFxObj?.PlayNextSound();

            Transform btn = modal.jumpToCurrentLocationButton?.transform.parent.Find(BUTTON_NAME);
            if (btn != null) UpdateButtonLabel(btn.gameObject);

            if (_lensOn) BuildOverlay(modal, modal.manager?.currentPlace?.GetVw());
            else ClearOverlay(modal);
        }

        private static void SetButtonVisible(MapModal modal, bool visible)
        {
            Transform btn = modal.jumpToCurrentLocationButton?.transform.parent.Find(BUTTON_NAME);
            if (btn != null) btn.gameObject.SetActive(visible);
        }

        // ─── Overlay construction ─────────────────────────────────────────────────

        private static void ClearOverlay(MapModal modal)
        {
            Transform old = modal.mapLocationsParent?.Find(OVERLAY_NAME);
            if (old != null) UnityEngine.Object.Destroy(old.gameObject);
            SetLegendVisible(modal, false);
        }

        private static void BuildOverlay(MapModal modal, VoronoiWorld vw)
        {
            ClearOverlay(modal);
            if (vw == null || modal.mapLocationsParent == null) return;
            GameplayManager manager = modal.manager;
            if (manager == null) return;

            List<Place> places = vw.regions
                .Where(r => r != null && r.places != null)
                .SelectMany(r => r.places)
                .Where(p => p != null)
                .Distinct()
                .ToList();
            if (places.Count == 0) return;

            // Faction lookups
            var factionByUuid = new Dictionary<string, Faction>();
            foreach (var f in manager.GetCurrentFactions() ?? new List<Faction>())
                if (f != null && !factionByUuid.ContainsKey(f.uuid)) factionByUuid[f.uuid] = f;

            Dictionary<string, string> ownerByPlace = ResolveOwnership(places);

            // Sites — nudge exact duplicates so the Voronoi doesn't degenerate
            var seen = new Dictionary<Vector2, int>();
            var sites = new List<Vector2>(places.Count);
            foreach (var p in places)
            {
                Vector2 pos = p.worldCoords;
                if (seen.TryGetValue(pos, out int n)) { seen[pos] = n + 1; pos += new Vector2(7f * n + 7f, 3f * n + 3f); }
                else seen[pos] = 1;
                sites.Add(pos);
            }

            float minX = sites.Min(s => s.x) - BBOX_PAD, maxX = sites.Max(s => s.x) + BBOX_PAD;
            float minY = sites.Min(s => s.y) - BBOX_PAD, maxY = sites.Max(s => s.y) + BBOX_PAD;
            float cellRadius = ComputeCellRadius(sites);

            GameObject root = new GameObject(OVERLAY_NAME, typeof(RectTransform));
            root.layer = modal.mapLocationsParent.gameObject.layer;
            // MapLocations sit at localPosition == worldCoords (relative to the parent's
            // pivot). Default RectTransform anchors would center us on the parent's rect
            // instead, which drifts when the parent has asymmetric offsets — so pin the
            // overlay's local origin to the parent pivot to share the icons' space.
            PinToParentPivot(root, modal.mapLocationsParent);

            string playerPlaceUuid = manager.currentPlace?.GetTopLvlPlace()?.uuid;
            var largestCellByFaction = new Dictionary<string, (float area, Vector2 centroid)>();

            for (int i = 0; i < places.Count; i++)
            {
                // Start from the map bounding box (CCW) and clip by every bisector
                var pts  = new List<Vector2> {
                    new Vector2(minX, minY), new Vector2(maxX, minY),
                    new Vector2(maxX, maxY), new Vector2(minX, maxY) };
                var tags = new List<string> { null, null, null, null };
                for (int j = 0; j < places.Count; j++)
                {
                    if (i == j) continue;
                    ClipByBisector(pts, tags, sites[i], sites[j], places[j].uuid);
                    if (pts.Count < 3) break;
                }
                // Bound the cell to a disc around its own place: hull cells of a
                // clustered map would otherwise explode into huge wedges reaching
                // the bbox, putting "territory" far from anything it belongs to
                for (int k = 0; k < 12 && pts.Count >= 3; k++)
                {
                    float ang = k * Mathf.PI * 2f / 12f;
                    Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                    ClipHalfPlane(pts, tags, dir, Vector2.Dot(dir, sites[i]) + cellRadius, "");
                }
                if (pts.Count < 3) continue;

                string ownerUuid = ownerByPlace.TryGetValue(places[i].uuid, out var o) ? o : null;
                Faction owner = ownerUuid != null && factionByUuid.TryGetValue(ownerUuid, out var fo) ? fo : null;

                Color fill = UNOWNED_FILL;
                if (owner != null)
                {
                    float alpha = Mathf.Clamp(SS.I.regionColorOpacity + 0.15f, 0.25f, 0.5f);
                    fill = new Color(owner.color.r, owner.color.g, owner.color.b, alpha);
                }

                // Border colors per edge: war front > player location > owned > wilderness.
                // Map-boundary edges (null tag) get no border so the bbox stays invisible.
                var edgeColors = new List<Color>(pts.Count);
                var edgeWidths = new List<float>(pts.Count);
                bool isPlayerCell = places[i].uuid == playerPlaceUuid;
                for (int e = 0; e < pts.Count; e++)
                {
                    string neighborPlace = tags[e];
                    string neighborOwner = neighborPlace != null && ownerByPlace.TryGetValue(neighborPlace, out var no) ? no : null;
                    bool warFront = ownerUuid != null && neighborOwner != null && ownerUuid != neighborOwner
                        && WorldData.CurrentState.ActiveWars.ContainsKey(WorldData.GetRelationshipKey(ownerUuid, neighborOwner));
                    if (warFront)                { edgeColors.Add(WAR_FRONT_COLOR); edgeWidths.Add(WAR_FRONT_WIDTH); }
                    else if (isPlayerCell)       { edgeColors.Add(PLAYER_COLOR);    edgeWidths.Add(PLAYER_WIDTH); }
                    else if (string.IsNullOrEmpty(neighborPlace))
                    {
                        // Outer boundary (bbox or disc rim): outline owned blobs, leave wilderness open
                        if (ownerUuid != null)   { edgeColors.Add(BORDER_COLOR);    edgeWidths.Add(BORDER_WIDTH); }
                        else                     { edgeColors.Add(Color.clear);     edgeWidths.Add(0f); }
                    }
                    else if (ownerUuid != null || neighborOwner != null)
                                                 { edgeColors.Add(BORDER_COLOR);    edgeWidths.Add(BORDER_WIDTH); }
                    else                         { edgeColors.Add(WILD_COLOR);      edgeWidths.Add(WILD_WIDTH); }
                }

                GameObject cellObj = new GameObject("Cell_" + places[i].uuid, typeof(RectTransform));
                cellObj.layer = root.layer;
                PinToParentPivot(cellObj, root.transform);
                var cell = cellObj.AddComponent<PoliticalCellGraphic>();
                cell.raycastTarget = false;
                cell.Setup(pts, fill, edgeColors, edgeWidths);

                if (owner != null)
                {
                    float area = PolygonArea(pts);
                    if (!largestCellByFaction.TryGetValue(ownerUuid, out var best) || area > best.area)
                        largestCellByFaction[ownerUuid] = (area, Centroid(pts));
                }
            }

            // Faction name labels on their largest territory
            TMP_FontAsset font = modal.voronoiWorldTitle != null ? modal.voronoiWorldTitle.font : null;
            foreach (var kvp in largestCellByFaction)
            {
                if (!factionByUuid.TryGetValue(kvp.Key, out var f)) continue;
                GameObject lblObj = new GameObject("Label_" + f.GetPrettyName(), typeof(RectTransform));
                lblObj.layer = root.layer;
                PinToParentPivot(lblObj, root.transform);
                var lbl = lblObj.AddComponent<TextMeshProUGUI>();
                if (font != null) lbl.font = font;
                lbl.text = f.GetPrettyName();
                // Scale to the cell: text width ≈ len·0.55·fontSize should stay inside
                // the cell's approximate width (√area)
                float approxWidth = Mathf.Sqrt(kvp.Value.area);
                lbl.fontSize = Mathf.Clamp(
                    approxWidth * 1.4f / Mathf.Max(4, f.GetPrettyName().Length), 9f, 30f);
                lbl.fontStyle = FontStyles.Bold;
                lbl.alignment = TextAlignmentOptions.Center;
                lbl.color = new Color(
                    Mathf.Clamp01(f.color.r * 0.6f + 0.4f),
                    Mathf.Clamp01(f.color.g * 0.6f + 0.4f),
                    Mathf.Clamp01(f.color.b * 0.6f + 0.4f), 0.95f);
                lbl.raycastTarget = false;
                lbl.enableWordWrapping = false;
                var lrt = (RectTransform)lblObj.transform;
                lrt.localPosition = kvp.Value.centroid; // same pivot-relative space as the cells
                lrt.sizeDelta = new Vector2(1200, 60);
            }

            // Keep place icons clickable above the tinted cells
            foreach (var loc in modal.mapLocationsParent.GetComponentsInChildren<MapLocation>())
                loc.transform.SetAsLastSibling();

            BuildLegend(modal, factionByUuid, ownerByPlace, vw);
        }

        // Parents obj under parent with its local origin exactly on the parent's pivot —
        // the same reference point MapLocation icons use (localPosition == worldCoords) —
        // so cell vertices in worldCoords units land on top of the icons.
        private static void PinToParentPivot(GameObject obj, Transform parent)
        {
            var rt = (RectTransform)obj.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = Vector2.zero;
            rt.localScale = Vector3.one;
            rt.localPosition = Vector3.zero;
        }

        // Native Place.faction wins; mod-claimed territory (ClaimedPlaceUuids) fills the rest
        private static Dictionary<string, string> ResolveOwnership(List<Place> places)
        {
            var owner = new Dictionary<string, string>();
            foreach (var p in places)
                if (p.faction != null) owner[p.uuid] = p.faction.uuid;
            foreach (var kvp in WorldData.CurrentState.Factions)
            {
                if (WorldData.CurrentState.EliminatedFactions.Contains(kvp.Key)) continue;
                foreach (var uuid in kvp.Value.ClaimedPlaceUuids)
                    if (!owner.ContainsKey(uuid)) owner[uuid] = kvp.Key;
            }
            return owner;
        }

        // ─── Legend panel ─────────────────────────────────────────────────────────

        private static void SetLegendVisible(MapModal modal, bool visible)
        {
            Transform legend = modal.mapViewTrans?.Find(LEGEND_NAME);
            if (legend != null) legend.gameObject.SetActive(visible);
        }

        private static void BuildLegend(MapModal modal, Dictionary<string, Faction> factionByUuid,
            Dictionary<string, string> ownerByPlace, VoronoiWorld vw)
        {
            if (modal.mapViewTrans == null) return;
            Transform old = modal.mapViewTrans.Find(LEGEND_NAME);
            if (old != null) UnityEngine.Object.Destroy(old.gameObject);

            TMP_FontAsset font = modal.voronoiWorldTitle != null ? modal.voronoiWorldTitle.font : null;

            GameObject panel = new GameObject(LEGEND_NAME, typeof(RectTransform));
            panel.layer = modal.mapViewTrans.gameObject.layer;
            panel.transform.SetParent(modal.mapViewTrans, false);
            var prt = (RectTransform)panel.transform;
            prt.anchorMin = new Vector2(1, 0.5f);
            prt.anchorMax = new Vector2(1, 0.5f);
            prt.pivot     = new Vector2(1, 0.5f);
            prt.anchoredPosition = new Vector2(-14, 0);
            prt.sizeDelta = new Vector2(330, 100);

            panel.AddComponent<Image>().color = new Color(0.06f, 0.06f, 0.09f, 0.92f);
            var vlg = panel.AddComponent<VerticalLayoutGroup>();
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.spacing = 4;
            vlg.padding = new RectOffset(12, 12, 10, 10);
            panel.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var st = WorldData.CurrentState;
            AddLegendText(panel, font, $"<b>⚔ POLITICAL MAP</b> — {vw.GetPrettyName()}", 19, new Color(1f, 0.9f, 0.5f));
            AddLegendText(panel, font, $"Turn {st.CurrentTurn} · {st.CurrentSeason} · Economy: {st.Market.GlobalCondition}", 14, new Color(0.8f, 0.8f, 0.85f));
            AddLegendText(panel, font, "────────────────────", 12, new Color(0.3f, 0.3f, 0.35f));

            // Factions holding territory, biggest first
            var holdings = ownerByPlace.Values
                .GroupBy(u => u)
                .ToDictionary(g => g.Key, g => g.Count());
            var listed = holdings.Keys
                .Where(u => factionByUuid.ContainsKey(u))
                .OrderByDescending(u => holdings[u])
                .ToList();

            // One compact line per faction so the panel stays clear of the travel
            // panel above and the map buttons below
            const int MAX_LISTED = 10;
            for (int i = 0; i < listed.Count; i++)
            {
                if (i >= MAX_LISTED)
                {
                    AddLegendText(panel, font, $"…and {listed.Count - MAX_LISTED} more", 13, new Color(0.6f, 0.6f, 0.65f));
                    break;
                }
                string uuid = listed[i];
                Faction f = factionByUuid[uuid];
                var ext = WorldData.GetFactionData(uuid);
                string bounty = st.PlayerBounties.Contains(uuid) ? " <color=#FF5544>☠</color>" : "";
                string popState = ext.PopState != "Normal" ? $" · {ext.PopState}" : "";

                var wars = st.ActiveWars.Values
                    .Where(w => w.ActorUuid == uuid || w.TargetUuid == uuid)
                    .Select(w => w.ActorUuid == uuid ? w.TargetName : w.ActorName)
                    .ToList();
                string warStr = wars.Count > 0 ? $" · <color=#FF7766>⚔ {string.Join(", ", wars)}</color>" : "";

                string colorHex = ColorUtility.ToHtmlStringRGB(f.color);
                AddLegendText(panel, font,
                    $"<color=#{colorHex}>■</color> <b>{f.GetPrettyName()}</b>{bounty} <color=#B8B8C0>· {StandingLabel(f.GetStanding())} · {holdings[uuid]}t · pop {FormatPop(ext.Population)}{popState}</color>{warStr}",
                    15, Color.white);
            }

            int unowned = vw.regions.Where(r => r?.places != null).SelectMany(r => r.places)
                .Count(p => p != null && !ownerByPlace.ContainsKey(p.uuid));
            if (unowned > 0)
            {
                AddLegendText(panel, font, "────────────────────", 14, new Color(0.3f, 0.3f, 0.35f));
                AddLegendText(panel, font, $"<color=#999999>■</color> Unclaimed: {unowned} place{(unowned == 1 ? "" : "s")}", 15, new Color(0.65f, 0.65f, 0.7f));
            }
        }

        private static void AddLegendText(GameObject panel, TMP_FontAsset font, string text, float size, Color color)
        {
            GameObject obj = new GameObject("LegendEntry", typeof(RectTransform));
            obj.layer = panel.layer;
            obj.transform.SetParent(panel.transform, false);
            var tmp = obj.AddComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.richText = true;
            tmp.raycastTarget = false;
        }

        private static string StandingLabel(Faction.FactionStanding s)
        {
            switch (s)
            {
                case Faction.FactionStanding.DESPISED:   return "Despised";
                case Faction.FactionStanding.SCORNED:    return "Scorned";
                case Faction.FactionStanding.DISTRUSTED: return "Distrusted";
                case Faction.FactionStanding.FAVORED:    return "Favored";
                case Faction.FactionStanding.TRUSTED:    return "Trusted";
                case Faction.FactionStanding.HONORED:    return "Honored";
                case Faction.FactionStanding.ADMIRED:    return "Admired";
                case Faction.FactionStanding.REVERED:    return "Revered";
                default:                                 return "Neutral";
            }
        }

        private static string FormatPop(int pop)
        {
            if (pop >= 1000) return $"{pop / 1000}.{(pop % 1000) / 100}k";
            return pop.ToString();
        }

        // ─── Voronoi geometry ─────────────────────────────────────────────────────

        // Sutherland–Hodgman clip of a convex polygon by the half-plane closer to
        // `site` than `other`. tags[i] names the neighbor whose bisector produced
        // edge pts[i]→pts[i+1] (null/"" = outer boundary), so war fronts know who's across.
        private static void ClipByBisector(List<Vector2> pts, List<string> tags,
            Vector2 site, Vector2 other, string otherUuid)
        {
            Vector2 n = other - site;
            ClipHalfPlane(pts, tags, n, Vector2.Dot(n, (site + other) * 0.5f), otherUuid);
        }

        // Generic half-plane clip: keeps {x : dot(n,x) <= d}; new edges along the
        // clip line are tagged newEdgeTag
        private static void ClipHalfPlane(List<Vector2> pts, List<string> tags,
            Vector2 n, float d, string newEdgeTag)
        {
            var outPts  = new List<Vector2>(pts.Count + 2);
            var outTags = new List<string>(pts.Count + 2);
            int count = pts.Count;
            for (int i = 0; i < count; i++)
            {
                Vector2 a = pts[i];
                Vector2 b = pts[(i + 1) % count];
                float da = Vector2.Dot(n, a) - d;
                float db = Vector2.Dot(n, b) - d;
                bool aIn = da <= 1e-4f;
                bool bIn = db <= 1e-4f;

                if (aIn)
                {
                    outPts.Add(a);
                    outTags.Add(tags[i]);
                    if (!bIn)
                    {
                        outPts.Add(Vector2.Lerp(a, b, da / (da - db)));
                        outTags.Add(newEdgeTag); // from here the boundary runs along the clip line
                    }
                }
                else if (bIn)
                {
                    outPts.Add(Vector2.Lerp(a, b, da / (da - db)));
                    outTags.Add(tags[i]); // resumes the original edge toward b
                }
            }

            pts.Clear();  pts.AddRange(outPts);
            tags.Clear(); tags.AddRange(outTags);
        }

        // Cell bounding radius: ~1.7× the median nearest-neighbor spacing, so cells
        // cover the gaps between neighboring places without sprawling into the void
        private static float ComputeCellRadius(List<Vector2> sites)
        {
            if (sites.Count < 2) return 400f;
            var nearest = new List<float>(sites.Count);
            for (int i = 0; i < sites.Count; i++)
            {
                float best = float.MaxValue;
                for (int j = 0; j < sites.Count; j++)
                {
                    if (i == j) continue;
                    float d = (sites[j] - sites[i]).sqrMagnitude;
                    if (d < best) best = d;
                }
                nearest.Add(Mathf.Sqrt(best));
            }
            nearest.Sort();
            return Mathf.Max(60f, nearest[nearest.Count / 2] * 1.7f);
        }

        private static float PolygonArea(List<Vector2> pts)
        {
            float a = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector2 p = pts[i], q = pts[(i + 1) % pts.Count];
                a += p.x * q.y - q.x * p.y;
            }
            return Mathf.Abs(a) * 0.5f;
        }

        private static Vector2 Centroid(List<Vector2> pts)
        {
            Vector2 c = Vector2.zero;
            foreach (var p in pts) c += p;
            return c / pts.Count;
        }
    }

    // Renders one convex Voronoi cell: a triangle-fan fill plus per-edge border
    // quads (inset toward the centroid) so war fronts can differ from normal borders.
    public class PoliticalCellGraphic : MaskableGraphic
    {
        private List<Vector2> _verts;
        private Color _fill;
        private List<Color> _edgeColors;
        private List<float> _edgeWidths;

        public void Setup(List<Vector2> verts, Color fill, List<Color> edgeColors, List<float> edgeWidths)
        {
            _verts = verts;
            _fill = fill;
            _edgeColors = edgeColors;
            _edgeWidths = edgeWidths;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (_verts == null || _verts.Count < 3) return;

            UIVertex v = UIVertex.simpleVert;
            v.color = _fill;
            for (int i = 0; i < _verts.Count; i++)
            {
                v.position = _verts[i];
                vh.AddVert(v);
            }
            for (int i = 1; i < _verts.Count - 1; i++)
                vh.AddTriangle(0, i, i + 1);

            Vector2 centroid = Vector2.zero;
            foreach (var p in _verts) centroid += p;
            centroid /= _verts.Count;

            for (int i = 0; i < _verts.Count; i++)
            {
                float w = _edgeWidths != null && i < _edgeWidths.Count ? _edgeWidths[i] : 0f;
                if (w <= 0f) continue;
                Vector2 a = _verts[i];
                Vector2 b = _verts[(i + 1) % _verts.Count];
                Vector2 dir = (b - a).normalized;
                Vector2 normal = new Vector2(-dir.y, dir.x);
                if (Vector2.Dot(normal, centroid - (a + b) * 0.5f) < 0) normal = -normal;

                UIVertex ev = UIVertex.simpleVert;
                ev.color = _edgeColors[i];
                int baseIdx = vh.currentVertCount;
                ev.position = a;                vh.AddVert(ev);
                ev.position = b;                vh.AddVert(ev);
                ev.position = b + normal * w;   vh.AddVert(ev);
                ev.position = a + normal * w;   vh.AddVert(ev);
                vh.AddTriangle(baseIdx, baseIdx + 1, baseIdx + 2);
                vh.AddTriangle(baseIdx, baseIdx + 2, baseIdx + 3);
            }
        }
    }
}
