using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ADVRMorePlayers;

/// The scene ships exactly 4 rows/panels for per-player UI. Clone the last one on demand so players 5-8
/// get a row too. Purely local; only affects players running the mod.
internal static class UiExtend
{
    /// Test hook: when > 0, UI is extended as if this many players were connected.
    internal static int TestNeeded;

    internal static int Needed => Math.Min(TestNeeded > 0 ? TestNeeded : GameHandler.INSTANCE.TotalConnectedPlayers, Plugin.HardMax);

    /// Path of names from `root` down to `t`, used to find the matching object inside a clone.
    internal static List<string> PathFrom(Transform root, Transform t)
    {
        var path = new List<string>();
        for (var cur = t; cur != null && cur != root; cur = cur.parent) path.Insert(0, cur.name);
        return path;
    }

    internal static Transform Follow(Transform root, List<string> path)
    {
        var cur = root;
        foreach (var name in path)
        {
            cur = cur.Find(name);
            if (cur == null) return null;
        }
        return cur;
    }

    /// The ancestor of `t` that is a direct child of `layout` (or `t` itself).
    internal static Transform TopUnder(Transform layout, Transform t)
    {
        var cur = t;
        while (cur != null && cur.parent != layout) cur = cur.parent;
        return cur ?? t;
    }

    internal static T[] Append<T>(T[] arr, T item)
    {
        var res = new T[arr.Length + 1];
        Array.Copy(arr, res, arr.Length);
        res[arr.Length] = item;
        return res;
    }

    /// Shrink a laid-out container uniformly so its content fits the space it originally had.
    /// `vertical` picks the axis the layout grows along. Content size is measured from the children's
    /// actual rects, so it works whatever layout component (if any) positions them.
    internal static void FitInParent(RectTransform rt, Vector2 originalSize, bool vertical = true)
    {
        LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
        float needed = Mathf.Max(ChildExtent(rt, vertical),
            vertical ? LayoutUtility.GetPreferredHeight(rt) : LayoutUtility.GetPreferredWidth(rt));
        float available = vertical ? originalSize.y : originalSize.x;
        float scale = needed > available && needed > 0 ? available / needed : 1f;
        var parent = rt.parent as RectTransform;
        Vector2 anchorSpan = parent == null ? Vector2.zero : Vector2.Scale(parent.rect.size, rt.anchorMax - rt.anchorMin);
        // Local rect becomes originalSize/scale, then scaled back down to originalSize on screen.
        rt.sizeDelta = originalSize / scale - anchorSpan;
        rt.localScale = new Vector3(scale, scale, 1f);
        LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
        Plugin.Log.LogDebug($"Fit {rt.name}: content {needed:F2}, space {available:F2}, scale {scale:F3}");
    }

    /// Fit after Unity's next layout pass, so newly cloned children have real positions.
    internal static void FitNextFrame(MonoBehaviour host, RectTransform rt, Vector2 originalSize, bool vertical = true)
    {
        if (host != null && host.isActiveAndEnabled) host.StartCoroutine(FitRoutine(rt, originalSize, vertical));
        else FitInParent(rt, originalSize, vertical);
    }

    private static System.Collections.IEnumerator FitRoutine(RectTransform rt, Vector2 originalSize, bool vertical)
    {
        // Hidden UI has no layout; wait until it's shown, then one frame for the layout pass.
        while (rt != null && !rt.gameObject.activeInHierarchy) yield return null;
        yield return null;
        Canvas.ForceUpdateCanvases();
        if (rt != null) FitInParent(rt, originalSize, vertical);
    }

    private static readonly Vector3[] Corners = new Vector3[4];

    /// Span of the active children's rects along one axis, in `rt`'s local space.
    private static float ChildExtent(RectTransform rt, bool vertical)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (Transform child in rt)
        {
            if (!child.gameObject.activeSelf || !(child is RectTransform c)) continue;
            c.GetWorldCorners(Corners);
            foreach (var corner in Corners)
            {
                var p = rt.InverseTransformPoint(corner);
                float v = vertical ? p.y : p.x;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }
        return max > min ? max - min : 0f;
    }

