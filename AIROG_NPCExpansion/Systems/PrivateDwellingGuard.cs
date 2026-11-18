using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    /// <summary>
    /// Keeps NPC Expansion's simulation out of private dwellings (homes the game flags with
    /// <c>Place.isPrivateDwelling</c>). The game itself bars random events from them unless an
    /// event opts in; this is the same courtesy for our systems.
    ///
    /// NPC Expansion never moves an NPC's parentPlace directly. The intrusion comes from the
    /// scenario update, which asks the AI "where is this NPC now?" with the player's story as
    /// context. The AI would answer "heading to the player's cottage", and that text then fed the
    /// game log, the native background, rumours and GenContext, so the storyteller played it out.
    ///
    /// <c>isPrivateDwelling</c> only exists in newer game builds, so it is read by reflection:
    /// on builds without it every check here quietly returns false.
    /// </summary>
    internal static class PrivateDwellingGuard
    {
        private static FieldInfo _field;
        private static bool _resolved;

        private static FieldInfo Field
        {
            get
            {
                if (!_resolved)
                {
                    _resolved = true;
                    _field = typeof(Place).GetField("isPrivateDwelling", BindingFlags.Public | BindingFlags.Instance);
                    if (_field == null)
                        Debug.Log("[NPCExpansion] This game build has no private dwellings; dwelling checks are inactive.");
                }
                return _field;
            }
        }

        // Place.parentPlace and GameCharacter.parentPlace are legacy fields: the game only fills them
        // when it hydrates an old save and never keeps them current. Walking them meant the loops
        // below only ever looked at the place itself, and every character counted as being nowhere.
        // The live tree hangs off the parent entity.
        private static Place ParentOf(Place place) => place?.parentInGameEnt?.ParentPl();

        /// <summary>
        /// Where the character actually is. Party members are not children of any place, so for
        /// them it is wherever the player is.
        /// </summary>
        public static Place PlaceOf(GameCharacter npc)
        {
            if (npc == null) return null;
            try
            {
                if (npc.IsInParty()) return SS.I?.hackyManager?.currentPlace;
                return npc.parentInGameEnt?.ParentPl();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True if the place, or any place it sits inside (a room in a house), is a private dwelling.</summary>
        public static bool IsPrivate(Place place)
        {
            var f = Field;
            if (f == null || place == null) return false;
            try
            {
                int guard = 0;
                for (var p = place; p != null && guard < 16; p = ParentOf(p), guard++)
                    if (f.GetValue(p) is bool b && b) return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[NPCExpansion] Private dwelling check failed: " + ex.Message);
            }
            return false;
        }

        /// <summary>The outermost private place containing <paramref name="place"/>, or null.</summary>
        public static Place DwellingOf(Place place)
        {
            var f = Field;
            if (f == null || place == null) return null;
            Place found = null;
            int guard = 0;
            for (var p = place; p != null && guard < 16; p = ParentOf(p), guard++)
                if (f.GetValue(p) is bool b && b) found = p;
            return found;
        }

        /// <summary>
        /// Names of every private dwelling in the world except the one <paramref name="npc"/> is
        /// currently inside (someone already at home may stay there), rooms of that home included.
        /// </summary>
        public static List<string> ForbiddenDwellingNames(GameCharacter npc)
        {
            var names = new List<string>();
            if (Field == null || SS.I?.uuidToGameEntityMap == null) return names;
            Place own = null;
            try { own = DwellingOf(PlaceOf(npc)); } catch { }

            try
            {
                // The game locks this map whenever it adds entities, which it does from background tasks.
                List<Place> places;
                lock (SS.I.uuidToGameEntityMap)
                    places = SS.I.uuidToGameEntityMap.Values.OfType<Place>().ToList();

                foreach (var pl in places)
                {
                    if (!(Field.GetValue(pl) is bool b) || !b) continue;
                    if (own != null && (pl == own || DwellingOf(pl) == own)) continue;
                    string n = pl.GetPrettyName();
                    if (!string.IsNullOrWhiteSpace(n) && !names.Contains(n)) names.Add(n.Trim());
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[NPCExpansion] Could not list private dwellings: " + ex.Message);
            }
            return names;
        }

        /// <summary>Returns the first forbidden dwelling name the text mentions, or null.</summary>
        public static string MentionedDwelling(string text, List<string> forbidden)
        {
            if (string.IsNullOrEmpty(text) || forbidden == null) return null;
            // Very short names ("Hut") would match inside ordinary words; require 4+ characters.
            return forbidden.FirstOrDefault(n => n.Length >= 4 && text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
