using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace AIROG_VertexAI
{
    /// <summary>
    /// Builds our extra Options rows by cloning existing ones. Cloning rather than
    /// borrowing the game's own widgets keeps our key and model selection completely
    /// separate from the prefs those widgets write, so switching between Vertex and the
    /// stock OpenAI-compatible backend never clobbers the other's settings.
    /// </summary>
    internal static class VertexMenuUi
    {
        /// <summary>
        /// Duplicates a settings row next to its template. The clone keeps the prefab's
        /// layout but has all inspector-wired callbacks disabled, so it drives nothing but us.
        /// </summary>
        public static GameObject CloneRow(Transform template, string name)
        {
            if (template == null || template.parent == null) return null;

            GameObject clone = Object.Instantiate(template.gameObject, template.parent, false);
            clone.name = name;
            clone.transform.SetSiblingIndex(template.GetSiblingIndex() + 1);

            foreach (TMP_InputField field in clone.GetComponentsInChildren<TMP_InputField>(true))
            {
                DisablePersistentListeners(field.onValueChanged);
                DisablePersistentListeners(field.onEndEdit);
                DisablePersistentListeners(field.onSubmit);
                DisablePersistentListeners(field.onSelect);
                DisablePersistentListeners(field.onDeselect);
                field.onValueChanged.RemoveAllListeners();
                field.onEndEdit.RemoveAllListeners();
            }
            foreach (TMP_Dropdown dropdown in clone.GetComponentsInChildren<TMP_Dropdown>(true))
            {
                DisablePersistentListeners(dropdown.onValueChanged);
                dropdown.onValueChanged.RemoveAllListeners();
            }
            foreach (UnityEngine.UI.Button button in clone.GetComponentsInChildren<UnityEngine.UI.Button>(true))
            {
                // TMP_Dropdown drives its own button internally; only the prefab's
                // inspector-wired MainMenu callbacks need silencing.
                if (button.GetComponent<TMP_Dropdown>() != null) continue;
                DisablePersistentListeners(button.onClick);
                button.onClick.RemoveAllListeners();
            }

            return clone;
        }

        /// <summary>
        /// UnityEvent.RemoveAllListeners only drops runtime listeners; callbacks wired in
        /// the prefab's inspector survive cloning and must be switched off individually.
        /// </summary>
        public static void DisablePersistentListeners(UnityEventBase ev)
        {
            if (ev == null) return;
            for (int i = ev.GetPersistentEventCount() - 1; i >= 0; i--)
                ev.SetPersistentListenerState(i, UnityEventCallState.Off);
        }

        /// <summary>
        /// Retitles a cloned row. The label is the first text element that isn't part of
        /// the row's own control (an input field's text/placeholder, a dropdown's caption).
        /// </summary>
        public static void SetRowLabel(GameObject row, string label)
        {
            if (row == null) return;

            Transform controlRoot = null;
            TMP_InputField field = row.GetComponentInChildren<TMP_InputField>(true);
            if (field != null) controlRoot = field.transform;
            if (controlRoot == null)
            {
                TMP_Dropdown dropdown = row.GetComponentInChildren<TMP_Dropdown>(true);
                if (dropdown != null) controlRoot = dropdown.transform;
            }

            TMP_Text labelText = row.GetComponentsInChildren<TMP_Text>(true)
                .FirstOrDefault(t => controlRoot == null || !t.transform.IsChildOf(controlRoot));
            if (labelText != null) labelText.text = label;
        }

        public static void SetPlaceholder(TMP_InputField field, string text)
        {
            if (field?.placeholder is TMP_Text placeholder) placeholder.text = text;
        }

        public static void SetActive(GameObject go, bool active)
        {
            if (go != null && go.activeSelf != active) go.SetActive(active);
        }

        /// <summary>
        /// Transform overload. Kept separate so the Unity null check happens on the
        /// Transform itself — <c>transform?.gameObject</c> would sail past a destroyed
        /// object's fake null and throw.
        /// </summary>
        public static void SetActive(Transform t, bool active)
        {
            if (t == null) return;
            SetActive(t.gameObject, active);
        }
    }
}
