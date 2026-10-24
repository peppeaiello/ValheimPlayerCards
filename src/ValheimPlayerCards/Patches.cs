using System;
using HarmonyLib;

namespace ValheimPlayerCards
{
    [HarmonyPatch(typeof(ZNet), "Awake")]
    internal static class ZNet_Awake
    {
        private static void Postfix(ZNet __instance)
        {
            try
            {
                if (__instance.IsServer()) ServerSide.Start();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Server start: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    internal static class ZNet_OnNewConnection
    {
        private static void Postfix(ZNet __instance, ZNetPeer __0)
        {
            try
            {
                if (__instance.IsServer()) ServerSide.RegisterPeer(__0);
                else ClientSide.RegisterPeer(__0);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnNewConnection: {e}");
            }
        }
    }

    /// <summary>The player's character is in the world: a session starts (once per connection).</summary>
    [HarmonyPatch(typeof(ZNet), "RPC_CharacterID")]
    internal static class ZNet_RPC_CharacterID
    {
        private static void Postfix(ZNet __instance, ZRpc __0)
        {
            try
            {
                if (!__instance.IsServer()) return;
                foreach (var peer in __instance.GetPeers())
                    if (peer.m_rpc == __0)
                    {
                        PlayerStore.OnPlayerJoined(peer);
                        break;
                    }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"RPC_CharacterID: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
    internal static class ZNet_Disconnect
    {
        private static void Prefix(ZNet __instance, ZNetPeer peer)
        {
            try
            {
                if (__instance.IsServer()) ServerSide.OnPeerLeft(peer);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Disconnect: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), "OnDestroy")]
    internal static class ZNet_OnDestroy
    {
        private static void Prefix()
        {
            ServerSide.Stop();
            ClientSide.Reset();
        }
    }
}
