using System;
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
/// offers more than 4. The host spawns options 5-8 as ordinary networked items and tells the other
/// players their network ids over ModNet. Each player then tracks them like the first 4: taking any
/// option greys out the rest for that player (the lockout is per player, as in vanilla).
/// Unmodded clients see the extra options too, but only the first 4 grey out for them.
[HarmonyPatch(typeof(PodestChooseOneForEveryPlayer))]
internal static class PedestalExtrasPatch
{
    // Announcements that arrived before the pedestal itself spawned locally, by pedestal id.
    internal static readonly Dictionary<uint, uint[]> Pending = new();

    internal static void RegisterNet()
    {
        ModNet.On(ModNet.Msg.PedestalExtras, (_, r) =>
        {
            uint pedestalId = r.ReadUInt32();
            int n = r.ReadInt32();
            var ids = new uint[n];
            for (int i = 0; i < n; i++) ids[i] = r.ReadUInt32();

            var runner = ModNet.Runner;
            if (runner != null && runner.TryFindObject(new NetworkId { Raw = pedestalId }, out var obj) && obj != null
                && obj.TryGetComponent<PedestalExtras>(out var tracker))
                tracker.Expect(ids);
            else
                Pending[pedestalId] = ids;
        });
    }

    [HarmonyPostfix, HarmonyPatch(nameof(PodestChooseOneForEveryPlayer.Spawned))]
    private static void Spawned_Postfix(PodestChooseOneForEveryPlayer __instance)
    {
        try
        {
            var tracker = __instance.gameObject.GetComponent<PedestalExtras>() ?? __instance.gameObject.AddComponent<PedestalExtras>();
            tracker.Init(__instance);
            if (__instance.HasStateAuthority)
            {
                SpawnExtras(__instance, tracker);
                tracker.Announce(null);
            }
            else if (__instance.Object != null && Pending.TryGetValue(__instance.Object.Id.Raw, out var ids))
            {
                Pending.Remove(__instance.Object.Id.Raw);
                tracker.Expect(ids);
            }
        }
        catch (Exception e) { Plugin.Log.LogError($"Pedestal extras failed: {e}"); }
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
    private const float ResolveTimeout = 60f;

    private PodestChooseOneForEveryPlayer _owner;
    private readonly List<NetworkObject> _extras = new();
    private readonly List<uint> _unresolved = new();
    private float _resolveUntil, _nextTry;
    private bool _unobtainable, _subscribed;

    public void Init(PodestChooseOneForEveryPlayer owner)
    {
        _owner = owner;
        enabled = false;
        if (owner.HasStateAuthority && !_subscribed)
        {
            GameHandler.INSTANCE.AfterPlayerJoined += OnPlayerJoined;
            _subscribed = true;
        }
    }

    private void OnDestroy()
    {
        if (_subscribed && GameHandler.INSTANCE != null) GameHandler.INSTANCE.AfterPlayerJoined -= OnPlayerJoined;
    }

    private void OnPlayerJoined(PlayerRef player) => Announce(player);

    /// Host: tell everyone (target == null) or one late joiner which items are this pedestal's extras.
    public void Announce(PlayerRef? target)
    {
        if (_owner == null || _owner.Object == null || _extras.Count == 0) return;
        uint pedestalId = _owner.Object.Id.Raw;
        var ids = new List<uint>();
        foreach (var no in _extras) if (no != null && no.IsValid) ids.Add(no.Id.Raw);
        if (ids.Count == 0) return;
        Action<System.IO.BinaryWriter> write = w =>
        {
            w.Write(pedestalId);
            w.Write(ids.Count);
            foreach (var id in ids) w.Write(id);
        };
        if (target.HasValue) ModNet.SendTo(target.Value, ModNet.Msg.PedestalExtras, write);
        else ModNet.Broadcast(ModNet.Msg.PedestalExtras, write);
    }

    /// Client: the host told us these are the extras; resolve them as they spawn locally.
    public void Expect(uint[] ids)
    {
        _unresolved.Clear();
        _unresolved.AddRange(ids);
        _resolveUntil = Time.unscaledTime + ResolveTimeout;
        enabled = true;
        TryResolve();
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

    private void TryResolve()
    {
        var runner = ModNet.Runner;
        if (runner == null) return;
        for (int i = _unresolved.Count - 1; i >= 0; i--)
        {
            if (runner.TryFindObject(new NetworkId { Raw = _unresolved[i] }, out var no) && no != null)
            {
                Track(no);
                _unresolved.RemoveAt(i);
            }
        }
        if (_unresolved.Count == 0) enabled = false;
    }

    // Only enabled while some announced extras haven't spawned locally yet.
    private void Update()
    {
        if (Time.unscaledTime > _resolveUntil) { enabled = false; return; }
        if (Time.unscaledTime < _nextTry) return;
        _nextTry = Time.unscaledTime + 0.25f;
        TryResolve();
    }
}
