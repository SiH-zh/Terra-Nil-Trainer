using System;
using UnityEngine;

namespace TerraNilTrainer
{
    [Flags]
    public enum PanelEdge { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    public static class PanelResize
    {
        public const float MinimumWidth = 440f, MinimumHeight = 320f;
        public static Rect Clamp(Rect panel, Vector2 canvas)
        {
            float width = Math.Max(1f, canvas.x), height = Math.Max(1f, canvas.y);
            panel.width = Mathf.Clamp(panel.width, Math.Min(MinimumWidth, width), width);
            panel.height = Mathf.Clamp(panel.height, Math.Min(MinimumHeight, height), height);
            panel.x = Mathf.Clamp(panel.x, 0, width - panel.width);
            panel.y = Mathf.Clamp(panel.y, 0, height - panel.height);
            return panel;
        }
        public static PanelEdge Hit(Rect panel, Vector2 point)
        {
            if (!panel.Contains(point)) return PanelEdge.None;
            float left = point.x - panel.xMin, right = panel.xMax - point.x;
            float top = point.y - panel.yMin, bottom = panel.yMax - point.y;
            // 边框8像素；四角18像素，方便同时调节宽高。
            bool corner = Math.Min(left, right) <= 18f && Math.Min(top, bottom) <= 18f;
            float border = corner ? 18f : 8f;
            PanelEdge result = PanelEdge.None;
            if (left <= border) result |= PanelEdge.Left;
            else if (right <= border) result |= PanelEdge.Right;
            if (top <= border) result |= PanelEdge.Top;
            else if (bottom <= border) result |= PanelEdge.Bottom;
            return result;
        }
        public static Rect Drag(Rect start, Vector2 delta, PanelEdge edge, Vector2 canvas)
        {
            start = Clamp(start, canvas);
            float left = start.xMin, right = start.xMax, top = start.yMin, bottom = start.yMax;
            float minWidth = Math.Min(MinimumWidth, canvas.x), minHeight = Math.Min(MinimumHeight, canvas.y);
            if ((edge & PanelEdge.Left) != 0) left = Mathf.Clamp(left + delta.x, 0, right - minWidth);
            if ((edge & PanelEdge.Right) != 0) right = Mathf.Clamp(right + delta.x, left + minWidth, canvas.x);
            if ((edge & PanelEdge.Top) != 0) top = Mathf.Clamp(top + delta.y, 0, bottom - minHeight);
            if ((edge & PanelEdge.Bottom) != 0) bottom = Mathf.Clamp(bottom + delta.y, top + minHeight, canvas.y);
            return Rect.MinMaxRect(left, top, right, bottom);
        }
    }
}
