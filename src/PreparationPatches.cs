using System;
using System.Collections.Generic;
using System.Linq;
using Fusion;
using HarmonyLib;
using UnityEngine;

namespace ADVRMorePlayers;

/// UIPrepareBase.NetworkedPreparationList is a Fusion list with [Capacity(4)] baked into the weaved state
/// layout, so it cannot grow without every peer running a matching build. It drives the "ready" gate and
/// the weapon-pick badges, and only the state authority (host) writes it. So the host keeps players 5+
/// in a local overflow table:
/// - it gates the Start button on them too;
/// - it promotes them into the real list when a slot frees up;
/// - it sends their weapon picks to the other players over ModNet, so modded clients can show their
///   badges. Unmodded clients just show the 4 vanilla badges.
[HarmonyPatch(typeof(UIPrepareBase))]
internal static class PreparationPatches
{
    internal class Entry
    {
        public bool Ready;
        public string Weapon = DefaultWeapon;
    }

    private const string DefaultWeapon = "swordknife";

    // Host: overflow players per panel, and what was last sent for each panel.
    private static readonly Dictionary<UIPrepareBase, Dictionary<PlayerRef, Entry>> Overflow = new();
    private static readonly Dictionary<UIPrepareBase, string> LastSent = new();

    // Clients: overflow picks received from the host, by panel network id.
    private static readonly Dictionary<uint, List<(PlayerRef player, string weapon)>> Received = new();

    internal static void RegisterNet()
    {
        ModNet.On(ModNet.Msg.PrepPicks, (_, r) =>
        {
            uint panelId = r.ReadUInt32();
            int n = r.ReadInt32();
            var picks = new List<(PlayerRef, string)>(n);
            for (int i = 0; i < n; i++) picks.Add((PlayerRef.FromEncoded(r.ReadInt32()), r.ReadString()));
            Received[panelId] = picks;
            foreach (var panel in UnityEngine.Object.FindObjectsByType<UIPrepareBase>(FindObjectsSortMode.None))
                if (panel.Object != null && panel.Object.IsValid && panel.Object.Id.Raw == panelId)
                    panel.GetComponent<PrepWatcher>()?.Redraw();
        });
    }

    private static Dictionary<PlayerRef, Entry> OverflowOf(UIPrepareBase p)
    {
        if (!Overflow.TryGetValue(p, out var d)) Overflow[p] = d = new Dictionary<PlayerRef, Entry>();
        return d;
    }

    private static bool InList(UIPrepareBase p, PlayerRef player)
    {
        foreach (var info in p.NetworkedPreparationList)
            if (info.playerRef == player) return true;
        return false;
    }

    /// Add to the networked list if there is room, otherwise to the host-side overflow table.
    private static void SafeAdd(UIPrepareBase p, PlayerRef player)
    {
        var of = OverflowOf(p);
        if (InList(p, player) || of.ContainsKey(player)) return;
        bool ready = p.networkObject.StateAuthority == player;
        var list = p.NetworkedPreparationList;
        if (list.Count < list.Capacity)
        {
            list.Add(new UIPrepareBase.NetworkPreparationInfo { playerRef = player, weaponId = DefaultWeapon, ready = ready });
            return;
        }
        of[player] = new Entry { Ready = ready };
        Plugin.Log.LogDebug($"Run preparation list full; tracking {player} host-side");
        Publish(p);
    }

    /// Host: send overflow players' weapon picks to the other players if they changed.
    /// `force` resends unchanged data (e.g. to a player who just joined).
    private static void Publish(UIPrepareBase p, bool force = false)
    {
        if (!p.HasStateAuthority || p.Object == null || !p.Object.IsValid) return;
        Overflow.TryGetValue(p, out var of);
        var picks = of?.Select(kv => (kv.Key, kv.Value.Weapon)).ToList() ?? new List<(PlayerRef, string)>();
        string signature = string.Join(";", picks.Select(x => $"{x.Item1.RawEncoded}:{x.Item2}"));
        if (!force && LastSent.TryGetValue(p, out var last) && last == signature) return;
        LastSent[p] = signature;
        uint panelId = p.Object.Id.Raw;
        ModNet.Broadcast(ModNet.Msg.PrepPicks, w =>
        {
            w.Write(panelId);
            w.Write(picks.Count);
            foreach (var (player, weapon) in picks) { w.Write(player.RawEncoded); w.Write(weapon ?? DefaultWeapon); }
        });
    }

