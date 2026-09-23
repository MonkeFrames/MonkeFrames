using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.UI;
using MonkeFrames.Editor.Utilities;
using UnityEngine;

namespace MonkeFrames.Editor.Components;

public class UIManager : MonoBehaviour
{
    public static UIManager Instance;

    public const float MenuBarHeight = 30f;
    private const int DropdownWindowID = 0x4D46;
    private const float DropdownTime = 0.18f;

    public int Selection = -1;

    /// <summary>Status message. Setting it pops up an animated toast at the bottom of the screen.</summary>
    public string Status
    {
        get => _status;
        set
        {
            // May be set from background threads (builds), so the toast is timestamped on the next draw.
            _status = value ?? "";
            _statusPending = true;
        }
    }
    private string _status = "";
    private float _statusTime = -100f;
    private volatile bool _statusPending;

    public Texture2D Icon;

    public GUIStyle CenterText => Theme.LabelCenter ?? GUI.skin.label;

    public List<IEditorMenuManager> Menus = [];
    public List<IEditorWindowManager> Windows = [];

    /// <summary>Index (IEditorMenu.Index) of the open menu, or -1.</summary>
    public int CurrentMenuIndex = -1;

    /// <summary>Window id that last received a click (used to highlight its title bar).</summary>
    public int FocusedWindow = -1;

    public bool Drawing;

    private IEditorMenuManager _dropdownMenu;
    private float _dropdownProgress;
    private Rect _dropdownRect;

    public UIManager()
    {
        Instance = this;
    }

    public void Start()
    {
        Console.WriteLine("[MonkeFrames::UIManager] Loading titlebar icon");

        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon");
        using MemoryStream data = new MemoryStream();

        stream.CopyTo(data);
        Icon = UnityUtilities.CreateTexture(data.ToArray());

        // Intro logo (optional: the editor works fine without it)
        try
        {
            using Stream introStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("introLogo");
            if (introStream != null)
            {
                using MemoryStream introData = new MemoryStream();
                introStream.CopyTo(introData);
                IntroScreen.Logo = UnityUtilities.CreateTexture(introData.ToArray());
                IntroScreen.Logo.filterMode = FilterMode.Bilinear;
                IntroScreen.Logo.wrapMode = TextureWrapMode.Clamp;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::UIManager] Could not load intro logo: {ex.Message}");
        }

        Console.WriteLine("[MonkeFrames::UIManager] Initializing managers...");

        List<Type> windowTypes = Assembly.GetExecutingAssembly().GetLoadableTypes()
            .Where(t => typeof(IEditorWindow).IsAssignableFrom(t) && t.IsClass).ToList();
        List<Type> menuTypes = Assembly.GetExecutingAssembly().GetLoadableTypes()
            .Where(t => typeof(IEditorMenu).IsAssignableFrom(t) && t.IsClass).ToList();

        foreach (Type windowType in windowTypes)
        {
            if (Activator.CreateInstance(windowType) is not IEditorWindow window)
                continue;

            Windows.Add(new IEditorWindowManager(window));
        }

        foreach (Type menuType in menuTypes)
        {
            if (Activator.CreateInstance(menuType) is not IEditorMenu menu)
                continue;

            Menus.Add(new IEditorMenuManager(menu));
        }

        Menus = Menus.OrderBy(m => m.Menu.Index).ToList();

        Console.WriteLine("[MonkeFrames::UIManager] UI manager is running");

        Plugin.OnMonkeFramesLoaded.Invoke();
    }

    /// <summary>True when the mouse is over the menu bar, an open dropdown or any open MonkeFrames window.</summary>
    public bool IsPointerOverUI
    {
        get
        {
            if (!Drawing || UnityEngine.InputSystem.Mouse.current == null)
                return false;

            Vector2 p = UnityEngine.InputSystem.Mouse.current.position.ReadValue();
            Vector2 gui = new Vector2(p.x, Screen.height - p.y);

            if (gui.y <= MenuBarHeight || _dropdownRect.Contains(gui))
                return true;

            foreach (IEditorWindowManager w in Windows)
                if (w.Visible && w.WindowPosition.Contains(gui))
                    return true;

            return false;
        }
    }

    public void OpenWindow(string menuName)
    {
        Windows.First(w => w.Window.Name == menuName).Visible = true;
        Console.WriteLine($"[MonkeFrames::UIManager] {menuName}.Visible = true;");
    }

    public void CloseWindow(string menuName)
    {
        Windows.First(w => w.Window.Name == menuName).Visible = false;
        Console.WriteLine($"[MonkeFrames::UIManager] {menuName}.Visible = false;");
    }

