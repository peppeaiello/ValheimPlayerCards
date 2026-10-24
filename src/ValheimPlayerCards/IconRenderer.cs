using UnityEngine;

namespace ValheimPlayerCards
{
    /// <summary>
    /// Copies a game icon (a sprite inside a texture atlas the CPU cannot read) to a small PNG by
    /// drawing it on a render texture and reading the pixels back.
    /// </summary>
    internal static class IconRenderer
    {
        private const int Size = 64;

        public static byte[] Render(Sprite sprite)
        {
            if (sprite == null || sprite.texture == null) return null;
            Texture tex = sprite.texture;
            Rect r = sprite.textureRect;
            var source = new Rect(r.x / tex.width, r.y / tex.height, r.width / tex.width, r.height / tex.height);

            // Keep the aspect ratio, centered.
            float aspect = r.width / r.height;
            Rect target = aspect >= 1f
                ? new Rect(0f, (Size - Size / aspect) / 2f, Size, Size / aspect)
                : new Rect((Size - Size * aspect) / 2f, 0f, Size * aspect, Size);

            var rt = RenderTexture.GetTemporary(Size, Size, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            Texture2D result = null;
            try
            {
                RenderTexture.active = rt;
                GL.Clear(true, true, Color.clear);
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, Size, Size, 0f);
                Graphics.DrawTexture(target, tex, source, 0, 0, 0, 0);
                GL.PopMatrix();

                result = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
                result.ReadPixels(new Rect(0f, 0f, Size, Size), 0, 0);
                result.Apply(false);
                return result.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                if (result != null) Object.Destroy(result);
            }
        }
    }
}