    /// Overflow players' weapon picks: the host reads its table, clients read what the host sent.
    internal static IEnumerable<(PlayerRef player, string weapon)> OverflowPicks(UIPrepareBase p)
    {
        if (p.HasStateAuthority)
        {
            if (Overflow.TryGetValue(p, out var of))
                foreach (var kv in of) yield return (kv.Key, kv.Value.Weapon);
            yield break;
        }
        if (p.Object != null && Received.TryGetValue(p.Object.Id.Raw, out var picks))
            foreach (var pick in picks) yield return pick;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(UIPrepareBase.Spawned))]
    private static void Spawned_Prefix(UIPrepareBase __instance)
    {
        Overflow.Remove(__instance);
        LastSent.Remove(__instance);
    }

    // The vanilla Spawned() lambda adds every player and throws "NetworkList is full" at the 5th.
    // That lambda is disabled below; add the players ourselves.
    [HarmonyPostfix, HarmonyPatch(nameof(UIPrepareBase.Spawned))]
    private static void Spawned_Postfix(UIPrepareBase __instance)
    {
        try
        {
            var gi = GameHandler.INSTANCE;
            if (!gi.IsSinglePlayer() && __instance.GetComponent<PrepWatcher>() == null)
                __instance.gameObject.AddComponent<PrepWatcher>().owner = __instance;
            if (gi.GetAllPlayers().Count == 0 || !__instance.HasStateAuthority) return;
            __instance.RunWhenValid(() =>
            {
                foreach (var p in gi.GetAllPlayers()) SafeAdd(__instance, p.spawnedPlayerId);
                __instance.OnNetworkedPreparationChanged();
            });
        }
        catch (Exception e) { Plugin.Log.LogError($"Run preparation setup failed: {e}"); }
    }

    [HarmonyPatch]
    private static class SpawnedLambda
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
            AccessTools.GetDeclaredMethods(typeof(UIPrepareBase))
                .Where(m => m.Name.StartsWith("<Spawned>b__") && m.GetParameters().Length == 0);

        private static bool Prefix() => false;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(UIPrepareBase.GI_AfterPlayerJoined))]
    private static bool GI_AfterPlayerJoined_Prefix(UIPrepareBase __instance, PlayerRef pPlayerRef)
    {
        __instance.RunWhenValid(() =>
        {
            if (!__instance.HasStateAuthority) return;
            SafeAdd(__instance, pPlayerRef);
            // Late joiner: give them the current overflow picks too.
            Publish(__instance, force: true);
            __instance.OnNetworkedPreparationChanged();
        });
        return false;
    }

    // RPC bodies run on the state authority; vanilla ignores senders that aren't in the list.
    [HarmonyPostfix, HarmonyPatch(nameof(UIPrepareBase.RpcMarkAsReady))]
    private static void RpcMarkAsReady_Postfix(UIPrepareBase __instance, RpcInfo info)
    {
        if (!__instance.HasStateAuthority || !OverflowOf(__instance).TryGetValue(info.Source, out var e)) return;
        e.Ready = true;
        __instance.OnNetworkedPreparationChanged();
    }

    [HarmonyPostfix, HarmonyPatch(nameof(UIPrepareBase.RpcUpdateRunInformationAll))]
    private static void RpcUpdateRunInformationAll_Postfix(UIPrepareBase __instance, string pSelectedWeaponId, PlayerRef pSource)
    {
        if (!__instance.HasStateAuthority || !OverflowOf(__instance).TryGetValue(pSource, out var e)) return;
        e.Weapon = pSelectedWeaponId;
        Publish(__instance);
        __instance.OnNetworkedPreparationChanged();
    }

    // After vanilla redraws badges and computes readiness from the 4 listed players:
    // add overflow players' badges, and on the host also require them to be ready.
    [HarmonyPostfix, HarmonyPatch(nameof(UIPrepareBase.OnNetworkedPreparationChanged))]
    private static void OnNetworkedPreparationChanged_Postfix(UIPrepareBase __instance)
    {
        try
        {
            var gi = GameHandler.INSTANCE;
            if (gi.IsSinglePlayer()) return;

            var picks = OverflowPicks(__instance).ToList();
            int drawn = 0;
            if (picks.Count > 0)
            {
                foreach (var button in __instance.weaponSelectionGroup.GetComponentsInChildren<RunPreparationUI_ButtonSelectWeapon>())
                    foreach (var (player, weapon) in picks)
                    {
                        var info = button.weaponCombo != null && button.weaponCombo.id == weapon ? gi.GetPlayerById(player) : null;
                        if (info == null) continue;
                        button.AddPlayerThatSelected(info);
                        drawn++;
                    }
            }
            __instance.GetComponent<PrepWatcher>()?.NoteDrawn(drawn, picks.Count);

            if (!__instance.HasStateAuthority || !__instance._allPlayersReady) return;
            if (gi.debugSettings.PlayerAlwaysReadyInRunPreparation) return;
            if (!Overflow.TryGetValue(__instance, out var of) || of.Values.All(e => e.Ready)) return;
            __instance._allPlayersReady = false;
            __instance.UpdateStartRunButtons(interactable: false, "multiplayer_wait_for_players_to_get_ready");
            __instance.UpdateContinueSavedRunButtons();
        }
        catch (Exception e) { Plugin.Log.LogError($"Run preparation update failed: {e}"); }
    }

    // Someone left: drop departed overflow players, promote waiting ones into freed slots.
    [HarmonyPostfix, HarmonyPatch(nameof(UIPrepareBase.ClearInvalidPreparedPlayers))]
    private static void ClearInvalidPreparedPlayers_Postfix(UIPrepareBase __instance)
    {
        if (!__instance.HasStateAuthority || !Overflow.TryGetValue(__instance, out var of) || of.Count == 0) return;
        var present = new HashSet<PlayerRef>(GameHandler.INSTANCE.GetAllPlayers().Select(p => p.spawnedPlayerId));
        foreach (var gone in of.Keys.Where(k => !present.Contains(k)).ToList()) of.Remove(gone);

        var list = __instance.NetworkedPreparationList;
        foreach (var kv in of.ToList())
        {
            if (list.Count >= list.Capacity) break;
            list.Add(new UIPrepareBase.NetworkPreparationInfo { playerRef = kv.Key, weaponId = kv.Value.Weapon, ready = kv.Value.Ready });
            of.Remove(kv.Key);
        }
        Publish(__instance);
        __instance.OnNetworkedPreparationChanged();
    }

    [HarmonyPrefix, HarmonyPatch(nameof(UIPrepareBase.Despawned))]
    private static void Despawned_Prefix(UIPrepareBase __instance)
    {
        Overflow.Remove(__instance);
        LastSent.Remove(__instance);
        if (__instance.Object != null) Received.Remove(__instance.Object.Id.Raw);
    }
}

/// Clients: if some overflow badges couldn't be drawn yet (their player hadn't spawned locally),
/// retry until they can. Cheap: only polls while something is missing.
internal class PrepWatcher : MonoBehaviour
{
    public UIPrepareBase owner;
    private bool _incomplete;
    private float _next;

    public void NoteDrawn(int drawn, int wanted) => _incomplete = drawn < wanted;

    public void Redraw()
    {
        if (owner != null && owner.Object != null && owner.Object.IsValid) owner.OnNetworkedPreparationChanged();
    }

    private void Update()
    {
        if (!_incomplete || Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 1f;
        Redraw();
    }
}