    public void ToggleWindow(string menuName)
    {
        var w = Windows.FirstOrDefault(w => w.Window.Name == menuName);
        w?.Visible = !w.Visible;
        Console.WriteLine($"[MonkeFrames::UIManager] {menuName}.Visible = {w?.Visible ?? false};");
    }

    public void Update()
    {
        float dt = Time.unscaledDeltaTime;

        foreach (IEditorWindowManager window in Windows)
            window.Tick(dt);

        float target = CurrentMenuIndex != -1 ? 1f : 0f;
        _dropdownProgress = Mathf.MoveTowards(_dropdownProgress, target, dt * Anim.Speed / DropdownTime);
    }

    public void OnGUI()
    {
        if (!Drawing)
            return;

        GUISkin prevSkin = GUI.skin;
        GUI.skin = Theme.Skin;
        GUI.backgroundColor = Color.white;
        GUI.color = Color.white;

        // Clicking anywhere (or pressing Escape) drops text-field focus so keyboard shortcuts work again.
        // A text field that was clicked grabs focus straight back while it processes this same event.
        if (Event.current.rawType == EventType.MouseDown
            || (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape))
            GUIUtility.keyboardControl = 0;

        if (Settings.current != null && !Settings.current.ShowIntro && IntroScreen.Active)
            IntroScreen.Skip();

        // While the intro is playing, show only the intro. Once it starts its exit,
        // the editor draws underneath so it's revealed as the intro fades away.
        if (IntroScreen.Active && !IntroScreen.Exiting)
        {
            IntroScreen.Draw();
            GUI.skin = prevSkin;
            return;
        }

        CloseMenuOnOutsideClick();
        DrawMenuBar();

        foreach (IEditorWindowManager window in Windows)
            window.Draw();

        DrawDropdown();
        DrawStatus();
        IntroScreen.Draw();

        GUI.skin = prevSkin;
    }

    // ---------------- Menu bar ----------------

    private void DrawMenuBar()
    {
        Rect bar = new Rect(0, 0, Screen.width, MenuBarHeight);
        Theme.Fill(bar, Theme.Background, 0);
        Theme.Fill(new Rect(0, MenuBarHeight - 1, Screen.width, 1), Theme.Border, 0);

        GUI.DrawTexture(new Rect(10, 7, 16, 16), Icon);

        float x = 34;
        foreach (IEditorMenuManager menu in Menus)
        {
            float w = Theme.Label.CalcSize(new GUIContent(menu.Menu.Name)).x + 22;
            Rect r = new Rect(x, 4, w, MenuBarHeight - 8);
            menu.BarRect = r;

            bool open = CurrentMenuIndex == menu.Menu.Index;

            // While a menu is open, hovering another title switches to it (like a desktop menu bar).
            if (CurrentMenuIndex != -1 && !open && r.Contains(Event.current.mousePosition))
            {
                OpenMenu(menu, fromSwitch: true);
                open = true;
            }

            if (Widgets.Ghost("bar." + menu.Menu.Name, r, menu.Menu.Name, Theme.LabelCenter, open))
            {
                if (open)
                    CurrentMenuIndex = -1;
                else
                    OpenMenu(menu, fromSwitch: false);
            }

            // Accent underline for the open menu
            float u = Anim.To("bar.u." + menu.Menu.Name, open ? 1f : 0f, 18f);
            if (u > 0.01f)
            {
                float uw = (r.width - 16) * Anim.OutCubic(u);
                Theme.Fill(new Rect(r.center.x - uw / 2f, MenuBarHeight - 3, uw, 2), Theme.Accent, 1);
            }

            x += w + 2;
        }

        // Smooth-look indicator (Caps Lock)
        float look = Anim.To("bar.smoothlook", Settings.current?.SmoothMouseLook == true ? 1f : 0f, 14f);
        if (look > 0.01f)
        {
            Color prevC = GUI.color;
            GUI.color = new Color(1, 1, 1, prevC.a * look);
            Rect chip = new Rect(Screen.width - 560 + (1f - look) * 12f, 6, 128, MenuBarHeight - 12);
            Theme.Fill(chip, Theme.Accent.WithAlpha(0.22f), 9);
            float pulse = 0.65f + 0.35f * Mathf.Sin(Time.unscaledTime * 3f);
            Theme.Dot(new Vector2(chip.x + 12, chip.center.y), 3.5f, Theme.Accent.WithAlpha(pulse));
            Theme.DrawText(new Rect(chip.x + 20, chip.y, chip.width - 22, chip.height), "SMOOTH LOOK", Theme.LabelCenterSmall, Theme.Text);
            GUI.color = prevC;
        }

        // Right side: project info + compile indicator
        var project = KeyframeManager.Instance?.Project;
        if (project != null)
        {
            string info = $"{project.Name}  ·  {project.Keyframes.Count} keyframes  ·  {project.FPS} fps";
            Rect infoRect = new Rect(Screen.width - 420, 0, 410, MenuBarHeight);
            Theme.DrawText(infoRect, info, Theme.MutedRight, Theme.TextMuted);

            if (KeyframeManager.Instance.IsCompiling)
            {
                float tw = Theme.Muted.CalcSize(new GUIContent(info)).x;
                Widgets.Spinner(new Vector2(infoRect.xMax - tw - 16, MenuBarHeight / 2f), 5f, Theme.Accent);
            }
        }
    }

