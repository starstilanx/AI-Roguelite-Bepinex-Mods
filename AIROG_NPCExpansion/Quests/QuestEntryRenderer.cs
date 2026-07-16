using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_NPCExpansion
{
    /// <summary>Renders a single quest section header / quest card into a given scroll-content
    /// parent. Split out of QuestUI so window lifecycle and card rendering are separate concerns.</summary>
    internal static class QuestEntryRenderer
    {
        public static void AddSectionHeader(Transform parent, string text)
        {
            var go = new GameObject("Header", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 22);
            var txt = go.AddComponent<TextMeshProUGUI>();
            txt.text = text;
            txt.fontSize = 12;
            txt.color = new Color(0.8f, 0.7f, 0.3f, 1f);
            txt.fontStyle = FontStyles.Bold;
            txt.alignment = TextAlignmentOptions.Center;
        }

        public static void AddQuestEntry(Transform parent, QuestData quest)
        {
            var card = new GameObject("QuestCard", typeof(RectTransform));
            card.transform.SetParent(parent, false);
            var cardRect = card.GetComponent<RectTransform>();
            cardRect.sizeDelta = new Vector2(0, 80);

            var cardBg = card.AddComponent<Image>();
            cardBg.color = quest.Status switch
            {
                QuestStatus.Completed => new Color(0.05f, 0.15f, 0.05f, 0.9f),
                QuestStatus.Failed    => new Color(0.15f, 0.05f, 0.05f, 0.9f),
                _                     => new Color(0.08f, 0.08f, 0.15f, 0.9f)
            };

            var vlg = card.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(8, 8, 6, 6);
            vlg.spacing = 3;
            vlg.childControlWidth = true;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            card.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Giver + status badge
            string statusColor = quest.Status switch
            {
                QuestStatus.Completed => "#55ff55",
                QuestStatus.Failed    => "#ff5555",
                _                     => "#ffd700"
            };
            string statusLabel = quest.Status switch
            {
                QuestStatus.Completed => "[DONE]",
                QuestStatus.Failed    => "[FAILED]",
                _                     => "[ACTIVE]"
            };
            AddCardLine(card.transform,
                $"<color={statusColor}>{statusLabel}</color> <b>{quest.GiverName}</b>", 11);

            AddCardLine(card.transform, quest.ObjectiveText, 10);

            if (!string.IsNullOrEmpty(quest.CompletionCondition))
                AddCardLine(card.transform, $"<color=#aaaaaa>Condition: {quest.CompletionCondition}</color>", 9);

            if (!string.IsNullOrEmpty(quest.RewardText) || quest.RewardGold > 0)
            {
                string reward = quest.RewardText ?? "";
                if (quest.RewardGold > 0) reward += $" (+{quest.RewardGold}g)";
                AddCardLine(card.transform, $"<color=#aaaaff>Reward: {reward.Trim()}</color>", 9);
            }
        }

        private static void AddCardLine(Transform parent, string text, int fontSize)
        {
            var go = new GameObject("Line", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var txt = go.AddComponent<TextMeshProUGUI>();
            txt.text = text;
            txt.fontSize = fontSize;
            txt.color = Color.white;
            txt.enableWordWrapping = true;
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = fontSize * 1.8f;
            le.flexibleWidth = 1;
        }
    }
}
