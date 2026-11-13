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
#if DEBUG
    /// Test hook: when > 0, UI is extended as if this many players were connected.
    internal static int TestNeeded;
    internal static int Needed => Math.Min(TestNeeded > 0 ? TestNeeded : ConnectedPlayers, Plugin.HardMax);
#else
    internal static int Needed => Math.Min(ConnectedPlayers, Plugin.HardMax);
#endif

    private static int ConnectedPlayers => GameHandler.INSTANCE?.networkHandler != null ? GameHandler.INSTANCE.TotalConnectedPlayers : 0;

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

    /// Clone the last element of a per-player array of components until it has `needed` entries.
    /// Clones go next to the original, with any children of the source left as they are.
    internal static T[] ExtendByCloning<T>(T[] arr, int needed) where T : Component
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
            res = Append(res, clone.GetComponent<T>());
        }
        return res;
    }

    /// Once a prefab turns out not to look the way we expect (e.g. after a game update), stop trying to
    /// extend it instead of retrying (and logging) on every refresh.
    internal static bool GaveUp(Component c) => c == null || c.GetComponent<GaveUpMarker>() != null;

    internal static void GiveUp(Component c, string what)
    {
        if (c == null || GaveUp(c)) return;
        c.gameObject.AddComponent<GaveUpMarker>();
        Plugin.Log.LogWarning($"{what}: layout not as expected (game updated?); not extending it");
    }
}

internal class GaveUpMarker : MonoBehaviour { }

/// Keeps a laid-out container's content inside the space it originally had by scaling it uniformly.
/// Hidden UI has no layout, so the fit waits until the container is visible (LateUpdate only runs then)
/// plus one frame for Unity's layout pass, and stays pending until it has run.
internal class FitWhenVisible : MonoBehaviour
{
    private Vector2 _originalSize;
    private bool _vertical;
    private int _framesToWait;
    private static readonly Vector3[] Corners = new Vector3[4];

    /// Attach (first call records the container's original size) and request a fit.
    internal static void Request(RectTransform rt, bool vertical)
    {
        var fit = rt.GetComponent<FitWhenVisible>();
        if (fit == null)
        {
            fit = rt.gameObject.AddComponent<FitWhenVisible>();
            fit._originalSize = rt.rect.size;
            fit._vertical = vertical;
        }
        fit._framesToWait = 1;
        fit.enabled = true;
    }

    private void OnEnable() => _framesToWait = Math.Max(_framesToWait, 1);

    private void LateUpdate()
    {
        if (_framesToWait-- > 0) return;
        enabled = false;
        try
        {
            Canvas.ForceUpdateCanvases();
            Fit((RectTransform)transform);
        }
        catch (Exception e) { Plugin.Log.LogError($"Fitting {name} failed: {e}"); }
    }

    private void Fit(RectTransform rt)
    {
        LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
        float needed = Mathf.Max(ChildExtent(rt),
            _vertical ? LayoutUtility.GetPreferredHeight(rt) : LayoutUtility.GetPreferredWidth(rt));
        float available = _vertical ? _originalSize.y : _originalSize.x;
        float scale = needed > available && needed > 0 ? available / needed : 1f;
        var parent = rt.parent as RectTransform;
        Vector2 anchorSpan = parent == null ? Vector2.zero : Vector2.Scale(parent.rect.size, rt.anchorMax - rt.anchorMin);
        // Local rect becomes originalSize/scale, then scaled back down to originalSize on screen.
        rt.sizeDelta = _originalSize / scale - anchorSpan;
        rt.localScale = new Vector3(scale, scale, 1f);
        LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
        Plugin.Log.LogDebug($"Fit {rt.name}: content {needed:F2}, space {available:F2}, scale {scale:F3}");
    }

    /// Span of the active children's rects along the layout axis, in `rt`'s local space, measured at the
    /// container's unscaled size so refits don't compound.
    private float ChildExtent(RectTransform rt)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (Transform child in rt)
        {
            if (!child.gameObject.activeSelf || !(child is RectTransform c)) continue;
            c.GetWorldCorners(Corners);
            foreach (var corner in Corners)
            {
                var p = rt.InverseTransformPoint(corner);
                float v = _vertical ? p.y : p.x;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }
        return max > min ? max - min : 0f;
    }
}

/// Remembers a transform's original local scale so relative scaling can be reapplied safely.
internal class OriginalScale : MonoBehaviour
{
    internal Vector3 Value;

    internal static Vector3 Of(Transform t)
    {
        var o = t.GetComponent<OriginalScale>();
        if (o == null)
        {
            o = t.gameObject.AddComponent<OriginalScale>();
            o.Value = t.localScale;
        }
        return o.Value;
    }
}