    /// Clone the last element of a per-player array of components until it has `needed` entries.
    /// Clones go next to the original, with any children of the source left as they are.
    internal static T[] ExtendByCloning<T>(T[] arr, int needed, Action<T, int> init = null) where T : Component
    {
        if (arr == null || arr.Length == 0 || arr.Length >= needed) return arr;
        var src = arr[arr.Length - 1];
        var res = arr;
        while (res.Length < needed)
        {
            int idx = res.Length;
            var clone = UnityEngine.Object.Instantiate(src.gameObject, src.transform.parent, false);
            clone.name = System.Text.RegularExpressions.Regex.Replace(src.name, @"\d+$", "") + (idx + 1);
            clone.transform.SetSiblingIndex(src.transform.GetSiblingIndex() + (idx - arr.Length) + 1);
            var comp = clone.GetComponent<T>();
            init?.Invoke(comp, idx);
            res = Append(res, comp);
        }
        return res;
    }
}

[HarmonyPatch(typeof(UIMultiplayerConnectDialog))]
internal static class ConnectDialogRowsPatch
{
    private static readonly Dictionary<UIMultiplayerConnectDialog, Vector2> OriginalListSize = new();
    private static readonly HashSet<UIMultiplayerConnectDialog> PendingFit = new();

    // Runs from the dialog's Update every 50 frames, so the dialog is active and can host the fit coroutine.
    [HarmonyPrefix, HarmonyPatch(nameof(UIMultiplayerConnectDialog.RefreshPlayerNames))]
    private static void RefreshPlayerNames_Prefix(UIMultiplayerConnectDialog __instance)
    {
        try
        {
            EnsureRows(__instance, UiExtend.Needed);
            if (PendingFit.Remove(__instance))
                UiExtend.FitNextFrame(__instance, (RectTransform)__instance.txtPlayerNames[0].transform.parent, OriginalListSize[__instance]);
        }
        catch (Exception e) { Plugin.Log.LogError($"Extending player list failed: {e}"); }
    }

    private static void EnsureRows(UIMultiplayerConnectDialog d, int needed)
    {
        var names = d.txtPlayerNames;
        if (names == null || names.Length == 0 || names.Length >= needed) return;
        if (d.hostPlayerIndicators.Length != names.Length || d.buttonsKickPlayer.Length != names.Length) return;

        var srcRow = names[names.Length - 1].transform;
        var list = (RectTransform)srcRow.parent;
        if (!OriginalListSize.ContainsKey(d)) OriginalListSize[d] = list.rect.size;
        var hostPath = UiExtend.PathFrom(srcRow, d.hostPlayerIndicators[names.Length - 1].transform);
        var kickPath = UiExtend.PathFrom(srcRow, d.buttonsKickPlayer[names.Length - 1].transform);

        while (d.txtPlayerNames.Length < needed)
        {
            int idx = d.txtPlayerNames.Length;
            var row = UnityEngine.Object.Instantiate(srcRow.gameObject, list, false).transform;
            row.name = $"txtNameOfPlayer{idx + 1}";
            row.SetAsLastSibling();

            var host = UiExtend.Follow(row, hostPath);
            var kick = UiExtend.Follow(row, kickPath)?.GetComponent<Button>();
            if (host == null || kick == null)
            {
                UnityEngine.Object.Destroy(row.gameObject);
                Plugin.Log.LogWarning("Player list row layout not as expected; not extending");
                return;
            }
            // The cloned button still carries the persistent KickPlayer(3) call. Silence it and bind our index.
            for (int i = 0; i < kick.onClick.GetPersistentEventCount(); i++)
                if (kick.onClick.GetPersistentMethodName(i) == nameof(UIMultiplayerConnectDialog.KickPlayer))
                    kick.onClick.SetPersistentListenerState(i, UnityEventCallState.Off);
            kick.onClick.AddListener(() => d.KickPlayer(idx));

            d.txtPlayerNames = UiExtend.Append(d.txtPlayerNames, row.GetComponent<TextMeshProUGUI>());
            d.hostPlayerIndicators = UiExtend.Append(d.hostPlayerIndicators, host.gameObject);
            d.buttonsKickPlayer = UiExtend.Append(d.buttonsKickPlayer, kick);
        }
        PendingFit.Add(d);
    }
}

