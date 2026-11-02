using System;
using System.IO;
using System.Linq;
using BepInEx;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Test-only: file-driven bridge so an agent can drive menus without taking over the user's mouse.
[BepInPlugin("advr.testbridge", "Test Bridge", "0.0.1")]
public class Bridge : BaseUnityPlugin
{
    string cmdFile, outFile; float next;

    void Awake()
    {
        cmdFile = Path.Combine(Paths.BepInExRootPath, "bridge_cmd.txt");
        outFile = Path.Combine(Paths.BepInExRootPath, "bridge_out.txt");
    }

    void Out(string s) { File.AppendAllText(outFile, s + "\n"); Logger.LogInfo(s); }

    static string Label(Button b) => string.Join(" ", b.GetComponentsInChildren<TMP_Text>(true).Select(t => (t.text ?? "").Replace("\n", " ").Trim()).Where(t => t.Length > 0));

    static string PathOf(Transform t) { var p = t.name; while ((t = t.parent) != null) p = t.name + "/" + p; return p; }

    void Update()
    {
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + 0.5f;
        if (!File.Exists(cmdFile)) return;
        var lines = File.ReadAllLines(cmdFile);
        File.Delete(cmdFile);
        foreach (var line in lines)
        {
            try { Run(line.Trim()); } catch (Exception e) { Out("ERR " + line + ": " + e); }
        }
    }

