using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SpacePeek.Interop;
using SpacePeek.Shell;
using SpacePeek.Hud;

namespace SpacePeek;

internal sealed class AppController : IDisposable
{
    private const int HotkeyId = 0x5EA1;
    private const int SecondaryHotkeyId = 0x5EA3;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint VK_F11 = 0x7A;

    /// <summary>Synthetic keys are ignored by default (they would let automation drive the HUD);
    /// set SPACEPEEK_ALLOW_INJECTED=1 to exercise the hook from tests.</summary>
    private static readonly bool AllowInjected =
        Environment.GetEnvironmentVariable("SPACEPEEK_ALLOW_INJECTED") == "1";

    private readonly HudWindow _hud = new();
    private readonly ExplorerGate _gate = new();
    private readonly System.Windows.Forms.NotifyIcon _tray;
    private HwndSource? _messageWindow;
    private IntPtr _keyboardHook = IntPtr.Zero;
    private NativeMethods.LowLevelKeyboardProc? _hookProc;
    private bool _visible;
    private bool _spaceDown;
    private bool _ateSpace;
    private Settings _settings = new();
    private SettingsWindow? _settingsWindow;
    private System.Windows.Forms.ToolStripMenuItem? _startupItem;

    public AppController()
    {
        _settings = Settings.Load();

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = AppIcon.LoadDrawingIcon() ?? System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "快速预览",
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("设置…", null, (_, _) => ShowSettings());
        _startupItem = new System.Windows.Forms.ToolStripMenuItem("开机自动启动", null,
            (_, _) => ToggleStartup()) { Checked = Settings.IsStartupEnabled() };
        menu.Items.Add(_startupItem);
        menu.Items.Add("清空预览缓存", null, (_, _) => Settings.ClearCache());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("关于 快速预览", null, (_, _) => ShowAbout());
        menu.Items.Add("退出", null, (_, _) => Application.Current.Shutdown());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowSettings();
    }

    private void ShowSettings()
    {
        try
        {
            if (_settingsWindow is { IsVisible: true })
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow(_settings, SettingsChanged);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }
        catch (Exception ex) { Log.Exception("settings-window", ex); }
    }

    private void SettingsChanged()
    {
        _settings = Settings.Load();
        _settings.StartWithWindows = Settings.IsStartupEnabled();
        _settings.Save();
        ApplyTriggerMode();
        if (_startupItem is not null) _startupItem.Checked = Settings.IsStartupEnabled();
    }

