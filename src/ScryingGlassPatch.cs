using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ADVRMorePlayers;

/// PropDungeonItemScryingGlass.PlayersWithItem is a networked array of 4 (layout shared with every peer).
/// With 5+ players the spawn callback throws on Set(4, ...) after everything that matters is already
/// written (item count, position, first 4 owners). Swallow that one exception so the spawn completes;
/// players 5-8 just don't get the glass's speech bubble.
[HarmonyPatch]
internal static class ScryingGlassPatch
{
    private static MethodBase TargetMethod() =>
        typeof(PropDungeonItemScryingGlass).GetNestedTypes(AccessTools.all)
            .SelectMany(t => t.GetMethods(AccessTools.all))
            .First(m => m.Name.Contains("<SpawnScryingGlass>g__OnBeforeSpawned"));

    private static Exception Finalizer(Exception __exception)
    {
        if (__exception is IndexOutOfRangeException)
        {
            Plugin.Log.LogInfo("Scrying glass: more owners than networked slots; extra players skip the speech bubble");
            return null;
        }
        return __exception;
    }
}