[HarmonyPatch(typeof(Inventory))]
internal static class InventoryPanelsPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(Inventory.RefreshFoundPickupConfiguration))]
    [HarmonyPatch(nameof(Inventory.AddPickupImage))]
    private static void Prefix(Inventory __instance)
    {
        try { EnsurePanels(__instance, UiExtend.Needed); }
        catch (Exception e) { Plugin.Log.LogError($"Extending inventory panels failed: {e}"); }
    }

    private static void EnsurePanels(Inventory inv, int needed)
    {
        var grids = inv.uiParentOfPickupPlayers;
        var settings = inv.uiParentOfPickupPlayerNameAndSettings;
        if (grids == null || grids.Length == 0 || grids.Length >= needed || settings.Length != grids.Length) return;

        var srcGrid = grids[grids.Length - 1];
        var layout = srcGrid.parent;
        var srcSettingsTop = UiExtend.TopUnder(layout, settings[settings.Length - 1].transform);
        var settingsPath = UiExtend.PathFrom(srcSettingsTop, settings[settings.Length - 1].transform);

        while (inv.uiParentOfPickupPlayers.Length < needed)
        {
            int idx = inv.uiParentOfPickupPlayers.Length;

            var settingsTop = UnityEngine.Object.Instantiate(srcSettingsTop.gameObject, layout, false).transform;
            settingsTop.name = $"ProxyPlayerSettings_{idx + 1}";
            settingsTop.SetAsLastSibling();
            var playerSettings = (settingsPath.Count == 0 ? settingsTop : UiExtend.Follow(settingsTop, settingsPath))
                ?.GetComponent<StatScreen_PlayerSettings>();

            var grid = UnityEngine.Object.Instantiate(srcGrid.gameObject, layout, false).transform;
            grid.name = $"UpgradesFoundMP_Player{idx + 1}";
            grid.SetAsLastSibling();
            for (int c = grid.childCount - 1; c >= 0; c--) UnityEngine.Object.Destroy(grid.GetChild(c).gameObject);

            if (playerSettings == null)
            {
                UnityEngine.Object.Destroy(settingsTop.gameObject);
                UnityEngine.Object.Destroy(grid.gameObject);
                Plugin.Log.LogWarning("Inventory panel layout not as expected; not extending");
                return;
            }
            playerSettings.playerIndex = idx;
            settingsTop.gameObject.SetActive(false);
            grid.gameObject.SetActive(false);

            inv.uiParentOfPickupPlayers = UiExtend.Append(inv.uiParentOfPickupPlayers, grid);
            inv.uiParentOfPickupPlayerNameAndSettings = UiExtend.Append(inv.uiParentOfPickupPlayerNameAndSettings, playerSettings);
        }
    }
}

// Stat screen shows other players' stats in a fixed set of panels. With more other players than panels
// vanilla throws on every refresh, which also stops the inventory from animating open (player stuck).
// Same loop, bounded; players past the last panel aren't shown.
[HarmonyPatch(typeof(Inventory), nameof(Inventory.UpdateNetworkedPlayerStats))]
internal static class NetworkedStatsPatch
{
    private static bool Prefix(Inventory __instance)
    {
        var infos = __instance._networkedPlayerInfos;
        var panels = __instance.networkedPlayerStats;
        int shown = Math.Min(infos.Count, panels.Length);
        for (int i = 0; i < shown; i++)
        {
            var info = infos[i];
            var inv = info.playerController.networkedPlayerInventory;
            if (inv != null)
                panels[i].UpdateStats(info, inv.MostRecentStatValues, ref inv.CurrentNetworkedWeaponInfo);
        }
        for (int j = 0; j < panels.Length; j++)
            panels[j].gameObject.SetActive(infos.Count >= j + 1);
        return false;
    }
}

