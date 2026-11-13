using System.Reflection.Emit;
using System.Linq;
using System.Collections.Generic;
using Fusion;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace ADVRMorePlayers;

/// Host-side: let the lobby creator pick 5-8 players. The value flows into StartGameArgs.PlayerCount,
/// which becomes the Photon room's MaxPlayers. Joining clients (modded or not) initialise Fusion from the
/// room's MaxPlayers, so they need nothing to fit in.
[HarmonyPatch(typeof(UIMultiplayerConnectDialog))]
internal static class LobbyPatches
{
    // The "4" button becomes a cycling "4..8" button; 2 and 3 keep working as before.
    [HarmonyPrefix, HarmonyPatch(nameof(UIMultiplayerConnectDialog.SetMaxPlayers))]
    private static void SetMaxPlayers_Prefix(UIMultiplayerConnectDialog __instance, ref int i)
    {
        if (i != Plugin.VanillaMax) return;
        int current = __instance._lobbySettings.maxPlayers;
        i = current < Plugin.VanillaMax ? Plugin.Max
            : current >= Plugin.Max ? Plugin.VanillaMax
            : current + 1;
    }

    [HarmonyPostfix, HarmonyPatch(nameof(UIMultiplayerConnectDialog.SetMaxPlayers))]
    private static void SetMaxPlayers_Postfix(UIMultiplayerConnectDialog __instance) => RefreshSizeButton(__instance);

    // Start() and the debug auto-join path pass a literal 4 meaning "the biggest lobby".
    [HarmonyPrefix, HarmonyPatch(nameof(UIMultiplayerConnectDialog.SetLobbySettings))]
    private static void SetLobbySettings_Prefix(ref int maxPlayers)
    {
        if (maxPlayers == Plugin.VanillaMax) maxPlayers = Plugin.DefaultSize;
    }

    [HarmonyPostfix, HarmonyPatch(nameof(UIMultiplayerConnectDialog.SetLobbySettings))]
    private static void SetLobbySettings_Postfix(UIMultiplayerConnectDialog __instance) => RefreshSizeButton(__instance);

    private static void RefreshSizeButton(UIMultiplayerConnectDialog dialog)
    {
        int size = dialog._lobbySettings.maxPlayers;
        var btn = dialog.btnMaxPlayers4;
        if (btn == null) return;
        Util.HighlightButtonAlt(btn, size >= Plugin.VanillaMax);
        var label = btn.GetComponentInChildren<TMP_Text>(true);
        if (label != null) label.text = (size >= Plugin.VanillaMax ? size : Plugin.Max).ToString();
    }

    // Public room browser: vanilla hides rooms with MaxPlayers > 4. Swap just that constant, so the
    // rest of the vanilla method stays in charge.
    [HarmonyTranspiler, HarmonyPatch(nameof(UIMultiplayerConnectDialog.InitPublicLobbyList))]
    private static IEnumerable<CodeInstruction> InitPublicLobbyList_Transpiler(IEnumerable<CodeInstruction> code)
    {
        var getMaxPlayers = AccessTools.PropertyGetter(typeof(SessionInfo), nameof(SessionInfo.MaxPlayers));
        var list = code.ToList();
        int n = 0;
        for (int i = 0; i + 1 < list.Count; i++)
        {
            if (list[i].Calls(getMaxPlayers) && list[i + 1].LoadsConstant(Plugin.VanillaMax))
            {
                list[i + 1] = new CodeInstruction(OpCodes.Ldc_I4, Plugin.HardMax).MoveLabelsFrom(list[i + 1]);
                n++;
            }
        }
        if (n != 1) Plugin.Log.LogWarning($"Public room list: expected 1 MaxPlayers<=4 check, found {n} (game updated?)");
        return list;
    }
}

[HarmonyPatch(typeof(UIMultiplayerConnectDialog_ButtonJoinPublicRoom), nameof(UIMultiplayerConnectDialog_ButtonJoinPublicRoom.SanitizePlayerCount))]
internal static class PublicRoomCountPatch
{
    private static bool Prefix(int count, ref int __result)
    {
        __result = Mathf.Clamp(count, 0, Plugin.HardMax);
        return false;
    }
}
