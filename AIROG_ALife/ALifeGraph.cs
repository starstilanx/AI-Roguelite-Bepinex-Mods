using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AIROG_ALife
{
    /// <summary>
    /// Travel graph over the current world's top-level places, built from worldCoords
    /// nearest-neighbor distances (the same signal the game's own map/travel-range uses).
    /// This is the A-Life equivalent of STALKER's smart-terrain graph: squads hop one
    /// edge at a time. Rebuilt lazily whenever the place set changes.
    /// </summary>
    public static class ALifeGraph
    {
        private const int MAX_NEIGHBORS = 4;
        private static readonly System.Random Rng = new System.Random();

        private static Dictionary<string, List<string>> _neighbors = new Dictionary<string, List<string>>();
        private static int _builtForSignature = -1;
        private static string _builtForVwUuid;

        public static Place PlaceByUuid(string uuid)
        {
            if (string.IsNullOrEmpty(uuid) || SS.I?.uuidToGameEntityMap == null) return null;
            GameEntity ge;
            SS.I.uuidToGameEntityMap.TryGetValue(uuid, out ge);
            return ge as Place;
        }

        public static List<Place> GetTopPlaces(GameplayManager manager)
        {
            try
            {
                return manager?.GetCurrentVoronoiWorld()?.GetAllTopLvlPlaces() ?? new List<Place>();
            }
            catch
            {
                return new List<Place>();
            }
        }

        /// <summary>
        /// Order-independent signature over a place set's uuids. Only needs to be stable
        /// within this process run (it's computed and compared moments apart, never
        /// persisted), so string.GetHashCode()'s per-process randomization is fine here.
        /// </summary>
        private static int PlaceSetSignature(List<Place> places)
        {
            unchecked
            {
                int sig = 0;
                foreach (Place p in places)
                    sig ^= (p.uuid?.GetHashCode() ?? 0) * 397 + places.Count;
                return sig;
            }
        }

        public static void EnsureBuilt(GameplayManager manager)
        {
            List<Place> places = GetTopPlaces(manager);
            string vwUuid = manager?.GetCurrentVoronoiWorld()?.uuid;
            // A place-set signature, not just a count: a same-tick swap (one place removed,
            // a different one added) leaves the count unchanged, which used to skip the
            // rebuild and strand squads routing through the new place with no adjacency
            // entry at all.
            int signature = PlaceSetSignature(places);
            if (signature == _builtForSignature && vwUuid == _builtForVwUuid) return;

            _neighbors = new Dictionary<string, List<string>>();
            _builtForSignature = signature;
            _builtForVwUuid = vwUuid;
            if (places.Count < 2) return;

            // Median nearest-neighbor spacing → distance cap, so islands of far-flung
            // places don't get absurd cross-map edges.
            var nnDists = new List<float>();
            foreach (Place p in places)
            {
                float best = float.MaxValue;
                foreach (Place q in places)
                    if (q != p) best = Mathf.Min(best, Vector2.Distance(p.worldCoords, q.worldCoords));
                if (best < float.MaxValue) nnDists.Add(best);
            }
            nnDists.Sort();
            float cap = nnDists[nnDists.Count / 2] * 3.0f;

            foreach (Place p in places)
            {
                var near = places
                    .Where(q => q != p)
                    .OrderBy(q => Vector2.Distance(p.worldCoords, q.worldCoords))
                    .Take(MAX_NEIGHBORS)
                    .Where(q => Vector2.Distance(p.worldCoords, q.worldCoords) <= cap)
                    .Select(q => q.uuid)
                    .ToList();
                // Always keep at least the single nearest place so nothing is stranded.
                if (near.Count == 0)
                {
                    Place nearest = places.Where(q => q != p)
                        .OrderBy(q => Vector2.Distance(p.worldCoords, q.worldCoords)).FirstOrDefault();
                    if (nearest != null) near.Add(nearest.uuid);
                }
                _neighbors[p.uuid] = near;
            }
            Debug.Log($"[ALife] Travel graph rebuilt: {places.Count} places, cap {cap:0.0}.");
        }

        public static List<Place> Neighbors(string placeUuid)
        {
            List<string> uuids;
            if (!_neighbors.TryGetValue(placeUuid, out uuids)) return new List<Place>();
            return uuids.Select(PlaceByUuid).Where(p => p != null).ToList();
        }

        public static Place RandomNeighbor(string placeUuid)
        {
            var n = Neighbors(placeUuid);
            return n.Count == 0 ? null : n[Rng.Next(n.Count)];
        }

        /// <summary>Adjacent place that gets the squad closest to the target's coords.</summary>
        public static Place NextHopToward(string fromUuid, string targetUuid)
        {
            Place target = PlaceByUuid(targetUuid);
            if (target == null) return RandomNeighbor(fromUuid);
            var n = Neighbors(fromUuid);
            if (n.Count == 0) return null;
            return n.OrderBy(p => Vector2.Distance(p.worldCoords, target.worldCoords)).First();
        }
    }
}
