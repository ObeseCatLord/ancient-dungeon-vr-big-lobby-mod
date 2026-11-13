using System;
using System.Collections.Generic;
using System.IO;
using Fusion;
using Fusion.Sockets;
using HarmonyLib;

namespace ADVRMorePlayers;

/// Mod-to-mod messages over Fusion's reliable-data channel, under a key only this mod uses.
/// Unmodded peers route all reliable data to RunSaveManager.OnNetworkDataReceived, which ignores
/// unknown keys, so they are unaffected. Unlike session (room) properties this has no size or key
/// limits and doesn't invalidate SessionInfo while an update is in flight.
internal static class ModNet
{
    // Arbitrary, distinct from the game's own keys (RunSaveManager).
    private static readonly ReliableKey Key = ReliableKey.FromULongs(0xB16_10BB_1u, 0x0ADC_0DE5u);

    internal enum Msg : byte
    {
        PrepPicks = 1,   // host -> all: overflow players' weapon picks for one prep panel
        PedestalExtras = 2, // host -> all: network ids of a pedestal's options 5-8
    }

    private static readonly Dictionary<Msg, Action<PlayerRef, BinaryReader>> Handlers = new();

    internal static void On(Msg msg, Action<PlayerRef, BinaryReader> handler) => Handlers[msg] = handler;

    internal static NetworkRunner Runner => GameHandler.INSTANCE?.networkHandler?.GetNetworkRunner();

    /// Send to every other player (the local player is skipped; callers handle themselves).
    internal static void Broadcast(Msg msg, Action<BinaryWriter> write)
    {
        var runner = Runner;
        if (runner == null || !runner.IsRunning || runner.IsSinglePlayer) return;
        byte[] data = Encode(msg, write);
        foreach (var p in GameHandler.INSTANCE.GetAllPlayers())
            if (p.spawnedPlayerId != runner.LocalPlayer)
                runner.SendReliableDataToPlayer(p.spawnedPlayerId, Key, data);
    }

    internal static void SendTo(PlayerRef target, Msg msg, Action<BinaryWriter> write)
    {
        var runner = Runner;
        if (runner == null || !runner.IsRunning || runner.IsSinglePlayer || target == runner.LocalPlayer) return;
        runner.SendReliableDataToPlayer(target, Key, Encode(msg, write));
    }

    private static byte[] Encode(Msg msg, Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write((byte)msg);
            write(w);
        }
        return ms.ToArray();
    }

    [HarmonyPatch(typeof(NetworkHandler), "Fusion.INetworkRunnerCallbacks.OnReliableDataReceived")]
    private static class ReceivePatch
    {
        private static bool Prefix(PlayerRef player, ReliableKey key, ArraySegment<byte> data)
        {
            if (!key.Equals(Key)) return true;
            try
            {
                using var r = new BinaryReader(new MemoryStream(data.Array, data.Offset, data.Count));
                var msg = (Msg)r.ReadByte();
                if (Handlers.TryGetValue(msg, out var handler)) handler(player, r);
            }
            catch (Exception e) { Plugin.Log.LogError($"Bad mod message from {player}: {e}"); }
            return false;
        }
    }
}