    private void OpenMenu(IEditorMenuManager menu, bool fromSwitch)
    {
        CurrentMenuIndex = menu.Menu.Index;
        _dropdownMenu = menu;

        // Replay a shortened entrance when sliding between menus.
        _dropdownProgress = fromSwitch ? Mathf.Min(_dropdownProgress, 0.45f) : 0f;
    }

    private void CloseMenuOnOutsideClick()
    {
        if (CurrentMenuIndex == -1 || Event.current.rawType != EventType.MouseDown)
            return;

        Vector2 mouse = Event.current.mousePosition;
        bool inBar = mouse.y <= MenuBarHeight;
        bool inDropdown = _dropdownRect.Contains(mouse);

        if (!inBar && !inDropdown)
            CurrentMenuIndex = -1;
    }

    private void DrawDropdown()
    {
        if (_dropdownMenu == null || _dropdownProgress <= 0f)
        {
            _dropdownRect = Rect.zero;
            return;
        }

        float e = Anim.OutCubic(_dropdownProgress);
        float height = _dropdownMenu.DropdownHeight;
        Rect bar = _dropdownMenu.BarRect;

        _dropdownRect = new Rect(bar.x, MenuBarHeight + 4, IEditorMenuManager.DropdownWidth, height);

        Rect animated = _dropdownRect;
        animated.y -= (1f - e) * 8f;

        Color prev = GUI.color;
        GUI.color = new Color(1, 1, 1, e);

        GUI.Window(DropdownWindowID, animated, DrawDropdownContents, GUIContent.none, Theme.PopupStyle);
        GUI.BringWindowToFront(DropdownWindowID);

        GUI.color = prev;
    }

    private void DrawDropdownContents(int id)
    {
        float e = Anim.OutCubic(_dropdownProgress);
        GUI.color = new Color(1, 1, 1, e);

        bool interactive = CurrentMenuIndex != -1;
        if (_dropdownMenu.DrawItems(_dropdownProgress, interactive))
            CurrentMenuIndex = -1;
    }

    // ---------------- Status toast ----------------

    private void DrawStatus()
    {
        if (string.IsNullOrEmpty(_status))
            return;

        if (_statusPending)
        {
            _statusPending = false;
            _statusTime = Time.unscaledTime;
            Anim.Set("status.pop", 0f);
            NotificationSound.Play();
        }

        float holdTime = 4f + _status.Length * 0.03f;
        bool visible = Time.unscaledTime - _statusTime < holdTime;
        float a = Anim.To("status.pop", visible ? 1f : 0f, visible ? 12f : 6f, 0f);

        if (a <= 0.01f)
            return;

        GUIStyle style = Theme.Label;
        float maxW = Mathf.Min(Screen.width - 40, 900);
        float textW = Mathf.Min(style.CalcSize(new GUIContent(_status)).x, maxW - 40);

        Rect r = new Rect(20, Screen.height - 54 + (1f - Anim.OutCubic(a)) * 20f, textW + 40, 34);

        Color prev = GUI.color;
        GUI.color = new Color(1, 1, 1, a);

        Theme.Fill(new Rect(r.x, r.y + 3, r.width, r.height), new Color(0, 0, 0, 0.35f), 9);
        Theme.Fill(r, Theme.Background, 8);
        Theme.Fill(new Rect(r.x + 1, r.y + 1, r.width - 2, r.height - 2), Theme.Surface, 7);
        Theme.Dot(new Vector2(r.x + 16, r.center.y), 4f, Theme.Accent);
        Theme.DrawText(new Rect(r.x + 28, r.y, r.width - 34, r.height), _status, style, Theme.Text);

        // Remaining time bar
        float remaining = 1f - Mathf.Clamp01((Time.unscaledTime - _statusTime) / holdTime);
        Theme.Fill(new Rect(r.x + 8, r.yMax - 3, (r.width - 16) * remaining, 2), Theme.Accent.WithAlpha(0.6f), 1);

        GUI.color = prev;
    }
}
