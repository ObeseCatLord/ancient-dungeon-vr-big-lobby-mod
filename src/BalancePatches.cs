using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ADVRMorePlayers;

/// Replacements for array/list reads and player counts that the game only defined for 1-4 players.
/// Every helper returns exactly what vanilla would for in-range input, so they only change behaviour
/// in cases that used to throw or fall back to solo values.
internal static class Safe
{
    /// Boss HP tables (short[] indexed by players-1): extrapolate linearly past the end.
    /// The NG+ multiplier applied afterwards saturates via SatShort (BossHealthOverflowPatch).
    public static short ShortTableAt(short[] a, int i)
    {
        if ((uint)i < (uint)a.Length || a.Length < 2) return a[i];
        int last = a[a.Length - 1], step = Math.Max(0, last - a[a.Length - 2]);
        return (short)Math.Min(short.MaxValue, last + step * (i - (a.Length - 1)));
    }

    /// Integer per-player-count tables: extrapolate linearly past the end.
    public static int IntTableAt(int[] a, int i)
    {
        if ((uint)i < (uint)a.Length || a.Length < 2) return a[i];
        int last = a[a.Length - 1];
        return Math.Max(last, last + (last - a[a.Length - 2]) * (i - (a.Length - 1)));
    }

    /// Per-player spots (spawn points, teleport targets): reuse them round-robin.
    public static T WrapAt<T>(T[] a, int i) => a.Length == 0 ? a[i] : a[((i % a.Length) + a.Length) % a.Length];

    /// Start-floor entrance spawn spots: players past the 4 spots share them, offset sideways so that
    /// two VR players don't spawn inside each other. In-range reads return the vanilla spot unchanged.
    public static RoomCreator.RoomFile.ExtraObjectsInRoom SpawnMarkerAt(List<RoomCreator.RoomFile.ExtraObjectsInRoom> l, int i)
    {
        if ((uint)i < (uint)l.Count || l.Count == 0) return l[i];
        var src = l[((i % l.Count) + l.Count) % l.Count];
        int lap = i / l.Count; // 1 for players 5-8
        float angle = 2f * UnityEngine.Mathf.PI * (i % l.Count) / l.Count;
        return new RoomCreator.RoomFile.ExtraObjectsInRoom
        {
            identifier = src.identifier,
            pos = src.pos + new UnityEngine.Vector3(UnityEngine.Mathf.Cos(angle), 0f, UnityEngine.Mathf.Sin(angle)) * (0.6f * lap),
            rot = src.rot,
            customData = src.customData,
        };
    }

    internal static int Players => GameHandler.INSTANCE.GetAllPlayers().Count;

    /// `(short)x` that clamps instead of wrapping. Boss HP is a short and the game multiplies it by the
    /// player count (final boss stage 2: inspector HP x players), which overflows to negative at 8.
    public static short SatShort(double v) => (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Truncate(v)));

    /// Arena: extra enemies per wave. Vanilla 2/3/4 players = 0.5/0.75/1.0 x wave size (0.25 per player).
    public static float ArenaExtraEnemies(float vanilla, List<string> wave) =>
        Players > Plugin.VanillaMax ? 0.25f * Players * wave.Count : vanilla;

    /// Arcane rift: max enemies alive. Vanilla 1-4 players = 2/3/4/5 (players + 1).
    public static int RiftMaxAlive(int vanilla) => Players > Plugin.VanillaMax ? Players + 1 : vanilla;

    /// Player count as seen by balance switches that only have cases for 1-4.
    public static int BalanceCount(List<NetworkHandler.SpawnedPlayerInfo> players) => Math.Min(players.Count, Plugin.VanillaMax);
}

internal static class Il
{
    private static readonly MethodInfo GetAllPlayers = AccessTools.Method(typeof(GameHandler), nameof(GameHandler.GetAllPlayers));
    private static readonly MethodInfo PlayersCount =
        AccessTools.PropertyGetter(typeof(List<NetworkHandler.SpawnedPlayerInfo>), nameof(List<int>.Count));

