using UnityEngine;

namespace KspFlightPlanner
{
    /// <summary>The window chrome the add-ons' windows share: resizing from the bottom-right grip.</summary>
    internal static class PanelWindow
    {
        private static GUIStyle _dark;
        private static Texture2D _darkTexture;

        /// <summary>
        /// A window style with a dark, nearly solid background, so text stays readable over a bright scene (the VAB floor,
        /// the runway). Pass it to GUILayout.Window after GUI.skin is set.
        /// </summary>
        internal static GUIStyle Dark()
        {
            if (_dark != null && _darkTexture != null) return _dark;
            _darkTexture = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            _darkTexture.SetPixel(0, 0, new Color(0.07f, 0.08f, 0.10f, 0.96f));
            _darkTexture.Apply();
            _dark = new GUIStyle(GUI.skin.window);
            _dark.normal.background = _darkTexture;
            _dark.onNormal.background = _darkTexture;
            _dark.normal.textColor = _dark.onNormal.textColor = new Color(0.85f, 0.9f, 1f);
            return _dark;
        }

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
