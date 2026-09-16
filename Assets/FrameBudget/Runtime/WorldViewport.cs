using UnityEngine;

namespace FrameBudget
{
    /// <summary>
    /// Keeps the world and the instrument panel in separate screen regions. The HUD reserves a column
    /// of pixels on the left; the camera's viewport is confined to everything right of it and its
    /// orthographic size is refitted so the whole world square stays visible whatever the aspect of
    /// that region. With no column reserved (HUD hidden, batch mode) the camera covers the screen.
    /// </summary>
    public sealed class WorldViewport
    {
        private const float Margin = 1.02f;

        private float lastLeftPixels = -1f;
        private float lastHalfExtent = -1f;
        private int lastWidth;
        private int lastHeight;

        public void Fit(Camera camera, float leftPixels, float worldHalfExtent)
        {
            if (camera == null) return;
            int width = Screen.width;
            int height = Screen.height;
            if (leftPixels == lastLeftPixels && worldHalfExtent == lastHalfExtent && width == lastWidth && height == lastHeight) return;
            lastLeftPixels = leftPixels;
            lastHalfExtent = worldHalfExtent;
            lastWidth = width;
            lastHeight = height;

            float left = width > 0 ? Mathf.Clamp(leftPixels / width, 0f, 0.9f) : 0f;
            camera.rect = new Rect(left, 0f, 1f - left, 1f);

            // Orthographic size is the visible half-height; the visible half-width is that times the
            // viewport aspect. Fit the square by whichever dimension is tighter.
            float aspect = height > 0 ? ((1f - left) * width) / height : 1f;
            camera.orthographicSize = aspect >= 1f ? worldHalfExtent * Margin : worldHalfExtent * Margin / aspect;
        }
    }
}