    /// `GI.GetAllPlayers().Count` -> `Safe.BalanceCount(GI.GetAllPlayers())`
    public static IEnumerable<CodeInstruction> ClampPlayerCount(IEnumerable<CodeInstruction> code, MethodBase original)
    {
        var list = code.ToList();
        int n = 0;
        for (int i = 1; i < list.Count; i++)
        {
            if (list[i].Calls(PlayersCount) && list[i - 1].Calls(GetAllPlayers))
            {
                list[i] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Safe), nameof(Safe.BalanceCount))).MoveLabelsFrom(list[i]);
                n++;
            }
        }
        Report(original, "player-count clamp", n);
        return list;
    }

    /// Replace `ldelem*` reads from arrays of `elementType[]` with a call to `helper(T[] a, int i)`.
    /// The array is identified by the nearest preceding field/local load of an array type, within the
    /// same straight-line block (the scan stops at branches and branch targets), and the ldelem opcode
    /// must match the element type. If the number of matches isn't `expected`, nothing is patched:
    /// the game changed, and a guess could corrupt the method.
    public static IEnumerable<CodeInstruction> ReplaceArrayReads(IEnumerable<CodeInstruction> code, MethodBase original,
        Type elementType, MethodInfo helper, int expected)
    {
        var list = code.ToList();
        var arrayType = elementType.MakeArrayType();
        var locals = original.GetMethodBody()?.LocalVariables;
        var hits = new List<int>();
        for (int i = 0; i < list.Count; i++)
        {
            if (!LdelemMatches(list[i], elementType)) continue;
            for (int back = i - 1; back >= Math.Max(0, i - 16); back--)
            {
                if (list[back].Branches(out _) || list[back + 1].labels.Count > 0) break;
                Type t = LoadedType(list[back], locals);
                if (t == null || !t.IsArray) continue;
                if (t == arrayType) hits.Add(i);
                break; // nearest array load decides
            }
        }
        if (hits.Count != expected)
        {
            Plugin.Log.LogWarning($"{Name(original)}: expected {expected} {elementType.Name}[] reads, found {hits.Count}; not patched (game updated?)");
            return list;
        }
        foreach (int i in hits) list[i] = new CodeInstruction(OpCodes.Call, helper).MoveLabelsFrom(list[i]);
        Plugin.Log.LogDebug($"{Name(original)}: patched {hits.Count} {elementType.Name}[] reads");
        return list;
    }

    /// Replace `List<elementType>.get_Item` calls with `helper(List<T> l, int i)`; same `expected` rule.
    public static IEnumerable<CodeInstruction> ReplaceListReads(IEnumerable<CodeInstruction> code, MethodBase original,
        Type elementType, MethodInfo helper, int expected)
    {
        var getter = AccessTools.PropertyGetter(typeof(List<>).MakeGenericType(elementType), "Item");
        var list = code.ToList();
        var hits = Enumerable.Range(0, list.Count).Where(i => list[i].Calls(getter)).ToList();
        if (hits.Count != expected)
        {
            Plugin.Log.LogWarning($"{Name(original)}: expected {expected} List<{elementType.Name}> reads, found {hits.Count}; not patched (game updated?)");
            return list;
        }
        foreach (int i in hits) list[i] = new CodeInstruction(OpCodes.Call, helper).MoveLabelsFrom(list[i]);
        Plugin.Log.LogDebug($"{Name(original)}: patched {hits.Count} List<{elementType.Name}> reads");
        return list;
    }

    private static bool LdelemMatches(CodeInstruction ci, Type elementType)
    {
        if (elementType == typeof(short)) return ci.opcode == OpCodes.Ldelem_I2;
        if (elementType == typeof(int)) return ci.opcode == OpCodes.Ldelem_I4;
        if (!elementType.IsValueType) return ci.opcode == OpCodes.Ldelem_Ref;
        return ci.opcode == OpCodes.Ldelem && ci.operand as Type == elementType;
    }

    internal static string Name(MethodBase m) => $"{m.DeclaringType?.FullName}.{m.Name}";

    private static Type LoadedType(CodeInstruction ci, IList<LocalVariableInfo> locals)
    {
        if ((ci.opcode == OpCodes.Ldfld || ci.opcode == OpCodes.Ldsfld) && ci.operand is FieldInfo f) return f.FieldType;
        if (ci.operand is LocalBuilder lb && (ci.opcode == OpCodes.Ldloc || ci.opcode == OpCodes.Ldloc_S)) return lb.LocalType;
        if (locals == null) return null;
        int idx = ci.opcode == OpCodes.Ldloc_0 ? 0 : ci.opcode == OpCodes.Ldloc_1 ? 1
            : ci.opcode == OpCodes.Ldloc_2 ? 2 : ci.opcode == OpCodes.Ldloc_3 ? 3 : -1;
        return idx >= 0 && idx < locals.Count ? locals[idx].LocalType : null;
    }

    private static void Report(MethodBase m, string what, int n)
    {
        if (n == 0) Plugin.Log.LogWarning($"{Name(m)}: no {what} found to patch (game updated?)");
        else Plugin.Log.LogDebug($"{Name(m)}: patched {n} {what}");
    }

    public static MethodBase MoveNext(Type t, string iterator) =>
        AccessTools.EnumeratorMoveNext(AccessTools.Method(t, iterator))
        ?? throw new MissingMethodException($"{t.Name}.{iterator} coroutine not found (game updated?)");
}