// Insight board: each upgrade shows one lock per player (open/closed = whether that player bought it).
// The prefab has 4. Every player's purchases are already networked on their own PlayerController, so
// only the icons are missing. Add icons and shrink the row to fit the button.
[HarmonyPatch(typeof(ProgressItemUI), nameof(ProgressItemUI.UpdateInfosInsight))]
internal static class InsightLocksPatch
{
    private static readonly Dictionary<RectTransform, Vector2> OriginalRowSize = new();
    private static readonly HashSet<ProgressItemUI> NeedsFit = new();

    private static void Prefix(ProgressItemUI __instance)
    {
        try
        {
            var locks = __instance.playerLockImages;
            if (locks == null || locks.Length == 0 || locks.Length >= UiExtend.Needed) return;
            var row = (RectTransform)locks[0].transform.parent;
            if (!OriginalRowSize.ContainsKey(row)) OriginalRowSize[row] = row.rect.size;
            __instance.playerLockImages = UiExtend.ExtendByCloning(locks, UiExtend.Needed);
            NeedsFit.Add(__instance);
        }
        catch (Exception e) { Plugin.Log.LogError($"Extending insight locks failed: {e}"); }
    }

    // Vanilla reparents and shows/hides the row and its locks; fit afterwards.
    private static void Postfix(ProgressItemUI __instance)
    {
        if (!NeedsFit.Remove(__instance)) return;
        try
        {
            var row = (RectTransform)__instance.playerLockImages[0].transform.parent;
            UiExtend.FitNextFrame(__instance, row, OriginalRowSize[row], vertical: false);
        }
        catch (Exception e) { Plugin.Log.LogError($"Fitting insight locks failed: {e}"); }
    }
}

// Minimap: player markers and name labels are fixed arrays of 4, positioned explicitly every frame.
[HarmonyPatch(typeof(Minimap))]
internal static class MinimapMarkersPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(Minimap.UpdatePlayerIcons))]
    [HarmonyPatch(nameof(Minimap.UpdateAmountPlayersOnMinimap))]
    private static void Prefix(Minimap __instance)
    {
        int needed = UiExtend.Needed;
        if (__instance.playerPositions == null || __instance.playerPositions.Length >= needed) return;
        if (__instance.playerLabelTransforms == null || __instance.playerLabelTransforms.Length != __instance.playerPositions.Length) return;
        try
        {
            __instance.playerPositions = UiExtend.ExtendByCloning(__instance.playerPositions, needed);
            __instance.playerLabelTransforms = UiExtend.ExtendByCloning(__instance.playerLabelTransforms, needed);
        }
        catch (Exception e) { Plugin.Log.LogError($"Extending minimap markers failed: {e}"); }
    }
}

// Run-prep weapon buttons show the names of players who picked that weapon, in 4 fixed slots. Add slots
// for 5-8 and shrink the name column when more than 4 names are showing.
[HarmonyPatch(typeof(RunPreparationUI_ButtonSelectWeapon), nameof(RunPreparationUI_ButtonSelectWeapon.UpdatePlayersThatSelectedWeapon))]
internal static class WeaponBadgesPatch
{
    private static void Prefix(RunPreparationUI_ButtonSelectWeapon __instance)
    {
        try { __instance.playersThatSelectedWeapon = UiExtend.ExtendByCloning(__instance.playersThatSelectedWeapon, UiExtend.Needed); }
        catch (Exception e) { Plugin.Log.LogError($"Extending weapon badges failed: {e}"); }
    }

    private static void Postfix(RunPreparationUI_ButtonSelectWeapon __instance)
    {
        var names = __instance.playersThatSelectedWeapon;
        if (names == null || names.Length == 0) return;
        int shown = __instance._playersThatSelected.Count;
        float scale = shown > Plugin.VanillaMax ? (float)Plugin.VanillaMax / shown : 1f;
        names[0].transform.parent.localScale = new Vector3(scale, scale, 1f);
    }
}
