using UnityEngine;
using UnityEngine.UI;

namespace AIROG_Settlement
{
    public static class SettlementUIHelper
    {
        // Reference resolution from the user's canvas
        public const float CANVAS_WIDTH = 1024f;
        public const float CANVAS_HEIGHT = 559f;

        public static Vector2 GetAnchors(float x, float y, float w, float h)
        {
            // Convert pixel coordinates (0,0 is top-left in most image editors, but Unity UI is usually bottom-left for anchors)
            // Let's assume input x,y is top-left pixel relative to 1024x559
            float minX = x / CANVAS_WIDTH;
            float maxX = (x + w) / CANVAS_WIDTH;
            float minY = 1f - ((y + h) / CANVAS_HEIGHT);
            float maxY = 1f - (y / CANVAS_HEIGHT);

            return new Vector2(minX, minY); // For AnchorMin
        }

        public static void SetRect(RectTransform rect, float x, float y, float w, float h)
        {
            float minX = x / CANVAS_WIDTH;
            float maxX = (x + w) / CANVAS_WIDTH;
            float minY = 1f - ((y + h) / CANVAS_HEIGHT);
            float maxY = 1f - (y / CANVAS_HEIGHT);

            rect.anchorMin = new Vector2(minX, minY);
            rect.anchorMax = new Vector2(maxX, maxY);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        public static GameObject CreateUIElement(string name, Transform parent, float x, float y, float w, float h, Sprite sprite = null, Color? color = null)
        {
            GameObject obj = new GameObject(name, typeof(RectTransform), typeof(Image));
            obj.transform.SetParent(parent, false);
            
            Image img = obj.GetComponent<Image>();
            if (sprite != null) img.sprite = sprite;
            if (color.HasValue) img.color = color.Value;
            
            SetRect(obj.GetComponent<RectTransform>(), x, y, w, h);
            return obj;
        }

        // Layout Constants based on uploaded_image_1766746158400.png (1024x559)
        public static class Slots
        {
            // Top Tabs (5 squares)
            // Adjusted X and Y to center within the 72x72 frames in SettlementUI.png
            public static Rect TopTab(int index) => new Rect(332 + (index * 72), 35, 68, 68);

            // Left Sidebar (10 rectangles)
            public static Rect LeftSidebarItem(int index) => new Rect(12, 106 + (index * 45), 164, 37);

            // Right Sidebar (5 rectangles)
            public static Rect RightSidebarItem(int index) => new Rect(826, 106 + (index * 45), 164, 42);

            // Center Workspace
            public static Rect CenterWorkspace = new Rect(194, 124, 636, 416);

            // Bottom Bar (10 slots)
            public static Rect BottomBarItem(int index) => new Rect(284 + (index * 46), 508, 44, 44);
        }
    }
}
