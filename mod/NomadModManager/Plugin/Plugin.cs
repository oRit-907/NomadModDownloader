using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
#if BEPINEX6
using BepInEx.Unity.Mono;
#endif
using EvilCore.UI.MainMenu.Panels;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[assembly: System.Reflection.AssemblyMetadata("NomadHotLoad", "false")]

namespace NomadModManager
{
    [BepInPlugin(Guid, "Nomad Mod Manager", Version)]
    public class ModManagerPlugin : BaseUnityPlugin
    {
        public const string Guid = "akorit.nomadmodmanager";
        public const string Version = "1.0.4";

        internal static ModManagerPlugin I;
        internal static ManualLogSource Log;

        internal ModCatalog Catalog;
        internal ModsMenu Menu;
        internal ConfigEntry<bool> AutoStart;
        internal ConfigEntry<float> Scale;

        /// <summary>The game's main menu panel, while it exists.</summary>
        internal MainMenuPanel MainMenu;
        internal bool AtMainMenu => MainMenu != null && MainMenu.isActiveAndEnabled;

        private void Awake()
        {
            I = this;
            Log = Logger;
            AutoStart = Config.Bind("General", "StartNewModsAutomatically", true,
                "Start newly installed mods right away when they can run without a restart (only at the main menu).");
            Scale = Config.Bind("General", "MenuScale", 1f, "Size of the Mods menu (1 = 1080p size).");

            Catalog = new ModCatalog();
            Catalog.SnapshotStartup();
            Catalog.AutoStart = AutoStart.Value;
            Menu = new ModsMenu(this, Catalog);
            Catalog.Toast += Menu.Toast;

            try { new Harmony(Guid).PatchAll(typeof(MainMenuStartPatch)); }
            catch (Exception e) { Logger.LogError("Couldn't hook the main menu: " + e); }

            try { Directory.CreateDirectory(Pending.Dir(Paths.GameRootPath)); } catch { }
            if (!File.Exists(Path.Combine(Paths.PatcherPluginPath, "NomadModManager.Patcher.dll")))
                Logger.LogWarning("NomadModManager.Patcher.dll is missing from BepInEx\\patchers — queued changes won't be applied at launch.");

            StartCoroutine(Poll());
            Logger.LogInfo("Nomad Mod Manager loaded. Open it with the Mods button on the main menu.");
        }

        private IEnumerator Poll()
        {
            yield return new WaitForSecondsRealtime(1f);
            while (true)
            {
                try
                {
                    Catalog.AutoStart = AutoStart.Value;
                    Catalog.Refresh();
                    // New mods start only at the main menu: nothing of the world exists yet, so they set up like at launch.
                    if (AtMainMenu) Catalog.StartNewMods();
                    MenuButton.UpdateBadge(Catalog);
                }
                catch (Exception e) { Logger.LogWarning("Mod scan: " + e.Message); }
                yield return new WaitForSecondsRealtime(Menu.IsOpen ? 1f : 2f);
            }
        }

