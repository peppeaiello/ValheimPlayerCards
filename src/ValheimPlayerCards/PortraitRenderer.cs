using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ValheimPlayerCards
{
    /// <summary>
    /// Builds a visual-only copy of the local player (the same Player prefab the main menu uses for
    /// its character preview), dresses it exactly like the player, lets the animator settle in the
    /// idle pose and renders it with Jotunn's RenderManager on a transparent background.
    /// </summary>
    internal static class PortraitRenderer
    {
        private const int RenderLayer = 31;   // the layer Jotunn's render camera sees
        private const float SettleSeconds = 1.5f;   // cape hanging still
        private const float MaxTextureWait = 5f;    // give up waiting for full-size textures after this
        private const int FastForwardSteps = 40;    // 8 seconds of animation

        private static readonly MethodInfo SetupVisEquipment = AccessTools.Method(typeof(Humanoid), "SetupVisEquipment");
        private static readonly MethodInfo UpdateVisuals = AccessTools.Method(typeof(VisEquipment), "UpdateVisuals");
        private static readonly int StateF = Animator.StringToHash("statef");
        private static readonly int StateI = Animator.StringToHash("statei");
        // The player's animator starts with "wakeup" on: lying on the ground and getting up, as
        // when you spawn. The game turns it off after a second; the copy has nobody to do it.
        // These are the values of a player standing still on the ground.
        private static readonly (string, bool)[] StandingBools =
        {
            ("wakeup", false), ("onGround", true), ("inWater", false), ("encumbered", false), ("flying", false),
            ("crouching", false), ("blocking", false), ("dead", false), ("falling", false),
            ("skating", false), ("skategliding", false), ("slipping", false)
        };
        private static readonly string[] StandingFloats = { "forward_speed", "sideway_speed", "turn_speed", "tilt" };

        public static IEnumerator Render(Player player, int maxBytes, int maxSide, Action<byte[]> done)
        {
            GameObject holder = null;
            byte[] png = null;
            List<Texture2D> textures = null;
            try
            {
                holder = CreateClone(player);
                if (holder == null) yield break;
                textures = RequestFullTextures(holder);
                float start = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - start < SettleSeconds
                       || (Time.realtimeSinceStartup - start < MaxTextureWait && !textures.All(LoadedOrGone)))
                {
                    yield return null;
                    if (holder == null || player == null) yield break;
                }
                try
                {
                    png = Capture(holder, maxBytes, maxSide);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Could not render the portrait: {e}");
                }
            }
            finally
            {
                if (holder != null) Object.Destroy(holder);
                if (textures != null)
                    foreach (var t in textures)
                        if (t != null) t.ClearRequestedMipmapLevel();
                done(png);
            }
        }

        /// <summary>
        /// The game streams textures at the resolution the main camera needs. The render camera is
        /// far away with a narrow lens, so without this the armor comes out blocky. Asks for the
        /// full-size textures of everything the copy wears; released after the picture.
        /// </summary>
        private static List<Texture2D> RequestFullTextures(GameObject root)
        {
            var seen = new HashSet<Texture2D>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    foreach (int id in m.GetTexturePropertyNameIDs())
                        if (m.GetTexture(id) is Texture2D t && t.streamingMipmaps && seen.Add(t))
                            t.requestedMipmapLevel = 0;
                }
            return seen.ToList();
        }

        private static bool LoadedOrGone(Texture2D t) => t == null || t.IsRequestedMipmapLevelLoaded();

        // ---- Clone ---------------------------------------------------------

        private static GameObject CreateClone(Player player)
        {
            GameObject holder = null;
            try
            {
                GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Player") : null;
                if (prefab == null)
                {
                    Plugin.Log.LogWarning("Player prefab not found, no portrait.");
                    return null;
                }

                // Instantiated under an inactive parent, so no Awake runs until the gameplay
                // components (Player, physics, network sync...) are gone.
                holder = new GameObject("PlayerCardPortrait");
                holder.SetActive(false);
                holder.transform.position = new Vector3(0f, 6000f, 0f);
                var clone = Object.Instantiate(prefab, holder.transform, false);
                clone.name = "PlayerCardClone";
                StripComponents(clone);
                SetLayer(clone.transform, RenderLayer);

                ZNetView.m_forceDisableInit = true;
                try
                {
                    holder.SetActive(true);
                }
                finally
                {
                    ZNetView.m_forceDisableInit = false;
                }

                var vis = clone.GetComponent<VisEquipment>();
                if (vis == null)
                {
                    Plugin.Log.LogWarning("Player prefab has no VisEquipment, no portrait.");
                    Object.Destroy(holder);
                    return null;
                }
                SetupVisEquipment.Invoke(player, new object[] { vis, false });
                var zdo = player.GetComponent<ZNetView>()?.GetZDO();
                if (zdo != null)
                {
                    vis.SetModel(zdo.GetInt(ZDOVars.s_modelIndex));
                    vis.SetSkinColor(zdo.GetVec3(ZDOVars.s_skinColor, Vector3.one));
                    vis.SetHairColor(zdo.GetVec3(ZDOVars.s_hairColor, Vector3.one));
                }
                UpdateVisuals.Invoke(vis, null);
                SetLayer(clone.transform, RenderLayer);

                // Play any getting-up animation to its end now, instead of photographing it.
                PoseStanding(clone, player.GetComponentInChildren<Animator>(), FastForwardSteps);
                return holder;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not build the character copy: {e}");
                if (holder != null) Object.Destroy(holder);
                return null;
            }
        }

        /// <summary>Sets the animator to standing still and plays it forward <paramref name="steps"/> x 0.2 s.</summary>
        private static void PoseStanding(GameObject clone, Animator source, int steps)
        {
            var anims = clone.GetComponentsInChildren<Animator>();
            foreach (var anim in anims)
            {
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                anim.updateMode = AnimatorUpdateMode.Normal;
                anim.applyRootMotion = false;
                foreach (var (name, value) in StandingBools)
                    if (HasParameter(anim, Animator.StringToHash(name), AnimatorControllerParameterType.Bool)) anim.SetBool(name, value);
                foreach (var name in StandingFloats)
                    if (HasParameter(anim, Animator.StringToHash(name), AnimatorControllerParameterType.Float)) anim.SetFloat(name, 0f);
                // How the hands hold the weapon: same values the player's animator has.
                if (source != null && HasParameter(source, StateF, AnimatorControllerParameterType.Float) && HasParameter(anim, StateF, AnimatorControllerParameterType.Float))
                    anim.SetFloat(StateF, source.GetFloat(StateF));
                if (source != null && HasParameter(source, StateI, AnimatorControllerParameterType.Int) && HasParameter(anim, StateI, AnimatorControllerParameterType.Int))
                    anim.SetInteger(StateI, source.GetInteger(StateI));
            }
            for (int i = 0; i < steps; i++)
                foreach (var anim in anims)
                    anim.Update(0.2f);
        }

        private static bool HasParameter(Animator anim, int hash, AnimatorControllerParameterType type)
        {
            return anim.parameters.Any(p => p.nameHash == hash && p.type == type);
        }

        private static bool Keep(Component c)
        {
            if (c is Transform || c is Renderer || c is MeshFilter || c is Animator || c is LODGroup
                || c is VisEquipment || c is ZNetView || c is ParticleSystem)
                return true;
            // Capes and other cloth use MagicaCloth: keep it so they hang naturally.
            string ns = c.GetType().Namespace ?? "";
            return ns.StartsWith("MagicaCloth", StringComparison.Ordinal);
        }

        private static void StripComponents(GameObject root)
        {
            // Scripts first, then joints, then everything else (colliders, rigidbodies, audio...).
            Func<Component, int>[] order =
            {
                c => c is MonoBehaviour ? 0 : 99,
                c => c.GetType().Name.Contains("Joint") ? 0 : 99,
                c => 0,
                c => 0
            };
            foreach (var pass in order)
            {
                foreach (var c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || Keep(c) || pass(c) != 0) continue;
                    try
                    {
                        Object.DestroyImmediate(c);
                    }
                    catch (Exception)
                    {
                        // Another component still depends on it; the next pass retries.
                    }
                }
            }
        }

        private static void SetLayer(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            foreach (Transform child in t) SetLayer(child, layer);
        }

        /// <summary>Only the most detailed level of each LOD group is drawn.</summary>
        private static void DisableLowerLods(GameObject root)
        {
            foreach (var group in root.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = group.GetLODs();
                if (lods.Length < 2) continue;
                var best = new HashSet<Renderer>(lods[0].renderers.Where(r => r != null));
                for (int i = 1; i < lods.Length; i++)
                    foreach (var r in lods[i].renderers)
                        if (r != null && !best.Contains(r)) r.enabled = false;
            }
        }

        private static readonly int SkinColor = Shader.PropertyToID("_SkinColor");

        /// <summary>
        /// Hair and beard get their color from a MaterialPropertyBlock, which is not carried over
        /// when the renderer copies the object. Put the color in the material instead.
        /// </summary>
        private static void BakePropertyBlocks(GameObject root)
        {
            var block = new MaterialPropertyBlock();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.HasPropertyBlock()) continue;
                r.GetPropertyBlock(block);
                if (!block.HasColor(SkinColor)) continue;
                Color color = block.GetColor(SkinColor);
                foreach (var m in r.materials)
                    if (m != null && m.HasProperty(SkinColor)) m.SetColor(SkinColor, color);
            }
        }

        // ---- Render --------------------------------------------------------

        private static byte[] Capture(GameObject holder, int maxBytes, int maxSide)
        {
            var clone = holder.transform.GetChild(0).gameObject;
            DisableLowerLods(clone);
            BakePropertyBlocks(clone);
            // Again, in case the animator was not ready yet when the copy was made.
            PoseStanding(clone, Player.m_localPlayer != null ? Player.m_localPlayer.GetComponentInChildren<Animator>() : null, 10);

            int ss = Plugin.Supersampling.Value;
            int width = Mathf.Min(Plugin.Width.Value, maxSide);
            int height = Mathf.Min(Plugin.Height.Value, maxSide);
            var request = new RenderManager.RenderRequest(clone)
            {
                Width = width * ss,
                Height = height * ss,
                Rotation = Quaternion.Euler(Plugin.Pitch.Value, Plugin.Yaw.Value, 0f),
                ParticleSimulationTime = 1f
            };

            Sprite sprite = null;
            using (new PortraitLighting())
            {
                sprite = RenderManager.Instance.Render(request);
            }
            if (sprite == null || sprite.texture == null)
            {
                Plugin.Log.LogWarning("The renderer returned nothing.");
                return null;
            }

            Texture2D big = sprite.texture;
            Color32[] pixels;
            try
            {
                pixels = big.GetPixels32();
            }
            finally
            {
                Object.Destroy(sprite);
                Object.Destroy(big);
            }

            // Scale down from the supersampled image; if the PNG is still too big, shrink further.
            float scale = 1f;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                int w = Mathf.Max(64, Mathf.RoundToInt(width * scale));
                int h = Mathf.Max(64, Mathf.RoundToInt(height * scale));
                byte[] png = Encode(Resample(pixels, width * ss, height * ss, w, h), w, h);
                if (png.Length <= maxBytes) return png;
                Plugin.Log.LogInfo($"Portrait {w}x{h} is {png.Length / 1024} KB, trying smaller.");
                scale *= 0.8f;
            }
            Plugin.Log.LogWarning("Could not make the portrait small enough, not sent.");
            return null;
        }

        private static byte[] Encode(Color32[] pixels, int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                tex.SetPixels32(pixels);
                tex.Apply(false);
                return tex.EncodeToPNG();
            }
            finally
            {
                Object.Destroy(tex);
            }
        }

        /// <summary>Area average with premultiplied alpha, so edges do not get dark fringes.</summary>
        private static Color32[] Resample(Color32[] src, int sw, int sh, int dw, int dh)
        {
            if (sw == dw && sh == dh) return src;
            var dst = new Color32[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                int y0 = y * sh / dh, y1 = Math.Max(y0 + 1, (y + 1) * sh / dh);
                for (int x = 0; x < dw; x++)
                {
                    int x0 = x * sw / dw, x1 = Math.Max(x0 + 1, (x + 1) * sw / dw);
                    long r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        int row = yy * sw;
                        for (int xx = x0; xx < x1; xx++)
                        {
                            var c = src[row + xx];
                            r += c.r * c.a;
                            g += c.g * c.a;
                            b += c.b * c.a;
                            a += c.a;
                            n++;
                        }
                    }
                    dst[y * dw + x] = a == 0
                        ? new Color32(0, 0, 0, 0)
                        : new Color32((byte)(r / a), (byte)(g / a), (byte)(b / a), (byte)(a / n));
                }
            }
            return dst;
        }

        /// <summary>
        /// Same light whatever the time of day or biome: no fog, flat ambient, a key light and a rim
        /// light only on the render layer. The world's own lights are switched off for the one frame
        /// the render takes, then everything is put back.
        /// </summary>
        private sealed class PortraitLighting : IDisposable
        {
            private readonly bool _fog;
            private readonly AmbientMode _ambientMode;
            private readonly Color _ambientLight;
            private readonly float _ambientIntensity;
            private readonly List<Light> _disabled = new List<Light>();
            private readonly List<GameObject> _created = new List<GameObject>();

            public PortraitLighting()
            {
                _fog = RenderSettings.fog;
                _ambientMode = RenderSettings.ambientMode;
                _ambientLight = RenderSettings.ambientLight;
                _ambientIntensity = RenderSettings.ambientIntensity;

                RenderSettings.fog = false;
                RenderSettings.ambientMode = AmbientMode.Flat;
                float a = Plugin.Ambient.Value;
                RenderSettings.ambientLight = new Color(a, a, a, 1f);
                RenderSettings.ambientIntensity = 1f;

                const int mask = 1 << RenderLayer;
                foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                {
                    // Jotunn's own render light only sees the render layer: leave it on.
                    if (light.enabled && (light.cullingMask & mask) != 0 && light.cullingMask != mask)
                    {
                        light.enabled = false;
                        _disabled.Add(light);
                    }
                }

                // The render camera looks along -Z; the character faces it.
                AddLight(Quaternion.Euler(35f, 150f, 0f), Plugin.KeyLight.Value);
                AddLight(Quaternion.Euler(20f, -20f, 0f), Plugin.RimLight.Value);
            }

            private void AddLight(Quaternion rotation, float intensity)
            {
                if (intensity <= 0f) return;
                var go = new GameObject("PlayerCardLight");
                go.transform.rotation = rotation;
                var light = go.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = intensity;
                light.shadows = LightShadows.None;
                light.cullingMask = 1 << RenderLayer;
                _created.Add(go);
            }

            public void Dispose()
            {
                foreach (var go in _created) Object.DestroyImmediate(go);
                foreach (var light in _disabled)
                    if (light != null) light.enabled = true;
                RenderSettings.fog = _fog;
                RenderSettings.ambientMode = _ambientMode;
                RenderSettings.ambientLight = _ambientLight;
                RenderSettings.ambientIntensity = _ambientIntensity;
            }
        }
    }
}
