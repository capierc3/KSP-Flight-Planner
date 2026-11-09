using UnityEngine;

namespace KspFlightPlanner
{
    /// <summary>The window chrome the add-ons' windows share: resizing from the bottom-right grip.</summary>
    internal static class PanelWindow
    {
        /// <summary>While resizing, the window follows the mouse until the button comes up. Call before GUILayout.Window.</summary>
        internal static void FollowResize(ref Rect window, ref bool resizing)
        {
            if (!resizing) return;
            var e = Event.current;
            if (e.type == EventType.MouseUp) resizing = false;
            else if (e.type == EventType.MouseDrag)
            {
                window.width = Mathf.Clamp(e.mousePosition.x - window.x + 8, 320, Screen.width - window.x);
                window.height = Mathf.Clamp(e.mousePosition.y - window.y + 8, 200, Screen.height - window.y);
                e.Use();
            }
        }

        /// <summary>The resize grip, bottom right, drawn inside the window.</summary>
        internal static void Grip(Rect window, ref bool resizing)
        {
            var grip = new Rect(window.width - 20, window.height - 20, 18, 18);
            GUI.Label(grip, "◢");
            if (Event.current.type == EventType.MouseDown && grip.Contains(Event.current.mousePosition))
            {
                resizing = true;
                Event.current.Use();
            }
        }
    }
}