[HarmonyPatch(typeof(UIMultiplayerConnectDialog))]
internal static class ConnectDialogRowsPatch
{
    [HarmonyPrefix, HarmonyPatch(nameof(UIMultiplayerConnectDialog.RefreshPlayerNames))]
    private static void RefreshPlayerNames_Prefix(UIMultiplayerConnectDialog __instance)
    {
        try { EnsureRows(__instance, UiExtend.Needed); }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Extending player list failed: {e}");
            UiExtend.GiveUp(__instance, "Player list");
        }
    }

    private static void EnsureRows(UIMultiplayerConnectDialog d, int needed)
    {
        var names = d.txtPlayerNames;
        if (names == null || names.Length == 0 || names.Length >= needed || UiExtend.GaveUp(d)) return;
        if (d.hostPlayerIndicators.Length != names.Length || d.buttonsKickPlayer.Length != names.Length)
        {
            UiExtend.GiveUp(d, "Player list");
            return;
        }

        var srcRow = names[names.Length - 1].transform;
        var list = (RectTransform)srcRow.parent;
        var hostPath = UiExtend.PathFrom(srcRow, d.hostPlayerIndicators[names.Length - 1].transform);
        var kickPath = UiExtend.PathFrom(srcRow, d.buttonsKickPlayer[names.Length - 1].transform);
        FitWhenVisible.Request(list, vertical: true); // records the list's original size before rows are added

        while (d.txtPlayerNames.Length < needed)
        {
            int idx = d.txtPlayerNames.Length;
            var row = UnityEngine.Object.Instantiate(srcRow.gameObject, list, false).transform;
            row.name = $"txtNameOfPlayer{idx + 1}";
            row.SetAsLastSibling();

            var host = UiExtend.Follow(row, hostPath);
            var kick = UiExtend.Follow(row, kickPath)?.GetComponent<Button>();
            var text = row.GetComponent<TextMeshProUGUI>();
            if (host == null || kick == null || text == null)
            {
                UnityEngine.Object.Destroy(row.gameObject);
                UiExtend.GiveUp(d, "Player list");
                return;
            }
            // The cloned button still carries the persistent KickPlayer(3) call. Silence it and bind our index.
            for (int i = 0; i < kick.onClick.GetPersistentEventCount(); i++)
                if (kick.onClick.GetPersistentMethodName(i) == nameof(UIMultiplayerConnectDialog.KickPlayer))
                    kick.onClick.SetPersistentListenerState(i, UnityEventCallState.Off);
            kick.onClick.AddListener(() => d.KickPlayer(idx));

            d.txtPlayerNames = UiExtend.Append(d.txtPlayerNames, text);
            d.hostPlayerIndicators = UiExtend.Append(d.hostPlayerIndicators, host.gameObject);
            d.buttonsKickPlayer = UiExtend.Append(d.buttonsKickPlayer, kick);
        }
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
        catch (Exception e)
        {
            Plugin.Log.LogError($"Extending inventory panels failed: {e}");
            UiExtend.GiveUp(__instance, "Inventory panels");
        }
    }

    private static void EnsurePanels(Inventory inv, int needed)
    {
        var grids = inv.uiParentOfPickupPlayers;
        var settings = inv.uiParentOfPickupPlayerNameAndSettings;
        if (grids == null || grids.Length == 0 || grids.Length >= needed || UiExtend.GaveUp(inv)) return;
        if (settings == null || settings.Length != grids.Length)
        {
            UiExtend.GiveUp(inv, "Inventory panels");
            return;
        }

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
                UiExtend.GiveUp(inv, "Inventory panels");
                return;
            }
            ResetRow(playerSettings, idx);
            settingsTop.gameObject.SetActive(false);
            grid.gameObject.SetActive(false);

            inv.uiParentOfPickupPlayers = UiExtend.Append(inv.uiParentOfPickupPlayers, grid);
            inv.uiParentOfPickupPlayerNameAndSettings = UiExtend.Append(inv.uiParentOfPickupPlayerNameAndSettings, playerSettings);
        }
    }

    /// The source row may be the local player's, which vanilla altered (buttons hidden), or one whose
    /// volume/mute the player changed. Give the clone a fresh state.
    private static void ResetRow(StatScreen_PlayerSettings row, int idx)
    {
        row.playerIndex = idx;
        row.SetButtonsActive(true);
        if (row.volumeSlider != null)
        {
            row.volumeSlider.SetValueWithoutNotify(row.volumeSlider.maxValue);
            row.volumeSlider.gameObject.SetActive(false);
        }
        row._volumeSliderEnabled = false;
        if (row.btnMutePlayerImage != null) row.btnMutePlayerImage.sprite = row.speakerEnabled;
    }
}