// Boss HP per player count: BossInfo.healthForAmountPlayers[count-1] throws at 5+, leaving bosses at
// prefab HP with no NG+ scaling.
[HarmonyPatch(typeof(BossInfo), nameof(BossInfo.Spawned))]
internal static class BossInfoHealthPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code, MethodBase original) =>
        Il.ReplaceArrayReads(code, original, typeof(short), AccessTools.Method(typeof(Safe), nameof(Safe.ShortTableAt)), expected: 1);
}

// Crystal boss spike/enemy tables indexed by count-1: throws at 5+ after disabling everyone's menu.
[HarmonyPatch]
internal static class CrystalBossTablesPatch
{
    private static MethodBase TargetMethod() => Il.MoveNext(typeof(BossCrystalPrisom), nameof(BossCrystalPrisom.BossRoutine));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code, MethodBase original) =>
        Il.ReplaceArrayReads(code, original, typeof(int), AccessTools.Method(typeof(Safe), nameof(Safe.IntTableAt)), expected: 2);
}

// Final boss: only 4 "outside room" teleport targets exist; players 5+ index past them and the
// stage transition dies. Share targets round-robin.
[HarmonyPatch]
internal static class BeastTeleportTargetsPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return Il.MoveNext(typeof(BossBeastStage1), nameof(BossBeastStage1.BossRoutine));
        yield return AccessTools.Method(typeof(BossBeastStage2), nameof(BossBeastStage2.BossBeastStage2_DeathEvent));
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code, MethodBase original) =>
        Il.ReplaceArrayReads(code, original, typeof(BossBeastTeleportToOutsideRoom),
            AccessTools.Method(typeof(Safe), nameof(Safe.WrapAt)).MakeGenericMethod(typeof(BossBeastTeleportToOutsideRoom)), expected: 1);
}

// Start floor has 4 entrance spawn spots; local player index 4+ threw and aborted world generation.
// Players 5-8 now share the spots, offset sideways.
[HarmonyPatch]
internal static class StartRoomSpawnPatch
{
    private static MethodBase TargetMethod() => Il.MoveNext(typeof(WorldGeneratorEvolved), nameof(WorldGeneratorEvolved.WorldGenerationRoutine));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code, MethodBase original) =>
        Il.ReplaceListReads(code, original, typeof(RoomCreator.RoomFile.ExtraObjectsInRoom),
            AccessTools.Method(typeof(Safe), nameof(Safe.SpawnMarkerAt)), expected: 1);
}

// Arena and arcane-rift balance switches only have cases for 1-4 and fall back to solo values otherwise.
// Clamp the switch input to 4 so 5-8 never get solo values, then extend the 4-player values along the
// game's own per-player curve (below). Arena relic loot stays at the 4-player amount (one pedestal each).
[HarmonyPatch]
internal static class BalanceSwitchPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ArenaManager.ArenaRuntimeValues), nameof(ArenaManager.ArenaRuntimeValues.GetAdjustedMaxConcurrentEnemies));
        yield return AccessTools.Method(typeof(ArenaManager.ArenaRuntimeValues), nameof(ArenaManager.ArenaRuntimeValues.GetAdjustedTimeBetweenEnemySpawns));
        yield return Il.MoveNext(typeof(ArenaManager), nameof(ArenaManager.ArenaGraphRoutine));
        yield return Il.MoveNext(typeof(AIPOIArcaneRift), nameof(AIPOIArcaneRift.SemiBossRoutine));
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code, MethodBase original) =>
        Il.ClampPlayerCount(code, original);
}