    private void ToggleStartup()
    {
        bool enable = !Settings.IsStartupEnabled();
        Settings.SetStartup(enable);
        if (_startupItem is not null) _startupItem.Checked = Settings.IsStartupEnabled();
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            "快速预览 (SpacePeek)\n\n选中文件或文件夹后按空格即可预览，再按一次关闭。\n" +
            "支持：图片、视频首帧、PDF、Word/Excel/PPT、Markdown、HTML、EPUB、压缩包、文本与代码。",
            "关于 快速预览", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ApplyTriggerMode()
    {
        if (_settings.TriggerMode == TriggerMode.Space)
        {
            InstallKeyboardHook();
        }
        else
        {
            RemoveKeyboardHook();
        }
    }

    public void Start()
    {
        var parameters = new HwndSourceParameters("SpacePeek.MessageWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x00000080, // WS_EX_TOOLWINDOW
        };
        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(WndProc);

        bool primary = NativeMethods.RegisterHotKey(_messageWindow.Handle, HotkeyId, MOD_CONTROL | MOD_SHIFT, NativeMethods.VK_SPACE);
        bool secondary = NativeMethods.RegisterHotKey(_messageWindow.Handle, SecondaryHotkeyId, MOD_CONTROL, NativeMethods.VK_SPACE);

        Log.Write($"start: Ctrl+Shift+Space registered={primary}, Ctrl+Space registered={secondary}");
        _tray.Text = "快速预览";

        _hud.Closed += (_, _) => _visible = false;
        ApplyTriggerMode();
        WarmUp();
    }

    /// <summary>
    /// Pays the one-time costs before the first keypress: COM/IDispatch, the UIA client,
    /// and WPF's first Show() (which builds the HWND and initialises the render stack).
    /// </summary>
    private void WarmUp()
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                Type? t = Type.GetTypeFromProgID("Shell.Application");
                if (t is not null) { dynamic s = Activator.CreateInstance(t)!; _ = s.Windows(); }
            }
            catch { }
            try { _ = System.Windows.Automation.AutomationElement.RootElement; } catch { }
        });

        _hud.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                _hud.Left = -20000;
                _hud.Top = -20000;
                _hud.PresentAsync(new System.Windows.Controls.TextBlock { Text = "warm-up" }, IntPtr.Zero, null);
                _hud.Hide();
                Log.Write("warmup: hud pre-shown and hidden");
            }
            catch (Exception ex) { Log.Exception("warmup", ex); }
        }));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            Log.Write($"hotkey id=0x{id:X}");
            if (id == HotkeyId || id == SecondaryHotkeyId)
            {
                handled = true;
                _ = ToggleAsync();
            }
        }
        return IntPtr.Zero;
    }

    private async Task ToggleAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (_visible)
            {
                Log.Write("toggle: hide");
                HideHud();
                return;
            }

            IntPtr foreground = NativeMethods.GetForegroundWindow();
            IntPtr explorerHwnd = ShellSelection.ResolveRoot(foreground);
            string cls = ShellSelection.ClassOf(explorerHwnd);
            bool isExplorer = ShellSelection.IsExplorerWindow(explorerHwnd, out _);
            Log.Write($"toggle: fg=0x{foreground:X} root=0x{explorerHwnd:X} class={cls} explorer={isExplorer} +{sw.ElapsedMilliseconds}ms");
            if (!isExplorer) return;

            IReadOnlyList<SelectedItem> items = ShellSelection.Read(explorerHwnd);
            if (items.Count == 0 && foreground != explorerHwnd) items = ShellSelection.Read(foreground);
            Log.Write($"toggle: items={items.Count} [{string.Join(", ", System.Linq.Enumerable.Select(items, i => i.Name))}] +{sw.ElapsedMilliseconds}ms");
            if (items.Count == 0) return;

            FrameworkElement? content = PreviewBuilder.ForSelection(items);
            if (content is null) return;

            Rect? anchor = null;
            if (items.Count == 1)
            {
                SelectedItem item = items[0];
                anchor = await Task.Run(() =>
                {
                    try { return UiaProbe.TryGetItemRect(explorerHwnd, item.Name); }
                    catch (Exception ex) { Log.Exception("uia", ex); return (Rect?)null; }
                });
                Log.Write($"toggle: anchor={(anchor.HasValue ? anchor.Value.ToString() : "null")} +{sw.ElapsedMilliseconds}ms");
            }

            await _hud.PresentAsync(content, explorerHwnd, anchor);
            _visible = true;
            Log.Write($"toggle: shown +{sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Log.Exception("toggle", ex);
        }
    }

    private void HideHud()
    {
        _hud.Hide();
        _visible = false;
        Log.Write("hud hidden");
    }

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero) return;
        _hookProc = HookProc;
        _keyboardHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL, _hookProc, NativeMethods.GetModuleHandle(null), 0);
        Log.Write($"hook: installed={_keyboardHook != IntPtr.Zero}");
    }

    private void RemoveKeyboardHook()
    {
        if (_keyboardHook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = IntPtr.Zero;
        _hookProc = null;
    }

    /// <summary>
    /// Runs on the thread that installed the hook. Must return fast: Windows removes the hook
    /// silently if the callback exceeds LowLevelHooksTimeout, so everything here is cached reads.
    /// </summary>
    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        bool down = msg == 0x0100 || msg == 0x0104;   // WM_KEYDOWN / WM_SYSKEYDOWN
        bool up = msg == 0x0101 || msg == 0x0105;     // WM_KEYUP   / WM_SYSKEYUP

        int vk = Marshal.ReadInt32(lParam);
        bool injected = (Marshal.ReadInt32(lParam, 8) & 0x10) != 0 && !AllowInjected;   // LLKHF_INJECTED

        if (vk == NativeMethods.VK_ESCAPE && down && _visible)
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(HideHud));
            return 1;
        }

        if (vk == NativeMethods.VK_SPACE && !injected)
        {
            if (down)
            {
                // Autorepeat: holding Space must not open and immediately close the HUD.
                if (_spaceDown) return 1;
                _spaceDown = true;

                if (_visible)
                {
                    _ateSpace = true;
                    Dispatcher.CurrentDispatcher.BeginInvoke(new Action(HideHud));
                    return 1;
                }

                if (_gate.Eligible)
                {
                    _ateSpace = true;
                    Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => _ = ToggleAsync()));
                    return 1;
                }
                return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }

            if (up)
            {
                _spaceDown = false;
                if (_ateSpace)
                {
                    _ateSpace = false;
                    return 1;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        RemoveKeyboardHook();
        if (_messageWindow is not null)
        {
            NativeMethods.UnregisterHotKey(_messageWindow.Handle, HotkeyId);
            NativeMethods.UnregisterHotKey(_messageWindow.Handle, SecondaryHotkeyId);
            _messageWindow.Dispose();
            _messageWindow = null;
        }
        Hud.OfficePageRenderer.Shutdown();
        _tray.Visible = false;
        _tray.Dispose();
    }
}