// Stat screen shows other players' stats in a grid of 3 panels. Add panels for up to 7 other players
// and shrink the grid to fit its area. With more other players than panels vanilla throws on every
// refresh, which also stops the inventory from animating open (player stuck), so the loop is bounded
// too, in case extending isn't possible.
[HarmonyPatch(typeof(Inventory), nameof(Inventory.UpdateNetworkedPlayerStats))]
internal static class NetworkedStatsPatch
{
    private static bool Prefix(Inventory __instance)
    {
        var infos = __instance._networkedPlayerInfos;
        ExtendPanels(__instance, infos.Count);
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

    private static void ExtendPanels(Inventory inv, int others)
    {
        var panels = inv.networkedPlayerStats;
        int needed = Math.Min(others, Plugin.HardMax - 1);
        if (panels == null || panels.Length == 0 || panels.Length >= needed || UiExtend.GaveUp(inv)) return;
        try
        {
            if (panels[0].transform.parent is RectTransform grid)
                FitWhenVisible.Request(grid, vertical: true); // records the grid's original size first
            inv.networkedPlayerStats = UiExtend.ExtendByCloning(panels, needed);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Extending other-player stat panels failed: {e}");
            UiExtend.GiveUp(inv, "Other-player stat panels");
        }
    }
}

// Insight board: each upgrade shows one lock per player (open/closed = whether that player bought it).
// The prefab has 4. Every player's purchases are already networked on their own PlayerController, so
// only the icons are missing. Add icons and shrink the row to fit the button.
[HarmonyPatch(typeof(ProgressItemUI), nameof(ProgressItemUI.UpdateInfosInsight))]
internal static class InsightLocksPatch
{
    private static void Prefix(ProgressItemUI __instance)
    {
        try
        {
            var locks = __instance.playerLockImages;
            int needed = UiExtend.Needed;
            if (locks == null || locks.Length == 0 || locks.Length >= needed || UiExtend.GaveUp(__instance)) return;
            var row = locks[0].transform.parent as RectTransform;
            if (row == null)
            {
                UiExtend.GiveUp(__instance, "Insight locks");
                return;
            }
            FitWhenVisible.Request(row, vertical: false); // records the row's original size before locks are added
            __instance.playerLockImages = UiExtend.ExtendByCloning(locks, needed);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Extending insight locks failed: {e}");
            UiExtend.GiveUp(__instance, "Insight locks");
        }
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
        try
        {
            int needed = UiExtend.Needed;
            var markers = __instance.playerPositions;
            var labels = __instance.playerLabelTransforms;
            if (markers == null || markers.Length >= needed || UiExtend.GaveUp(__instance)) return;
            if (labels == null || labels.Length != markers.Length)
            {
                UiExtend.GiveUp(__instance, "Minimap markers");
                return;
            }
            // Build both before assigning either, so the arrays never end up with different lengths.
            var newMarkers = UiExtend.ExtendByCloning(markers, needed);
            var newLabels = UiExtend.ExtendByCloning(labels, needed);
            __instance.playerPositions = newMarkers;
            __instance.playerLabelTransforms = newLabels;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Extending minimap markers failed: {e}");
            UiExtend.GiveUp(__instance, "Minimap markers");
        }
    }
}

// Run-prep weapon buttons show the names of players who picked that weapon, in 4 fixed slots. Add slots
// for 5-8 and shrink the name column (relative to its original scale) when more than 4 names show.
[HarmonyPatch(typeof(RunPreparationUI_ButtonSelectWeapon), nameof(RunPreparationUI_ButtonSelectWeapon.UpdatePlayersThatSelectedWeapon))]
internal static class WeaponBadgesPatch
{
    private static void Prefix(RunPreparationUI_ButtonSelectWeapon __instance)
    {
        try
        {
            if (UiExtend.GaveUp(__instance)) return;
            __instance.playersThatSelectedWeapon = UiExtend.ExtendByCloning(__instance.playersThatSelectedWeapon, UiExtend.Needed);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Extending weapon badges failed: {e}");
            UiExtend.GiveUp(__instance, "Weapon badges");
        }
    }

    private static void Postfix(RunPreparationUI_ButtonSelectWeapon __instance)
    {
        var names = __instance.playersThatSelectedWeapon;
        if (names == null || names.Length == 0 || names[0] == null) return;
        var column = names[0].transform.parent;
        int shown = __instance._playersThatSelected.Count;
        float factor = shown > Plugin.VanillaMax ? (float)Plugin.VanillaMax / shown : 1f;
        var original = OriginalScale.Of(column);
        column.localScale = new Vector3(original.x * factor, original.y * factor, original.z);
    }
}
