using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ADVRMorePlayers;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "advr.moreplayers";
    public const string Name = "Big Lobby";
    public const string Version = "1.2.0";

    /// The game's original hard cap. Fusion networked collections and scene UI are built for this many.
    public const int VanillaMax = 4;
    public const int HardMax = 8;

    internal static ManualLogSource Log;
    internal static ConfigEntry<int> MaxPlayers;
    internal static ConfigEntry<int> DefaultLobbySize;

    private void Awake()
    {
        Log = Logger;
        MaxPlayers = Config.Bind("General", "MaxPlayers", HardMax,
            new ConfigDescription("Largest lobby size you can pick when hosting.",
                new AcceptableValueRange<int>(VanillaMax, HardMax)));
        DefaultLobbySize = Config.Bind("General", "DefaultLobbySize", HardMax,
            new ConfigDescription("Lobby size preselected in the multiplayer menu (capped at MaxPlayers).",
                new AcceptableValueRange<int>(2, HardMax)));

        PreparationPatches.RegisterNet();
        PedestalExtrasPatch.RegisterNet();
        ApplyPatches(new Harmony(Guid));
        Log.LogInfo($"{Name} {Version} loaded. Max lobby size: {Max}");
    }

    /// These keep each player's own game working with 5+ players. If one fails to apply (e.g. after a
    /// game update), 5+ player lobbies would break this player, so lobby sizes stay capped at 4.
    private static readonly HashSet<Type> Critical = new()
    {
        typeof(InventoryPanelsPatch), typeof(NetworkedStatsPatch), typeof(StartRoomSpawnPatch),
        typeof(PreparationPatches),
    };

    /// Raises lobby size; only applied once everything critical is in place.
    private static readonly HashSet<Type> LobbySize = new() { typeof(LobbyPatches) };

    private static bool _lobbyCapped;

    /// Patch each class on its own, so one patch failing (e.g. a renamed game method) doesn't silently
    /// leave every later class unpatched, as Harmony.PatchAll would.
    private static void ApplyPatches(Harmony harmony)
    {
        var failedCritical = new List<string>();
        var patchTypes = AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly)
            .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0).ToList();

        foreach (var type in patchTypes.Where(t => !LobbySize.Contains(t)))
        {
            try { harmony.CreateClassProcessor(type).Patch(); }
            catch (Exception e)
            {
                Log.LogError($"Patch {type.Name} failed: {e.GetBaseException().Message}");
                if (Critical.Contains(type) || (type.DeclaringType != null && Critical.Contains(type.DeclaringType)))
                    failedCritical.Add(type.Name);
            }
        }

        if (failedCritical.Count > 0)
        {
            _lobbyCapped = true;
            Log.LogError($"Essential fixes failed ({string.Join(", ", failedCritical)}); lobbies stay at 4 players. " +
                         "The game probably updated; check for a newer Big Lobby release.");
            return;
        }
        foreach (var type in patchTypes.Where(LobbySize.Contains))
        {
            try { harmony.CreateClassProcessor(type).Patch(); }
            catch (Exception e) { Log.LogError($"Patch {type.Name} failed: {e.GetBaseException().Message}"); }
        }
    }

    internal static int Max => _lobbyCapped ? VanillaMax : Mathf.Clamp(MaxPlayers.Value, VanillaMax, HardMax);
    internal static int DefaultSize => Mathf.Clamp(DefaultLobbySize.Value, 2, Max);
}