        private void Update()
        {
            if (AtMainMenu) MenuButton.KeepLayout();
            if (!Menu.IsOpen) return;
            if (!AtMainMenu) { Menu.Close(); return; }
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null && kb.escapeKey.wasPressedThisFrame) Menu.Close();
            }
            catch { }
        }

        private void OnGUI()
        {
            try { Menu.Draw(); }
            catch (Exception e) { Logger.LogWarning("Mods menu draw: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(MainMenuPanel), "Start")]
    internal static class MainMenuStartPatch
    {
        private static void Postfix(MainMenuPanel __instance)
        {
            ModManagerPlugin.I.MainMenu = __instance;
            try { MenuButton.Add(__instance); }
            catch (Exception e) { ModManagerPlugin.Log.LogError("Couldn't add the Mods button: " + e); }
        }
    }

    /// <summary>A "Mods" button cloned from the game's own Settings button, so it matches the menu exactly.</summary>
    internal static class MenuButton
    {
        private const string ObjectName = "NomadModManager_ModsButton";
        private static TMP_Text _label;
        private static string _base = "Mods";
        private static string _shown;

        public static void Add(MainMenuPanel panel)
        {
            var source = Traverse.Create(panel).Field("settingsButton").GetValue<Button>()
                         ?? Traverse.Create(panel).Field("quitButton").GetValue<Button>();
            if (source == null) { ModManagerPlugin.Log.LogWarning("Main menu has no Settings button to copy."); return; }
            var parent = source.transform.parent;
            if (parent.Find(ObjectName) != null) return;

            var go = UnityEngine.Object.Instantiate(source.gameObject, parent, false);
            go.name = ObjectName;
            go.transform.SetSiblingIndex(source.transform.GetSiblingIndex()); // just above Settings

            // The copied label is localized: drop that so it keeps our text.
            foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (c != null && c.GetType().Name.IndexOf("Localiz", StringComparison.OrdinalIgnoreCase) >= 0)
                    UnityEngine.Object.DestroyImmediate(c);

            _label = go.GetComponentInChildren<TMP_Text>(true);
            var original = source.GetComponentInChildren<TMP_Text>(true)?.text ?? "";
            _base = original.Length > 0 && original == original.ToUpperInvariant() ? "MODS" : "Mods";
            _shown = null;
            UpdateBadge(ModManagerPlugin.I.Catalog);

            // The menu finishes laying itself out after Start, so make room a few frames later.
            go.SetActive(false);
            ModManagerPlugin.I.StartCoroutine(MakeRoomLater(panel, source, go));

            var button = go.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() => ModManagerPlugin.I.Menu.Open(panel));
            button.interactable = true;
            ModManagerPlugin.Log.LogInfo("Added the Mods button to the main menu.");
        }

        /// <summary>Where each moved button should be, and where the game first put it (to spot the game resetting it).</summary>
        private static readonly List<(RectTransform Rt, Vector3 Original, Vector3 Target)> Moved = new List<(RectTransform, Vector3, Vector3)>();

        /// <summary>
        /// The menu places its buttons by position, not with a layout group, so a new button would sit on top of Settings.
        /// Puts Mods where Settings was and moves Settings and every button under it down one row.
        /// </summary>
        private static System.Collections.IEnumerator MakeRoomLater(MainMenuPanel panel, Button settings, GameObject mods)
        {
            for (var i = 0; i < 3; i++) yield return null;
            yield return new WaitForEndOfFrame();
            if (panel == null || mods == null) yield break;
            try { Canvas.ForceUpdateCanvases(); MakeRoom(panel, settings, mods); }
            catch (Exception e) { ModManagerPlugin.Log.LogError("Couldn't make room for the Mods button: " + e); }
            mods.SetActive(true);
        }

        private static void MakeRoom(MainMenuPanel panel, Button settings, GameObject mods)
        {
            Moved.Clear();
            var parent = settings.transform.parent;
            if (parent.GetComponent<LayoutGroup>() != null) return; // a layout group already makes room

            var setRt = (RectTransform)settings.transform;
            var setY = CenterY(setRt);

            // The menu's own buttons, plus anything else clickable lined up under them.
            var tr = Traverse.Create(panel);
            var known = new[] { "singlePlayerButton", "multiplayerButton", "joinButton", "loadGameButton", "settingsButton", "reportProblemButton", "quitButton" }
                .Select(f => tr.Field(f).GetValue<Button>()).Where(b => b != null).ToList();
            float leftEdge = float.MaxValue, rightEdge = float.MinValue; // (Enumerable.Min/Max on floats are stripped from the game)
            foreach (var b in known)
            {
                var c = Corners((RectTransform)b.transform);
                leftEdge = Mathf.Min(leftEdge, c[0].x);
                rightEdge = Mathf.Max(rightEdge, c[2].x);
            }
            var extra = panel.GetComponentsInChildren<Button>(false)
                .Where(b => !known.Contains(b) && b.gameObject != mods)
                .Where(b => { var c = Corners((RectTransform)b.transform); return c[2].x > leftEdge && c[0].x < rightEdge; });
            foreach (var b in known.Concat(extra))
            {
                var rt = (RectTransform)b.transform;
                ModManagerPlugin.Log.LogInfo($"  menu item {b.name}: y {CenterY(rt):0}, parent {rt.parent?.name}{(rt.parent?.GetComponent<LayoutGroup>() != null ? " (layout)" : "")}, active {b.gameObject.activeInHierarchy}");
            }
            var column = known.Concat(extra).Where(b => b != settings && b.gameObject != mods).Select(b => (RectTransform)b.transform).ToList();

            // Row height: the gap to the closest button above Settings (or below, if there's none above).
            var above = column.Where(rt => CenterY(rt) > setY + 1f).OrderBy(CenterY).FirstOrDefault();
            var below = column.Where(rt => CenterY(rt) < setY - 1f).OrderByDescending(CenterY).FirstOrDefault();
            float step;
            if (above != null) step = CenterY(above) - setY;
            else if (below != null) step = setY - CenterY(below);
            else step = setRt.rect.height * setRt.lossyScale.y * 1.1f;

            ((RectTransform)mods.transform).position = setRt.position;

            // Move Settings and everything under it. A button inside its own layout container is moved by its container.
            var toMove = new List<RectTransform>();
            foreach (var rt in column.Where(rt => CenterY(rt) < setY - 1f).Concat(new[] { setRt }))
            {
                var target = rt;
                var p = rt.parent as RectTransform;
                if (p != null && p != parent && p.GetComponent<LayoutGroup>() != null) target = p;
                if (!toMove.Contains(target)) toMove.Add(target);
            }
            foreach (var rt in toMove)
            {
                var original = rt.localPosition;
                rt.position = rt.position - new Vector3(0, step, 0);
                Moved.Add((rt, original, rt.localPosition));
            }
            ModManagerPlugin.Log.LogInfo($"Made room for Mods: moved {toMove.Count} item(s) down {step:0} px.");
        }

        private static Vector3[] Corners(RectTransform rt)
        {
            var c = new Vector3[4];
            rt.GetWorldCorners(c);
            return c;
        }

        private static float CenterY(RectTransform rt)
        {
            var c = Corners(rt);
            return (c[0].y + c[1].y) / 2f;
        }

        /// <summary>If the game puts a moved button back (e.g. when the menu is shown again), move it again.</summary>
        public static void KeepLayout()
        {
            foreach (var (rt, original, target) in Moved)
                if (rt != null && (rt.localPosition - original).sqrMagnitude < 0.01f) rt.localPosition = target;
        }

        public static void UpdateBadge(ModCatalog catalog)
        {
            if (_label == null) return;
            var n = catalog.UpdateCount;
            var text = n > 0 ? $"{_base} ({n})" : _base;
            if (text == _shown) return;
            _label.text = text;
            _shown = text;
        }
    }
}
