using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValheimPlayerCards
{
    /// <summary>
    /// Receives player cards. The player name always comes from the connection
    /// (ZNetPeer.m_playerName), never from the upload, so nobody can overwrite someone else's card.
    /// </summary>
    internal static class ServerSide
    {
        private sealed class Upload
        {
            public int Id;
            public int Count;
            public int Next;
            public byte[] Buffer;
            public int Written;
            public float Started;
        }

        // peer uid -> upload in progress
        private static readonly Dictionary<long, Upload> Uploads = new Dictionary<long, Upload>();
        // player id -> last accepted start, survives reconnects
        private static readonly Dictionary<string, float> LastStart = new Dictionary<string, float>();

        private static WebServer _web;

        private static int MaxBytes => Plugin.MaxUploadKB.Value * 1024;

        // ---- Lifecycle -----------------------------------------------------

        public static void Start()
        {
            if (!Plugin.ServerEnabled.Value) return;
            try
            {
                PlayerStore.Start();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Cannot open the player cards folder: {e}");
                return;
            }
            if (Plugin.WebEnabled.Value)
            {
                _web = new WebServer();
                _web.Start(Plugin.WebHost.Value, Plugin.WebPort.Value);
            }
        }

        public static void Stop()
        {
            _web?.Stop();
            _web = null;
            PlayerStore.Stop();
            Uploads.Clear();
            LastStart.Clear();
        }

        // ---- Network -------------------------------------------------------

        public static void RegisterPeer(ZNetPeer peer)
        {
            peer.m_rpc.Register<ZPackage>(Protocol.RpcHello, OnHello);
            peer.m_rpc.Register<ZPackage>(Protocol.RpcUpload, OnUpload);
        }

        public static void OnPeerLeft(ZNetPeer peer)
        {
            if (peer == null) return;
            Uploads.Remove(peer.m_uid);
            PlayerStore.OnPeerLeft(peer);
        }

        private static ZNetPeer FindPeer(ZRpc rpc)
        {
            return ZNet.instance?.GetPeers().FirstOrDefault(p => p.m_rpc == rpc);
        }

        private static void OnHello(ZRpc rpc, ZPackage pkg)
        {
            try
            {
                if (!PlayerStore.Active) return;
                int version = pkg.ReadInt();
                var peer = FindPeer(rpc);
                string who = peer != null && !string.IsNullOrEmpty(peer.m_playerName) ? peer.m_playerName : "a player";
                if (version != Protocol.Version)
                {
                    Plugin.Log.LogWarning($"{who} uses ValheimPlayerCards protocol {version}, this server uses {Protocol.Version}.");
                    return;
                }
                var ready = new ZPackage();
                ready.Write(Protocol.Version);
                ready.Write(MaxBytes);
                ready.Write(Plugin.ServerMinIntervalSeconds.Value);
                ready.Write(Protocol.MaxImageSide);
                var icons = PlayerStore.KnownIcons.Take(Protocol.MaxKnownIcons).ToList();
                ready.Write(icons.Count);
                foreach (string key in icons) ready.Write(key);
                rpc.Invoke(Protocol.RpcReady, ready);
                Plugin.Log.LogInfo($"{who} has ValheimPlayerCards.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Bad hello: {e.Message}");
            }
        }

        private static void OnUpload(ZRpc rpc, ZPackage pkg)
        {
            var peer = FindPeer(rpc);
            if (peer == null || !PlayerStore.Active) return;
            try
            {
                if (pkg.ReadInt() != Protocol.Version) return;
                int id = pkg.ReadInt();
                int index = pkg.ReadInt();
                int count = pkg.ReadInt();
                int total = pkg.ReadInt();
                byte[] data = pkg.ReadByteArray();

                if (index == 0)
                {
                    Uploads.Remove(peer.m_uid);
                    if (string.IsNullOrEmpty(peer.m_playerName))
                    {
                        Plugin.Log.LogWarning("Card from a connection without a player name, ignored.");
                        return;
                    }
                    if (total <= 0 || total > MaxBytes)
                    {
                        Plugin.Log.LogWarning($"Card from {peer.m_playerName} is {total / 1024} KB, limit is {Plugin.MaxUploadKB.Value} KB. Ignored.");
                        return;
                    }
                    if (count != Protocol.ChunkCount(total)) return;
                    string key = PlayerStore.SafeName(peer.m_playerName);
                    float now = Time.realtimeSinceStartup;
                    if (LastStart.TryGetValue(key, out float lastStart) && now - lastStart < Plugin.ServerMinIntervalSeconds.Value)
                    {
                        Plugin.Log.LogInfo($"Card from {peer.m_playerName} came too soon after the previous one, ignored.");
                        return;
                    }
                    LastStart[key] = now;
                    Uploads[peer.m_uid] = new Upload { Id = id, Count = count, Buffer = new byte[total], Started = now };
                }

                if (!Uploads.TryGetValue(peer.m_uid, out var up) || up.Id != id) return;
                bool last = index == up.Count - 1;
                int expected = last ? up.Buffer.Length - up.Written : Protocol.ChunkSize;
                if (index != up.Next || data.Length != expected || Time.realtimeSinceStartup - up.Started > 120f)
                {
                    Uploads.Remove(peer.m_uid);
                    Plugin.Log.LogWarning($"Card from {peer.m_playerName} arrived broken, ignored.");
                    return;
                }
                Buffer.BlockCopy(data, 0, up.Buffer, up.Written, data.Length);
                up.Written += data.Length;
                up.Next++;
                if (!last) return;

                Uploads.Remove(peer.m_uid);
                Save(peer.m_playerName, up.Buffer);
            }
            catch (Exception e)
            {
                Uploads.Remove(peer.m_uid);
                Plugin.Log.LogWarning($"Bad card from {peer.m_playerName}: {e.Message}");
            }
        }

        /// <summary>Also used directly when the player hosts the world from the game.</summary>
        public static void Save(string playerName, byte[] payload)
        {
            if (!PlayerStore.Active) return;
            CardPayload p;
            try
            {
                p = Protocol.Deserialize(payload);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Card from {playerName} could not be read: {e.Message}");
                return;
            }
            if (!Protocol.TryReadPngSize(p.Png, out int w, out int h) || w > Protocol.MaxImageSide || h > Protocol.MaxImageSide)
            {
                Plugin.Log.LogWarning($"Portrait from {playerName} is not a valid PNG, ignored.");
                return;
            }
            PlayerStore.SaveCard(playerName, p, w, h);
        }
    }
}