[HarmonyPatch(typeof(ArenaManager.ArenaRuntimeValues))]
internal static class ArenaRatesPatch
{
    // Vanilla bonus concurrent enemies: 1->0, 2->1, 3->1, 4->2 (+1 per 2 players). Continue it.
    [HarmonyPostfix, HarmonyPatch(nameof(ArenaManager.ArenaRuntimeValues.GetAdjustedMaxConcurrentEnemies))]
    private static void MaxConcurrent(ref int __result)
    {
        int n = Safe.Players;
        if (n > Plugin.VanillaMax) __result += (n - Plugin.VanillaMax) / 2;
    }

    // Vanilla spawn interval multiplier: 1, 0.725, 0.6, 0.5 for 1-4 players (~2/n at 4). Continue as 2/n.
    [HarmonyPostfix, HarmonyPatch(nameof(ArenaManager.ArenaRuntimeValues.GetAdjustedTimeBetweenEnemySpawns))]
    private static void SpawnInterval(ref float __result)
    {
        int n = Safe.Players;
        if (n > Plugin.VanillaMax && !GameHandler.INSTANCE.debugSettings.SpeedUpArenaWaves)
            __result *= (float)Plugin.VanillaMax / n;
    }
}

// Arena per-wave extra enemies: after the 2/3/4-player switch merges into its result local, run it
// through Safe.ArenaExtraEnemies, which continues the 0.25-per-player curve.
[HarmonyPatch]
internal static class ArenaWaveSizePatch
{
    private static MethodBase TargetMethod() => Il.MoveNext(typeof(ArenaManager), nameof(ArenaManager.ArenaGraphRoutine));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code, MethodBase original)
    {
        var list = code.ToList();
        var stringCount = AccessTools.PropertyGetter(typeof(List<string>), nameof(List<string>.Count));
        for (int i = 0; i < list.Count; i++)
        {
            // case "2 players": ldc.r4 0.5; ldloc <wave>; callvirt List<string>.get_Count; conv.r4; mul; stloc <result>; br <merge>
            if (list[i].opcode != OpCodes.Ldc_R4 || !(list[i].operand is float f) || f != 0.5f) continue;
            if (i + 6 >= list.Count || !list[i + 2].Calls(stringCount) || list[i + 4].opcode != OpCodes.Mul) continue;
            var waveLoad = list[i + 1];
            var store = list[i + 5];
            var br = list[i + 6];
            if (!store.IsStloc() || !(br.operand is Label merge)) continue;
            int m = list.FindIndex(ci => ci.labels.Contains(merge));
            if (m < 0) continue;
            var inject = new[]
            {
                LoadOf(store).MoveLabelsFrom(list[m]),
                new CodeInstruction(waveLoad.opcode, waveLoad.operand),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Safe), nameof(Safe.ArenaExtraEnemies))),
                new CodeInstruction(store.opcode, store.operand),
            };
            list.InsertRange(m, inject);
            Plugin.Log.LogDebug("ArenaGraphRoutine: extended per-wave extra enemies");
            return list;
        }
        Plugin.Log.LogWarning("ArenaGraphRoutine: wave-size switch not found (game updated?); arena stays at 4-player wave size");
        return list;
    }

    private static CodeInstruction LoadOf(CodeInstruction stloc)
    {
        if (stloc.opcode == OpCodes.Stloc_0) return new CodeInstruction(OpCodes.Ldloc_0);
        if (stloc.opcode == OpCodes.Stloc_1) return new CodeInstruction(OpCodes.Ldloc_1);
        if (stloc.opcode == OpCodes.Stloc_2) return new CodeInstruction(OpCodes.Ldloc_2);
        if (stloc.opcode == OpCodes.Stloc_3) return new CodeInstruction(OpCodes.Ldloc_3);
        return new CodeInstruction(stloc.opcode == OpCodes.Stloc ? OpCodes.Ldloc : OpCodes.Ldloc_S, stloc.operand);
    }
}

