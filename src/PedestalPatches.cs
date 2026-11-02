using System.Collections.Generic;
using Fusion;
using HarmonyLib;
using UnityEngine;

namespace ADVRMorePlayers;

/// Item layouts for "one per player" spots only have positions for up to 4 items; items 5+ all land on
/// the centre. Lay 5-8 items out evenly on a ring instead. Host-side (the host spawns these items).
[HarmonyPatch]
internal static class RingLayoutPatch
{
    private const float Radius = 0.5f;

    [HarmonyPostfix, HarmonyPatch(typeof(SpawnFoodAfterBoss), nameof(SpawnFoodAfterBoss.CalculatePositionOffset))]
    private static void Food(int index, int totalItems, ref Vector3 __result) => Ring(index, totalItems, ref __result);

    [HarmonyPostfix, HarmonyPatch(typeof(PodestChooseOneForEveryPlayer), nameof(PodestChooseOneForEveryPlayer.CalculatePositionOffset))]
    private static void Pedestal(int index, int totalItems, ref Vector3 __result) => Ring(index, totalItems, ref __result);

    private static void Ring(int index, int totalItems, ref Vector3 result)
    {
        if (totalItems <= Plugin.VanillaMax) return;
        float a = 2f * Mathf.PI * index / totalItems;
        result = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * Radius;
    }
}

/// "Choose one for every player" pedestals hold their options in 4 networked fields, so vanilla never
/// offers more than 4. The host spawns options 5-8 as ordinary networked items. Every client (and the
/// host) tracks them so taking any option greys out the rest, as vanilla does for the first 4.
/// Unmodded clients see the extra options too, but only the first 4 grey out for them.
[HarmonyPatch(typeof(PodestChooseOneForEveryPlayer))]
internal static class PedestalExtrasPatch
{
    [HarmonyPostfix, HarmonyPatch(nameof(PodestChooseOneForEveryPlayer.Spawned))]
    private static void Spawned_Postfix(PodestChooseOneForEveryPlayer __instance)
    {
        var tracker = __instance.gameObject.GetComponent<PedestalExtras>() ?? __instance.gameObject.AddComponent<PedestalExtras>();
        tracker.Init(__instance);
        if (__instance.HasStateAuthority) SpawnExtras(__instance, tracker);
    }

    [HarmonyPostfix, HarmonyPatch(nameof(PodestChooseOneForEveryPlayer.MakeAllUnobtainable))]
    private static void MakeAllUnobtainable_Postfix(PodestChooseOneForEveryPlayer __instance) =>
        __instance.GetComponent<PedestalExtras>()?.MakeUnobtainable();

    private static void SpawnExtras(PodestChooseOneForEveryPlayer p, PedestalExtras tracker)
    {
        var gi = GameHandler.INSTANCE;
        int count = gi.GetAllPlayers().Count;
        if (count <= Plugin.VanillaMax) return;

        // Same drop table choice as vanilla Spawned().
        Vector3 pos = p.transform.position;
        string tableName = "podest";
        var room = WorldGeneratorEvolved.GetGeneratorAtRealPos(pos)?.GetRoomAtRealPos(pos);
        if (room != null) tableName = room.type == "secret" ? "secret" : room.type == "afterboss_start" ? "boss" : "podest";
        var table = gi.dropTableHandler.GetDropTableByName(tableName);
        if (table == null) return;

        int hash = Util.GetHashFromPosition(pos);
        for (int i = Plugin.VanillaMax; i < count; i++)
        {
            // Vanilla seeds options 1-4 with hash +1, -1, +2, -2. Continue: +3, -3, +4, -4.
            int offset = (i / 2 + 1) * (i % 2 == 0 ? 1 : -1);
            ItemBase drop = table.GetDrop(hash + offset);
            if (drop == null) continue;
            var go = gi.QueueInstantiate(drop.gameObject,
                pos + PodestChooseOneForEveryPlayer.CalculatePositionOffset(i, count) + Vector3.up * 1.1f,
                Quaternion.identity, DestroyOnHostMigration.OnBeforeSpawned(drop.gameObject, networked: true), registerNetwork: true);
            if (go == null) continue;
            go.transform.SetParent(p.center, worldPositionStays: true);
            tracker.Track(go.GetComponent<NetworkObject>());
        }
    }
}

internal class PedestalExtras : MonoBehaviour
{
    private const float DiscoverySeconds = 20f;

    private PodestChooseOneForEveryPlayer _owner;
    private readonly List<NetworkObject> _extras = new();
    private float _discoverUntil, _nextScan;
    private bool _unobtainable;

    public void Init(PodestChooseOneForEveryPlayer owner)
    {
        _owner = owner;
        _discoverUntil = Time.time + DiscoverySeconds;
    }

    public void Track(NetworkObject no)
    {
        if (no == null || _extras.Contains(no)) return;
        _extras.Add(no);
        var interact = no.GetComponent<BaseInteract>();
        if (interact != null) _owner.RegisterPickupEvents(interact);
        if (_unobtainable) SetTransparent(no);
    }

    public void MakeUnobtainable()
    {
        _unobtainable = true;
        foreach (var no in _extras) SetTransparent(no);
    }

    private static void SetTransparent(NetworkObject no)
    {
        if (no == null) return;
        var upgrade = no.GetComponent<ItemUpgrade>();
        if (upgrade != null) upgrade.isGrabbable = upgrade.isUsable = false;
        no.GetComponentInChildren<ItemCreator>()?.SetTransparent(transparent: true, 0.5f);
    }

    // Clients only see the extra options as networked items near the pedestal; find them there.
    private void Update()
    {
        if (_owner == null || _owner.Object == null || _owner.HasStateAuthority) return;
        if (Time.time > _discoverUntil || Time.time < _nextScan) return;
        _nextScan = Time.time + 0.5f;

        int expected = GameHandler.INSTANCE.GetAllPlayers().Count - Plugin.VanillaMax;
        if (expected <= 0 || _extras.Count >= expected) return;

        var center = _owner.transform.position + Vector3.up * 1.1f;
        foreach (var col in Physics.OverlapSphere(center, 0.8f, ~0, QueryTriggerInteraction.Collide))
        {
            var no = col.GetComponentInParent<NetworkObject>();
            if (no == null || _extras.Contains(no) || no.GetComponent<ItemUpgrade>() == null) continue;
            if (no == _owner.Object1 || no == _owner.Object2 || no == _owner.Object3 || no == _owner.Object4) continue;
            Track(no);
        }
    }
}