    void Run(string line)
    {
        if (line.Length == 0) return;
        var parts = line.Split(new[] { ' ' }, 2);
        string arg = parts.Length > 1 ? parts[1] : "";
        var buttons = UnityEngine.Object.FindObjectsByType<Button>(FindObjectsSortMode.None).Where(b => b.isActiveAndEnabled).ToList();
        switch (parts[0])
        {
            case "buttons":
                foreach (var b in buttons) Out($"BTN [{Label(b)}] {PathOf(b.transform)} interactable={b.interactable}");
                break;
            case "click": // click <substring of path or label>
            {
                var b = buttons.FirstOrDefault(x => PathOf(x.transform).EndsWith(arg)) ?? buttons.FirstOrDefault(x => Label(x).Contains(arg));
                if (b == null) { Out("NOBTN " + arg); break; }
                b.onClick.Invoke();
                Out($"CLICKED [{Label(b)}] {PathOf(b.transform)}");
                break;
            }
            case "state":
            {
                var d = UnityEngine.Object.FindAnyObjectByType<UIMultiplayerConnectDialog>(FindObjectsInactive.Include);
                if (d != null) Out($"LOBBY maxPlayers={d._lobbySettings.maxPlayers} btn4label=[{Label(d.btnMaxPlayers4)}] rows={d.txtPlayerNames.Length}");
                var r = GameHandler.INSTANCE?.networkHandler?.GetNetworkRunner();
                if (r != null && r.SessionInfo.IsValid) Out($"SESSION name={r.SessionInfo.Name} max={r.SessionInfo.MaxPlayers} players={r.SessionInfo.PlayerCount} mode={r.GameMode}");
                else Out("SESSION none");
                break;
            }
            case "size": // press the max-players "4" button like a user would
            {
                var d = UnityEngine.Object.FindAnyObjectByType<UIMultiplayerConnectDialog>(FindObjectsInactive.Include);
                d.btnMaxPlayers4.onClick.Invoke();
                Out($"SIZE maxPlayers={d._lobbySettings.maxPlayers} label=[{Label(d.btnMaxPlayers4)}]");
                break;
            }
            case "create": // create <roomcode>: same call as the Create button with that code typed
            {
                var d = UnityEngine.Object.FindAnyObjectByType<UIMultiplayerConnectDialog>(FindObjectsInactive.Include);
                d.inputFieldRoomCode.text = arg;
                d.CreateRoom(arg);
                Out("CREATE " + arg);
                break;
            }
            case "leave":
            {
                var d = UnityEngine.Object.FindAnyObjectByType<UIMultiplayerConnectDialog>(FindObjectsInactive.Include);
                d.ResetToSingleplayer();
                Out("LEAVE");
                break;
            }
            case "rows": // rows <n>: run the mod's row extension for n players, fill fake names, screenshot
            {
                int n = int.Parse(arg);
                var d = UnityEngine.Object.FindAnyObjectByType<UIMultiplayerConnectDialog>(FindObjectsInactive.Include);
                var t = HarmonyLib.AccessTools.TypeByName("ADVRMorePlayers.ConnectDialogRowsPatch");
                HarmonyLib.AccessTools.Method(t, "RefreshPlayerNames_Prefix").Invoke(null, new object[] { d });
                for (int i = 0; i < d.txtPlayerNames.Length; i++)
                {
                    d.txtPlayerNames[i].text = "Adventurer " + (i + 1);
                    d.buttonsKickPlayer[i].gameObject.SetActive(i > 0);
                    d.hostPlayerIndicators[i].SetActive(i == 0);
                }
                Out($"ROWS names={d.txtPlayerNames.Length} kick={d.buttonsKickPlayer.Length} host={d.hostPlayerIndicators.Length} listScale={d.txtPlayerNames[0].transform.parent.localScale}");
                for (int i = 0; i < d.buttonsKickPlayer.Length; i++)
                {
                    var b = d.buttonsKickPlayer[i];
                    Out($"  kick[{i}] persistent={b.onClick.GetPersistentEventCount()} path={PathOf(b.transform)}");
                }
                ScreenCapture.CaptureScreenshot(Path.Combine(Paths.BepInExRootPath, "rows.png"));
                break;
            }
            case "panels": // panels <n>: run the mod's inventory panel extension
            {
                int n = int.Parse(arg);
                var inv = GameHandler.INSTANCE.inventory;
                var t = HarmonyLib.AccessTools.TypeByName("ADVRMorePlayers.InventoryPanelsPatch");
                HarmonyLib.AccessTools.Method(t, "EnsurePanels").Invoke(null, new object[] { inv, n });
                Out($"PANELS grids={inv.uiParentOfPickupPlayers.Length} settings={inv.uiParentOfPickupPlayerNameAndSettings.Length} stats={inv.networkedPlayerStats.Length}");
                for (int i = 0; i < inv.uiParentOfPickupPlayers.Length; i++)
                    Out($"  [{i}] grid={PathOf(inv.uiParentOfPickupPlayers[i])} sibling={inv.uiParentOfPickupPlayers[i].GetSiblingIndex()} settingsIdx={inv.uiParentOfPickupPlayerNameAndSettings[i].playerIndex} settings={PathOf(inv.uiParentOfPickupPlayerNameAndSettings[i].transform)}");
                break;
            }
            case "needed":
                HarmonyLib.AccessTools.Field(HarmonyLib.AccessTools.TypeByName("ADVRMorePlayers.UiExtend"), "TestNeeded").SetValue(null, int.Parse(arg));
                Out("NEEDED " + arg);
                break;
            case "locks": // refresh every insight button (mod extends locks), then force the lock rows visible
            {
                var shop = UnityEngine.Object.FindAnyObjectByType<ProgressShopInsight>(FindObjectsInactive.Include);
                int k = 0;
                foreach (var item in UnityEngine.Object.FindObjectsByType<ProgressItemUI>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (item.progressRepresentation == null || item.playerLockImages == null || item.playerLockImages.Length == 0) continue;
                    item.UpdateInfosInsight(shop, item.progressRepresentation);
                    item.multiplayerUnlockProgress.SetActive(true);
                    for (int i = 0; i < item.playerLockImages.Length; i++)
                    {
                        item.playerLockImages[i].gameObject.SetActive(true);
                        item.playerLockImages[i].sprite = (i % 2 == 0) ? item.lockOpen : item.lockClosed;
                    }
                    if (k++ == 0)
                    {
                        var row = (RectTransform)item.playerLockImages[0].transform.parent;
                        var btn = (RectTransform)item.transform;
                        Out($"LOCKS n={item.playerLockImages.Length} row={PathOf(row)} rowScale={row.localScale.x:F3} rowWidthWorld={row.rect.width * row.lossyScale.x:F4} buttonWidthWorld={btn.rect.width * btn.lossyScale.x:F4}");
                    }
                }
                Out("LOCKS updated " + k);
                break;
            }
            case "lookat": // lookat <GameObject name> [distance]: render a temp camera facing it
            {
                var a = arg.Split(' ');
                var go = Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g.name == a[0] && g.scene.IsValid() && g.activeInHierarchy);
                if (go == null) { Out("NOGO " + a[0]); break; }
                float dist = a.Length > 1 ? float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture) : 2f;
                var camGo = new GameObject("BridgeCam");
                var cam = camGo.AddComponent<Camera>();
                cam.nearClipPlane = 0.01f;
                var t = go.transform;
                bool flip = a.Length > 2 && a[2] == "flip";
                Vector3 fwd = flip ? -t.forward : t.forward;
                camGo.transform.position = t.position - fwd * dist;
                camGo.transform.rotation = Quaternion.LookRotation(fwd, t.up);
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.gray;
                cam.cullingMask = ~0;
                var rt = new RenderTexture(1280, 720, 24);
                cam.targetTexture = rt; cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); tex.Apply();
                RenderTexture.active = null; cam.targetTexture = null;
                File.WriteAllBytes(Path.Combine(Paths.BepInExRootPath, "look_" + a[0] + (flip ? "_flip" : "") + ".png"), ImageConversion.EncodeToPNG(tex));
                UnityEngine.Object.Destroy(camGo); rt.Release();
                Out("LOOK " + PathOf(t));
                break;
            }
            case "bringup": // bringup <GameObject name> <dist> <scale>: clone it in front of the main camera
            {
                var a = arg.Split(' ');
                var go = Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g.name == a[0] && g.scene.IsValid());
                var cam = Camera.main;
                if (go == null || cam == null) { Out("NOGO " + a[0] + " cam=" + (cam != null)); break; }
                float dist = float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture);
                float sc = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                var clone = UnityEngine.Object.Instantiate(go);
                clone.name = "BridgeClone";
                clone.SetActive(true);
                clone.transform.position = cam.transform.position + cam.transform.forward * dist;
                clone.transform.rotation = cam.transform.rotation;
                clone.transform.localScale = go.transform.lossyScale * sc;
                Out($"BRINGUP {PathOf(go.transform)} at {clone.transform.position} cam={PathOf(cam.transform)}");
                break;
            }
            case "destroyclone":
                foreach (var g in Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.name == "BridgeClone" && g.scene.IsValid())) UnityEngine.Object.Destroy(g);
                Out("DESTROYED clones");
                break;
            case "setactive": // setactive <name> <true|false>
            {
                var a = arg.Split(' ');
                foreach (var g in Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.name == a[0] && g.scene.IsValid()))
                { g.SetActive(a[1] == "true"); Out($"SETACTIVE {PathOf(g.transform)} {a[1]}"); }
                break;
            }
            case "comps": // comps <name>: component types and layout settings
            {
                foreach (var g in Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.name == arg && g.scene.IsValid()).Take(2))
                    foreach (var c in g.GetComponents<Component>())
                    {
                        string extra = "";
                        if (c is HorizontalOrVerticalLayoutGroup lg) extra = $" spacing={lg.spacing} ctrlW={lg.childControlWidth} ctrlH={lg.childControlHeight} expW={lg.childForceExpandWidth} expH={lg.childForceExpandHeight} align={lg.childAlignment} pad={lg.padding}";
                        if (c is Behaviour bh) extra += $" enabled={bh.enabled}";
                        if (c is RectTransform r) extra = $" rect={r.rect} scale={r.localScale}";
                        Out($"COMP {PathOf(g.transform)} {c.GetType().FullName}{extra}");
                    }
                break;
            }
            case "ring":
            {
                var food = new GameObject("t").AddComponent<SpawnFoodAfterBoss>();
                for (int n = 4; n <= 8; n += 4)
                    for (int i = 0; i < n; i++)
                        Out($"RING n={n} i={i} pedestal={PodestChooseOneForEveryPlayer.CalculatePositionOffset(i, n)} food={food.CalculatePositionOffset(i, n)}");
                UnityEngine.Object.Destroy(food.gameObject);
                break;
            }
            case "badges": // fake 8 pickers on the first weapon button
            {
                var btn = UnityEngine.Object.FindObjectsByType<RunPreparationUI_ButtonSelectWeapon>(FindObjectsInactive.Include, FindObjectsSortMode.None).First();
                btn.ClearPlayersThatSelected();
                for (int i = 0; i < 8; i++)
                    btn.AddPlayerThatSelected(new NetworkHandler.SpawnedPlayerInfo { spawnedPlayerId = Fusion.PlayerRef.FromEncoded(100 + i), playerController = GameHandler.INSTANCE.playerController });
                var names = btn.playersThatSelectedWeapon;
                Out($"BADGES slots={names.Length} active={names.Count(n => n.gameObject.activeSelf)} columnScale={names[0].transform.parent.localScale.x:F2} path={PathOf(names[names.Length - 1].transform)}");
                btn.ClearPlayersThatSelected();
                Out($"BADGES cleared active={names.Count(n => n.gameObject.activeSelf)} columnScale={names[0].transform.parent.localScale.x:F2}");
                break;
            }
            case "preptest": // host in a room: fill the 4-slot list with fakes, overflow 2 more, set a weapon
            {
                var p = UnityEngine.Object.FindObjectsByType<UIPrepareBase>(FindObjectsSortMode.None).FirstOrDefault(x => x.Object != null && x.HasStateAuthority);
                if (p == null) { Out("PREP none with authority"); break; }
                var t = HarmonyLib.AccessTools.TypeByName("ADVRMorePlayers.PreparationPatches");
                var safeAdd = HarmonyLib.AccessTools.Method(t, "SafeAdd");
                // Fill free vanilla slots with the real host so only fakes overflow (fakes in the vanilla list make vanilla UI throw).
                for (int i = 0; i < 5; i++) safeAdd.Invoke(null, new object[] { p, Fusion.PlayerRef.FromEncoded(100 + i) });
                var ofField = HarmonyLib.AccessTools.Field(t, "Overflow");
                Out("PREP overflow tables=" + ((System.Collections.IDictionary)ofField.GetValue(null)).Count);
                try { HarmonyLib.AccessTools.Method(t, "RpcUpdateRunInformationAll_Postfix").Invoke(null, new object[] { p, "bowsword", Fusion.PlayerRef.FromEncoded(104) }); }
                catch (Exception e) { Out("PREP (expected vanilla UI error from fake list entries) " + e.InnerException?.GetType().Name); }
                Out($"PREP {p.GetType().Name} list={p.NetworkedPreparationList.Count} panel={HarmonyLib.AccessTools.Method(t, "PanelId").Invoke(null, new object[] { p })}");
                break;
            }
            case "props":
            {
                var r = GameHandler.INSTANCE.networkHandler.GetNetworkRunner();
                var props = r?.SessionInfo?.Properties;
                if (props == null) { Out("PROPS none"); break; }
                foreach (var kv in props) Out($"PROP {kv.Key} = {kv.Value?.PropertyValue}");
                break;
            }
            case "formulas":
            {
                var safe = HarmonyLib.AccessTools.TypeByName("ADVRMorePlayers.Safe");
                var sh = HarmonyLib.AccessTools.Method(safe, "ShortTableAt");
                var it = HarmonyLib.AccessTools.Method(safe, "IntTableAt");
                var sat = HarmonyLib.AccessTools.Method(safe, "SatShort");
                var hp = new short[] { 1000, 1600, 2200, 2800 };
                var spikes = new int[] { 4, 6, 8, 10 };
                for (int n = 1; n <= 8; n++)
                    Out($"F n={n} bossHP={sh.Invoke(null, new object[] { hp, n - 1 })} spikes={it.Invoke(null, new object[] { spikes, n - 1 })}");
                Out($"F sat(5000*8)={sat.Invoke(null, new object[] { 40000.0 })} sat(-40000)={sat.Invoke(null, new object[] { -40000.0 })} sat(1234.9)={sat.Invoke(null, new object[] { 1234.9 })}");
                break;
            }
            case "sanitize":
                Out("SANITIZE 6 -> " + new GameObject().AddComponent<UIMultiplayerConnectDialog_ButtonJoinPublicRoom>().SanitizePlayerCount(6));
                break;
            case "shot": // shot <name>.png -> BepInEx/<name>.png
                ScreenCapture.CaptureScreenshot(Path.Combine(Paths.BepInExRootPath, arg));
                Out("SHOT " + arg);
                break;
            case "active": // log whether a named GameObject exists/active
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.name == arg && g.scene.IsValid()))
                    Out($"GO {PathOf(go.transform)} activeInHierarchy={go.activeInHierarchy}");
                break;
            default: Out("UNKNOWN " + line); break;
        }
    }
}