// Arcane rift: the coroutine keeps lMaxEnemiesAlive in a state-machine field. It's stored several times
// (a dummy initial value, then one store per switch arm), so instead of patching a store, pass every
// read of the field through Safe.RiftMaxAlive.
[HarmonyPatch]
internal static class RiftMaxAlivePatch
{
    private static MethodBase TargetMethod() => Il.MoveNext(typeof(AIPOIArcaneRift), nameof(AIPOIArcaneRift.SemiBossRoutine));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code, MethodBase original)
    {
        var helper = AccessTools.Method(typeof(Safe), nameof(Safe.RiftMaxAlive));
        int n = 0;
        foreach (var ci in code)
        {
            yield return ci;
            if (ci.opcode == OpCodes.Ldfld && ci.operand is FieldInfo fi && fi.Name.Contains("lMaxEnemiesAlive"))
            {
                yield return new CodeInstruction(OpCodes.Call, helper);
                n++;
            }
        }
        if (n == 0) Plugin.Log.LogWarning($"{Il.Name(original)}: lMaxEnemiesAlive reads not found (game updated?); rift stays at 4-player limit");
        else Plugin.Log.LogDebug($"{Il.Name(original)}: wrapped {n} lMaxEnemiesAlive reads");
    }
}

// Boss HP casts to short after multiplying by player count / NG+ factor. Saturate instead of wrapping.
[HarmonyPatch]
internal static class BossHealthOverflowPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(BossInfo), nameof(BossInfo.Spawned));
        yield return Il.MoveNext(typeof(BossBeastStage1), nameof(BossBeastStage1.BossRoutine));
        yield return AccessTools.Method(typeof(BossBeastStage2), nameof(BossBeastStage2.Spawned));
        yield return AccessTools.Method(typeof(BossBeastStage3), nameof(BossBeastStage3.Spawned));
        yield return AccessTools.Method(typeof(AIDerangedAdventurer), nameof(AIDerangedAdventurer.Spawned));
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code, MethodBase original)
    {
        var sat = AccessTools.Method(typeof(Safe), nameof(Safe.SatShort));
        int n = 0;
        foreach (var ci in code)
        {
            if (ci.opcode == OpCodes.Conv_I2)
            {
                // conv.r8 accepts both int32 and float32 stack values.
                yield return new CodeInstruction(OpCodes.Conv_R8).MoveLabelsFrom(ci);
                yield return new CodeInstruction(OpCodes.Call, sat);
                n++;
            }
            else yield return ci;
        }
        if (n == 0) Plugin.Log.LogWarning($"{original.DeclaringType?.Name}.{original.Name}: no short casts found (game updated?)");
    }
}

// Special enemies (King Slime, phase-jumping / teleporting AI) agree on a position by weighting each
// alive player's reported position, but vanilla only looks at the first 4 alive players. Same
// weighting, all alive players. Runs locally on each client.
[HarmonyPatch(typeof(ADVRNetworkTransformSpecial), nameof(ADVRNetworkTransformSpecial.GetPositionWish))]
internal static class PositionWishPatch
{
    private static bool Prefix(ADVRNetworkTransformSpecial __instance, ref UnityEngine.Vector3 __result)
    {
        var players = GameHandler.INSTANCE.GetAllAlivePlayers();
        if (players.Count <= Plugin.VanillaMax) return true; // identical to vanilla
        var id = __instance.networkObject.Id;
        var self = __instance.transform.position;
        UnityEngine.Vector3 sum = UnityEngine.Vector3.zero;
        float weights = 0f;
        foreach (var p in players)
        {
            if (!p.playerController.networkedSpecialEnemyInfos.TryGet(id, out var info))
            {
                __result = UnityEngine.Vector3.zero;
                return false;
            }
            if (info.position == UnityEngine.Vector3.zero) continue;
            float w = UnityEngine.Mathf.Min(1f, 1f / UnityEngine.Vector3.Distance(self, p.playerController.transform.position));
            sum += info.position * w;
            weights += w;
        }
        __result = sum / weights;
        return false;
    }
}
