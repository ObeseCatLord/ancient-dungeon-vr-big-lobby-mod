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
    public const string Version = "1.1.0";

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

        new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
        Log.LogInfo($"{Name} {Version} loaded. Max lobby size: {Max}");
    }

    internal static int Max => Mathf.Clamp(MaxPlayers.Value, VanillaMax, HardMax);
    internal static int DefaultSize => Mathf.Clamp(DefaultLobbySize.Value, 2, Max);
}
