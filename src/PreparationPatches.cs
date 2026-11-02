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
/// - it publishes their weapon picks in a session (Photon room) property, so modded clients can show
///   their badges. Unmodded clients just show the 4 vanilla badges.
[HarmonyPatch(typeof(UIPrepareBase))]
internal static class PreparationPatches
{
    internal class Entry
    {
        public bool Ready;
        public string Weapon = DefaultWeapon;
    }

    private const string DefaultWeapon = "swordknife";

    /// Session property carrying overflow weapon picks for every prep panel:
    /// "panelId=player:weapon,player:weapon/panelId=...". Fusion only lets the host update keys that
    /// existed when the room was created, so RegisterSessionKeyPatch adds it at creation time.
    internal const string SessionKey = "mpPrep";

    private static readonly Dictionary<UIPrepareBase, Dictionary<PlayerRef, Entry>> Overflow = new();
    private static string _lastPublished = "";

    private static Dictionary<PlayerRef, Entry> OverflowOf(UIPrepareBase p)
    {
        if (!Overflow.TryGetValue(p, out var d)) Overflow[p] = d = new Dictionary<PlayerRef, Entry>();
        return d;
    }

    internal static string PanelId(UIPrepareBase p) => p.Object.Id.Raw.ToString();

    private static bool InList(UIPrepareBase p, PlayerRef player)
    {
        foreach (var info in p.NetworkedPreparationList)
            if (info.playerRef == player) return true;
        return false;
    }

    /// Add to the networked list if there is room, otherwise to the host-side overflow table.
    private static void SafeAdd(UIPrepareBase p, PlayerRef player)
    {
        if (InList(p, player)) return;
        bool ready = p.networkObject.StateAuthority == player;
        var list = p.NetworkedPreparationList;
        if (list.Count < list.Capacity)
        {
            list.Add(new UIPrepareBase.NetworkPreparationInfo { playerRef = player, weaponId = DefaultWeapon, ready = ready });
            return;
        }
        var of = OverflowOf(p);
        if (!of.ContainsKey(player)) of[player] = new Entry { Ready = ready };
        Plugin.Log.LogInfo($"Run preparation list full; tracking {player} host-side");
        Publish(p);
    }

    /// Host: write overflow players' weapon picks to the session so modded clients can draw them.
    private static void Publish(UIPrepareBase p)
    {
        if (!p.HasStateAuthority) return;
        var runner = p.Runner;
        if (runner == null || runner.IsSinglePlayer || runner.SessionInfo == null) return;
        string value = string.Join("/", Overflow
            .Where(kv => kv.Key != null && kv.Key.Object != null && kv.Value.Count > 0)
            .Select(kv => PanelId(kv.Key) + "=" + string.Join(",", kv.Value.Select(e => $"{e.Key.RawEncoded}:{e.Value.Weapon}"))));
        if (value == _lastPublished) return;
        if (runner.SessionInfo.UpdateCustomProperties(new Dictionary<string, SessionProperty> { { SessionKey, value } }))
            _lastPublished = value;
        else
            Plugin.Log.LogWarning("Couldn't publish weapon picks for players 5+ (room created without the mod, or too much data)");
    }

    /// Overflow players' weapon picks: the host reads its table, clients read the session property.
    internal static IEnumerable<(PlayerRef player, string weapon)> OverflowPicks(UIPrepareBase p)
    {
        if (p.HasStateAuthority)
        {
            if (Overflow.TryGetValue(p, out var of))
                foreach (var kv in of) yield return (kv.Key, kv.Value.Weapon);
            yield break;
        }
        string panel = PanelId(p) + "=";
        string mine = PrepWatcher.Read().Split('/').FirstOrDefault(x => x.StartsWith(panel));
        if (mine == null) yield break;
        foreach (var part in mine.Substring(panel.Length).Split(','))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0 || !int.TryParse(part.Substring(0, colon), out int raw)) continue;
            yield return (PlayerRef.FromEncoded(raw), part.Substring(colon + 1));
        }
    }

    [HarmonyPrefix, HarmonyPatch(nameof(UIPrepareBase.Spawned))]
    private static void Spawned_Prefix(UIPrepareBase __instance) => Overflow.Remove(__instance);

    // The vanilla Spawned() lambda adds every player and throws "NetworkList is full" at the 5th.
    // That lambda is disabled below; add the players ourselves.
    [HarmonyPostfix, HarmonyPatch(nameof(UIPrepareBase.Spawned))]
    private static void Spawned_Postfix(UIPrepareBase __instance)
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
        var gi = GameHandler.INSTANCE;
        if (gi.IsSinglePlayer()) return;

        var picks = OverflowPicks(__instance).ToList();
        if (picks.Count > 0)
        {
            foreach (var button in __instance.weaponSelectionGroup.GetComponentsInChildren<RunPreparationUI_ButtonSelectWeapon>())
                foreach (var (player, weapon) in picks)
                {
                    var info = button.weaponCombo != null && button.weaponCombo.id == weapon ? gi.GetPlayerById(player) : null;
                    if (info != null) button.AddPlayerThatSelected(info);
                }
        }

        if (!__instance.HasStateAuthority || !__instance._allPlayersReady) return;
        if (gi.debugSettings.PlayerAlwaysReadyInRunPreparation) return;
        if (!Overflow.TryGetValue(__instance, out var of) || of.Values.All(e => e.Ready)) return;
        __instance._allPlayersReady = false;
        __instance.UpdateStartRunButtons(interactable: false, "multiplayer_wait_for_players_to_get_ready");
        __instance.UpdateContinueSavedRunButtons();
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
        if (!Overflow.Remove(__instance)) return;
        try { Publish(__instance); }
        catch (System.Exception e) { Plugin.Log.LogDebug($"Clearing published picks on despawn failed: {e.Message}"); }
    }
}

/// Add the side-channel key when the game creates or joins a room. Joining an existing room keeps that
/// room's properties, so this only matters when the modded player creates it.
[HarmonyPatch(typeof(NetworkRunner), nameof(NetworkRunner.StartGame))]
internal static class RegisterSessionKeyPatch
{
    private static void Prefix(StartGameArgs args)
    {
        var props = args.SessionProperties;
        if (props != null && props.ContainsKey("allowMods") && !props.ContainsKey(PreparationPatches.SessionKey))
            props[PreparationPatches.SessionKey] = "";
    }
}

/// Clients: redraw the weapon badges when the host's published overflow picks change.
internal class PrepWatcher : MonoBehaviour
{
    public UIPrepareBase owner;
    private string _last = "";
    private float _next;

    internal static string Read()
    {
        var props = GameHandler.INSTANCE?.networkHandler?.GetNetworkRunner()?.SessionInfo?.Properties;
        return props != null && props.TryGetValue(PreparationPatches.SessionKey, out var v) && v != null && v.IsString
            ? (string)v : "";
    }

    private void Update()
    {
        if (owner == null || owner.Object == null || !owner.Object.IsValid || owner.HasStateAuthority) return;
        if (Time.time < _next) return;
        _next = Time.time + 0.5f;
        string now = Read();
        if (now == _last) return;
        _last = now;
        owner.OnNetworkedPreparationChanged();
    }
}
