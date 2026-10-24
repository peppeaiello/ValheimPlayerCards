using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace ValheimPlayerCards
{
    /// <summary>
    /// Watches what the local player looks like and, when it changes, renders a portrait and
    /// sends it to the server. Nothing is sent to servers that did not answer the hello.
    /// </summary>
    internal static class ClientSide
    {
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> RightItem = Field("m_rightItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> LeftItem = Field("m_leftItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> ChestItem = Field("m_chestItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> LegItem = Field("m_legItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> HelmetItem = Field("m_helmetItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> ShoulderItem = Field("m_shoulderItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> UtilityItem = Field("m_utilityItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> TrinketItem = Field("m_trinketItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> HiddenLeftItem = Field("m_hiddenLeftItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> HiddenRightItem = Field("m_hiddenRightItem");
        private static readonly AccessTools.FieldRef<Humanoid, string> BeardItem = AccessTools.FieldRefAccess<Humanoid, string>("m_beardItem");
        private static readonly AccessTools.FieldRef<Humanoid, string> HairItem = AccessTools.FieldRefAccess<Humanoid, string>("m_hairItem");

        private const float FirstDelay = 8f;   // after spawning, let the game finish loading the character
        private const float Debounce = 3f;     // several equipment changes in a row make one portrait

        private static ZNetPeer _serverPeer;
        private static bool _serverReady;
        private static int _serverMaxBytes;
        private static int _serverMinInterval;
        private static int _serverMaxSide;
        // Item icons the server already stores (from Ready) or that we sent since connecting.
        private static readonly HashSet<string> ServerIcons = new HashSet<string>();

        private static Player _player;
        private static float _playerSeenAt;
        private static string _sentSignature;
        private static string _pendingSignature;
        private static float _pendingSince;
        private static float _lastCapture = -10000f;
        private static float _nextCheck;
        private static bool _busy;
        private static bool _forced;
        private static int _uploadId = Environment.TickCount & 0x3fffffff;

        private static AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> Field(string name)
        {
            return AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>(name);
        }

        // ---- Network -------------------------------------------------------

        public static void RegisterPeer(ZNetPeer peer)
        {
            if (Plugin.IsHeadless) return;
            _serverPeer = peer;
            _serverReady = false;
            peer.m_rpc.Register<ZPackage>(Protocol.RpcReady, OnReady);
            var hello = new ZPackage();
            hello.Write(Protocol.Version);
            peer.m_rpc.Invoke(Protocol.RpcHello, hello);
        }

        public static void Reset()
        {
            _serverPeer = null;
            _serverReady = false;
            _player = null;
            _sentSignature = null;
            _pendingSignature = null;
        }

        private static void OnReady(ZRpc rpc, ZPackage pkg)
        {
            try
            {
                if (_serverPeer == null || rpc != _serverPeer.m_rpc) return;
                if (pkg.ReadInt() != Protocol.Version) return;
                _serverMaxBytes = pkg.ReadInt();
                _serverMinInterval = pkg.ReadInt();
                _serverMaxSide = pkg.ReadInt();
                ServerIcons.Clear();
                int icons = Math.Min(pkg.ReadInt(), Protocol.MaxKnownIcons);
                for (int i = 0; i < icons; i++) ServerIcons.Add(pkg.ReadString());
                _serverReady = true;
                _sentSignature = null;
                Plugin.Log.LogInfo($"Server accepts player cards (up to {_serverMaxBytes / 1024} KB, one every {_serverMinInterval} s).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Bad answer from server: {e.Message}");
            }
        }

        /// <summary>True when portraits can go somewhere: a server that answered, or our own hosted world.</summary>
        private static bool CanSend()
        {
            if (ZNet.instance == null) return false;
            if (ZNet.instance.IsServer()) return PlayerStore.Active;
            return _serverReady && _serverPeer != null && _serverPeer.m_rpc != null;
        }

        private static int Cooldown()
        {
            int server = ZNet.instance != null && ZNet.instance.IsServer() ? Plugin.ServerMinIntervalSeconds.Value : _serverMinInterval;
            return Math.Max(Plugin.MinIntervalSeconds.Value, server + 2);
        }

        private static int ByteLimit()
        {
            int server = ZNet.instance != null && ZNet.instance.IsServer() ? Plugin.MaxUploadKB.Value * 1024 : _serverMaxBytes;
            // Leave room for item icons and the rest of the card.
            return Math.Min(Plugin.TargetKB.Value * 1024, Math.Max(32 * 1024, server - 128 * 1024));
        }

        // ---- Change detection ----------------------------------------------

        public static void ForceNext()
        {
            _forced = true;
            _sentSignature = null;
        }

        public static void Tick()
        {
            if (Plugin.IsHeadless || !Plugin.ClientEnabled.Value || _busy) return;
            float now = Time.time;
            if (now < _nextCheck) return;
            _nextCheck = now + 1f;

            var player = Player.m_localPlayer;
            if (player == null || !CanSend()) return;
            if (player != _player)
            {
                _player = player;
                _playerSeenAt = now;
                _sentSignature = null;
                _pendingSignature = null;
            }
            if (now - _playerSeenAt < FirstDelay || player.IsDead() || player.IsTeleporting()) return;

            string sig;
            try
            {
                sig = Signature(player);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Cannot read equipment: {e.Message}");
                _nextCheck = now + 60f;
                return;
            }
            if (sig == _sentSignature) return;
            if (sig != _pendingSignature)
            {
                _pendingSignature = sig;
                _pendingSince = now;
            }
            if (!_forced && now - _pendingSince < Debounce) return;
            if (!_forced && now - _lastCapture < Cooldown()) return;

            _forced = false;
            _busy = true;
            _lastCapture = now;
            Plugin.Instance.StartCoroutine(CaptureAndSend(player, sig));
        }

        /// <summary>Everything that changes how the character looks. Drawing or sheathing a weapon does not count.</summary>
        private static string Signature(Player p)
        {
            var sb = new StringBuilder();
            foreach (var (slot, item) in Equipped(p))
            {
                if (slot == "right" || slot == "left" || slot == "backRight" || slot == "backLeft") continue;
                sb.Append(slot).Append('=').Append(ItemKey(item)).Append(';');
            }
            var weapons = new List<string>
            {
                ItemKey(RightItem(p)), ItemKey(LeftItem(p)), ItemKey(HiddenRightItem(p)), ItemKey(HiddenLeftItem(p))
            };
            weapons.Sort(StringComparer.Ordinal);
            sb.Append("weapons=").Append(string.Join(",", weapons)).Append(';');
            sb.Append("hair=").Append(HairItem(p)).Append(";beard=").Append(BeardItem(p)).Append(';');
            var zdo = p.GetComponent<ZNetView>()?.GetZDO();
            if (zdo != null)
            {
                sb.Append("model=").Append(zdo.GetInt(ZDOVars.s_modelIndex)).Append(';');
                sb.Append("skin=").Append(zdo.GetVec3(ZDOVars.s_skinColor, Vector3.one)).Append(';');
                sb.Append("haircolor=").Append(zdo.GetVec3(ZDOVars.s_hairColor, Vector3.one)).Append(';');
            }
            return sb.ToString();
        }

        private static string ItemKey(ItemDrop.ItemData item)
        {
            if (item == null) return "";
            string prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared?.m_name;
            return $"{prefab}:{item.m_quality}:{item.m_variant}";
        }

        private static IEnumerable<(string, ItemDrop.ItemData)> Equipped(Player p)
        {
            yield return ("helmet", HelmetItem(p));
            yield return ("chest", ChestItem(p));
            yield return ("legs", LegItem(p));
            yield return ("cape", ShoulderItem(p));
            yield return ("utility", UtilityItem(p));
            yield return ("trinket", TrinketItem(p));
            yield return ("right", RightItem(p));
            yield return ("left", LeftItem(p));
            yield return ("backRight", HiddenRightItem(p));
            yield return ("backLeft", HiddenLeftItem(p));
        }

        // ---- Capture and upload --------------------------------------------

        private static IEnumerator CaptureAndSend(Player player, string sig)
        {
            try
            {
                byte[] png = null;
                yield return PortraitRenderer.Render(player, ByteLimit(), _serverMaxSide > 0 ? _serverMaxSide : Protocol.MaxImageSide, result => png = result);
                // Even when rendering failed, do not try again for this look: wait for the next change.
                _sentSignature = sig;
                if (png == null || player == null || player != Player.m_localPlayer || !CanSend()) yield break;

                var payload = BuildPayload(player, png);
                AddIcons(player, payload);
                byte[] bytes = Protocol.Serialize(payload);
                if (Plugin.SaveLocalCopy.Value) SaveLocal(png);
                Send(player, bytes);
                foreach (var icon in payload.Icons) ServerIcons.Add(icon.Key);
                Plugin.Log.LogInfo($"Card sent ({bytes.Length / 1024} KB: portrait {png.Length / 1024} KB, {payload.Items.Count} items, {payload.Skills.Count} skills, {payload.Stats.Count} stats, {payload.Icons.Count} icons).");
            }
            finally
            {
                _busy = false;
            }
        }

        private static CardPayload BuildPayload(Player p, byte[] png)
        {
            var payload = new CardPayload { Png = png, Hair = HairItem(p) ?? "", Beard = BeardItem(p) ?? "" };
            foreach (var (slot, item) in Equipped(p))
            {
                if (item == null) continue;
                string token = item.m_shared?.m_name ?? "";
                payload.Items.Add(new ItemInfo
                {
                    Slot = slot,
                    Prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : "",
                    Name = Localization.instance != null ? Localization.instance.Localize(token) : token,
                    Token = token,
                    Quality = item.m_quality,
                    Variant = item.m_variant
                });
            }
            var zdo = p.GetComponent<ZNetView>()?.GetZDO();
            if (zdo != null)
            {
                payload.Model = zdo.GetInt(ZDOVars.s_modelIndex);
                Vector3 skin = zdo.GetVec3(ZDOVars.s_skinColor, Vector3.one);
                Vector3 hair = zdo.GetVec3(ZDOVars.s_hairColor, Vector3.one);
                payload.SkinColor = new[] { skin.x, skin.y, skin.z };
                payload.HairColor = new[] { hair.x, hair.y, hair.z };
            }

            if (Plugin.ShareSkills.Value)
            {
                foreach (var skill in p.GetSkills().GetSkillList())
                {
                    if (skill?.m_info == null) continue;
                    string id = skill.m_info.m_skill.ToString();
                    string token = "$skill_" + id.ToLowerInvariant();
                    string name = Localization.instance != null ? Localization.instance.Localize(token) : id;
                    payload.Skills.Add(new SkillInfo { Id = id, Name = name.StartsWith("[") ? id : name, Level = skill.m_level });
                }
            }

            var profile = Game.instance != null ? Game.instance.GetPlayerProfile() : null;
            if (Plugin.ShareStats.Value && profile?.m_playerStats != null && profile.m_playerStats.Length > 0 && profile.m_playerStats[0] != null)
            {
                foreach (var stat in profile.m_playerStats[0].m_stats)
                {
                    if (stat.Value == 0f || stat.Key == PlayerStatType.Count || stat.Key == PlayerStatType.None) continue;
                    payload.Stats.Add(new StatInfo { Id = stat.Key.ToString(), Value = stat.Value });
                    if (payload.Stats.Count >= Protocol.MaxStats) break;
                }
            }
            return payload;
        }

        /// <summary>Game icons of the equipped items the server does not have yet.</summary>
        private static void AddIcons(Player p, CardPayload payload)
        {
            foreach (var (_, item) in Equipped(p))
            {
                if (item == null || item.m_dropPrefab == null || payload.Icons.Count >= Protocol.MaxIcons) continue;
                string key = PlayerStore.IconKey(item.m_dropPrefab.name, item.m_variant);
                if (ServerIcons.Contains(key) || payload.Icons.Any(i => i.Key == key)) continue;
                try
                {
                    byte[] png = IconRenderer.Render(item.GetIcon());
                    if (png != null && png.Length <= Protocol.MaxIconBytes)
                        payload.Icons.Add(new IconInfo { Key = key, Png = png });
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Cannot draw the icon of {item.m_dropPrefab.name}: {e.Message}");
                }
            }
        }

        private static void Send(Player player, byte[] bytes)
        {
            if (ZNet.instance.IsServer())
            {
                ServerSide.Save(player.GetPlayerName(), bytes);
                return;
            }
            int id = _uploadId++;
            int count = Protocol.ChunkCount(bytes.Length);
            for (int i = 0; i < count; i++)
            {
                int offset = i * Protocol.ChunkSize;
                int length = Math.Min(Protocol.ChunkSize, bytes.Length - offset);
                var chunk = new byte[length];
                Buffer.BlockCopy(bytes, offset, chunk, 0, length);
                var pkg = new ZPackage();
                pkg.Write(Protocol.Version);
                pkg.Write(id);
                pkg.Write(i);
                pkg.Write(count);
                pkg.Write(bytes.Length);
                pkg.Write(chunk);
                _serverPeer.m_rpc.Invoke(Protocol.RpcUpload, pkg);
            }
        }

        private static void SaveLocal(byte[] png)
        {
            try
            {
                string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "ValheimPlayerCards");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "last-portrait.png"), png);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Cannot save local copy: {e.Message}");
            }
        }
    }
}
