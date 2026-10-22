using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace WowDualBoxAssist;

internal sealed class MainForm : Form
{
    public const string WindowTitle = "WoW Dual Box Assist";
    public const string RestartArg = "--restarted";

    private static readonly string[] ThemeChoices = { "System", "Light", "Dark" };

    /// <summary>A detected client as shown in the two pickers.</summary>
    private sealed record ClientItem(WowWindow Window)
    {
        public override string ToString() => $"{Window.Title}   (PID {Window.Pid})";
    }

    private readonly Settings _settings = Settings.Load();
    private readonly InputHooks _hooks = new();
    private readonly HotkeyListener _listener = new();
    private readonly ForegroundWatcher _foreground = new();
    private readonly bool _elevated = IsElevated;
    private Theme _theme = Theme.Light;
    private List<WowWindow> _windows = new();
    private readonly Dictionary<IntPtr, long> _firstSeen = new();   // window -> order it was first found in
    private long _seenCounter;
    private IntPtr _mainHandle;
    private IntPtr _altHandle;
    private IntPtr _lastGame;   // the game that was in front most recently, for "bring me back"
    private bool _loading;
    private bool _updatingCombos;
    private string? _alert;
    private DateTime _alertUntil;

    private readonly Label _hotkeyLabel = new();
    private readonly Button _setKeyButton = new();
    private readonly Button _clearKeyButton = new();
    private readonly Label _hotkeyInfo = new();
    private readonly CheckBox _fromAnyWindow = new();
    private readonly ComboBox _mainCombo = new();
    private readonly ComboBox _altCombo = new();
    private readonly Button _flashMain = new();
    private readonly Button _flashAlt = new();
    private readonly Button _swapRolesButton = new();
    private readonly Button _refreshButton = new();
    private readonly Label _clientsInfo = new();
    private readonly TextBox _processNamesBox = new();
    private readonly ComboBox _layoutCombo = new();
    private readonly Button _applyLayoutButton = new();
    private readonly System.Windows.Forms.Timer _layoutTimer = new() { Interval = 3000 };
    private readonly System.Windows.Forms.Timer _layoutSettleTimer = new() { Interval = 300 };
    private bool _updatingLayouts;
    private readonly ComboBox _themeCombo = new();
    private readonly Button _adminButton = new();
    private readonly Label _statusLabel = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly System.Windows.Forms.Timer _alertTimer = new() { Interval = 15000 };

    public MainForm()
    {
        BuildUi();

        _listener.Pressed = OnSwapKeyPressed;
        _listener.Captured = _ => BeginInvoke(OnHotkeyCaptured);
        _listener.CaptureCancelled = () => BeginInvoke(AfterHotkeyChange);
        _hooks.Handler = _listener.Handle;
        _foreground.Changed = OnForegroundChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _layoutTimer.Tick += (_, _) => ApplyLayout();
        _layoutSettleTimer.Tick += (_, _) => { _layoutSettleTimer.Stop(); PlaceWindows(); };
    }

    // ---------------------------------------------------------------- lifecycle

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        _loading = true;
        ApplySettingsToControls();
        _loading = false;
        _listener.Hotkey = _settings.SwapHotkey;
        ApplyTheme();

        try
        {
            _hooks.InstallKeyboard();
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(this, ex.Message, WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }
        _foreground.Start();

        // Scans once a second only while looking for the games; see UpdateSearching.
        _timer.Interval = 1000;
        _timer.Tick += (_, _) => { RefreshWindows(); UpdateStatus(); };
        _alertTimer.Tick += (_, _) => { _alertTimer.Stop(); UpdateStatus(); };

        RefreshWindows();
        UpdateSearching();
        AfterHotkeyChange();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetDarkTitleBar(Handle, _theme.IsDark);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _timer.Stop();
        _layoutTimer.Stop();
        _layoutSettleTimer.Stop();
        _alertTimer.Stop();
        _hooks.Dispose();
        _foreground.Dispose();
        _settings.Save();
        base.OnFormClosing(e);
    }

    // ---------------------------------------------------------------- settings <-> controls

    private void ApplySettingsToControls()
    {
        _fromAnyWindow.Checked = _settings.SwapFromAnyWindow;
        int themeIdx = Array.IndexOf(ThemeChoices, _settings.Theme);
        _themeCombo.SelectedIndex = themeIdx >= 0 ? themeIdx : 0;
        _processNamesBox.Text = _settings.ProcessNames;
        FillLayouts();
    }

    private void OnOptionChanged(object? sender, EventArgs e)
    {
        if (_loading) return;

        bool namesChanged = _settings.ProcessNames != _processNamesBox.Text;
        _settings.SwapFromAnyWindow = _fromAnyWindow.Checked;
        _settings.Theme = ThemeChoices[Math.Max(0, _themeCombo.SelectedIndex)];
        _settings.ProcessNames = _processNamesBox.Text;
        _settings.Save();
        if (namesChanged) ForceRefresh();

        ApplyTheme();
        UpdateHotkeyUi();
        UpdateStatus();
    }

    // ---------------------------------------------------------------- theme

    private void ApplyTheme()
    {
        _theme = Theme.FromSetting(_settings.Theme);
        SuspendLayout();
        _theme.Apply(this);
        ResumeLayout();
        if (IsHandleCreated) Native.SetDarkTitleBar(Handle, _theme.IsDark);
        UpdateHotkeyUi();
        UpdateStatus();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && _settings.Theme == "System")
            BeginInvoke(ApplyTheme);
    }

    // ---------------------------------------------------------------- swapping

    /// <summary>
    /// Runs inside the keyboard/mouse hook, so it only decides; the focus change itself happens
    /// right after the hook returns. Returns whether the press is kept away from every window.
    /// </summary>
    private bool OnSwapKeyPressed()
    {
        var decision = SwapLogic.Decide(Native.GetForegroundWindow(), _mainHandle, _altHandle, _lastGame, _settings.SwapFromAnyWindow);
        if (decision.Target != IntPtr.Zero) BeginInvoke(() => SwitchTo(decision.Target));
        return decision.Swallow;
    }

    private void SwitchTo(IntPtr hWnd)
    {
        if (!Native.IsWindow(hWnd))
        {
            CheckKnownWindows();
            ShowAlert("That game was closed. Looking for it again; it is picked up as soon as it is back.");
            return;
        }

        var result = WindowActivator.BringToFront(hWnd);
        if (result == SwitchResult.Failed)
        {
            _adminButton.Visible = !_elevated;   // only offered once it may actually help
            ShowAlert(_elevated
                ? "Windows would not switch to the other game that time. Press the swap key again."
                : "Windows would not switch to the other game. If this keeps happening, WoW may be running as " +
                  "administrator: use \"Restart as administrator\".");
        }
        else
        {
            _settings.SwapCount++;
            if (result == SwitchResult.Fallback) _settings.FallbackCount++;
        }
        UpdateStatus();
    }

    private void OnForegroundChanged(IntPtr hWnd)
    {
        CheckKnownWindows();   // closing a game moves focus elsewhere, so this is where a close shows up
        if (hWnd != IntPtr.Zero && (hWnd == _mainHandle || hWnd == _altHandle)) _lastGame = hWnd;
        UpdateStatus();
    }

    // ---------------------------------------------------------------- swap key

    private void OnSetKeyClicked()
    {
        if (_listener.Capturing)
        {
            _listener.CancelCapture();
            return;
        }
        _listener.BeginCapture();
        AfterHotkeyChange();
    }

    private void OnHotkeyCaptured()
    {
        _settings.SwapHotkey = _listener.Hotkey;
        _settings.Save();
        AfterHotkeyChange();
    }

    private void ClearHotkey()
    {
        _listener.CancelCapture();
        _listener.Hotkey = default;
        _settings.SwapHotkey = default;
        _settings.Save();
        AfterHotkeyChange();
    }

    private void AfterHotkeyChange()
    {
        try
        {
            _hooks.SetMouseHook(_listener.Capturing || _listener.Hotkey.Kind == HotkeyKind.Mouse);
        }
        catch (Win32Exception ex)
        {
            ShowAlert(ex.Message + " Mouse buttons can't be used right now; pick a key instead.");
        }
        UpdateHotkeyUi();
        UpdateStatus();
    }

    private void UpdateHotkeyUi()
    {
        var hotkey = _listener.Hotkey;
        bool capturing = _listener.Capturing;

        _hotkeyLabel.Text = capturing ? "Press a key or mouse button…" : hotkey.ToString();
        _hotkeyLabel.ForeColor = !capturing && !hotkey.IsSet ? _theme.MutedText : _theme.Text;
        _setKeyButton.Text = capturing ? "Cancel" : hotkey.IsSet ? "Change key…" : "Set key…";
        _setKeyButton.BackColor = capturing ? _theme.Capture : _theme.Button;
        _clearKeyButton.Enabled = hotkey.IsSet && !capturing;

        _hotkeyInfo.ForeColor = _theme.MutedText;
        if (capturing)
            _hotkeyInfo.Text = "Hold Ctrl, Alt or Shift with it for a combo. Esc cancels. Left and right click are never used.";
        else if (!hotkey.IsSet)
            _hotkeyInfo.Text = "Pick a key or mouse button you don't use in WoW. Mouse side buttons work well.";
        else if (hotkey.Caveat(_settings.SwapFromAnyWindow) is { } caveat)
        {
            _hotkeyInfo.Text = caveat;
            _hotkeyInfo.ForeColor = _theme.Alert;
        }
        else
            _hotkeyInfo.Text = "Press it in either game to jump to the other one. The game never sees this key.";
    }

    // ---------------------------------------------------------------- clients

    private void RefreshWindows()
    {
        // Windows keep the order they were first found in, so the main stays the main. Games that are
        // already open when the app starts come in z-order (the one used most recently first).
        var found = WowWindowFinder.Find(WowWindowFinder.ParseProcessNames(_settings.ProcessNames));
        foreach (var w in found) _firstSeen.TryAdd(w.Handle, _seenCounter++);
        found = found.OrderBy(w => _firstSeen[w.Handle]).ToList();

        bool sameSet = found.Count == _windows.Count &&
                       found.Select(w => w.Handle).ToHashSet().SetEquals(_windows.Select(w => w.Handle));
        _windows = found;
        if (sameSet) return;

        if (!found.Any(w => w.Handle == _mainHandle)) _mainHandle = IntPtr.Zero;
        if (!found.Any(w => w.Handle == _altHandle)) _altHandle = IntPtr.Zero;
        if (_lastGame != _mainHandle && _lastGame != _altHandle) _lastGame = IntPtr.Zero;

        var (oldMain, oldAlt) = (_mainHandle, _altHandle);

        // Default: the client found first is the main, the next one the alt.
        if (_mainHandle == IntPtr.Zero) _mainHandle = found.Select(w => w.Handle).FirstOrDefault(h => h != _altHandle);
        if (_altHandle == IntPtr.Zero) _altHandle = found.Select(w => w.Handle).FirstOrDefault(h => h != _mainHandle);

        // A game that just appeared gets put in place once it has had a moment to finish opening
        // its window; a client still starting up may move its own window once more.
        if (_mainHandle != oldMain || _altHandle != oldAlt)
        {
            _layoutTimer.Stop();
            _layoutTimer.Start();
        }

        FillCombos();
        UpdateSearching();
    }

    /// <summary>A full scan right now, from scratch (the Refresh now button).</summary>
    private void ForceRefresh()
    {
        _windows = new List<WowWindow>();
        RefreshWindows();
        FillCombos();
        UpdateSearching();
        UpdateStatus();
    }

    /// <summary>
    /// Scans once a second only while the main or the alt is missing. With both found nothing runs
    /// on a timer at all; a closed game is noticed through <see cref="CheckKnownWindows"/>.
    /// </summary>
    private void UpdateSearching()
    {
        bool searching = _mainHandle == IntPtr.Zero || _altHandle == IntPtr.Zero;
        _timer.Enabled = searching;
        int found = (_mainHandle != IntPtr.Zero ? 1 : 0) + (_altHandle != IntPtr.Zero ? 1 : 0);
        _clientsInfo.Text = found switch
        {
            0 => "Looking for your games…",
            1 => "One game found. Looking for the second one…",
            _ => "Both found. Not scanning; click Refresh now if you add a client.",
        };
    }

    /// <summary>
    /// Checks that the main and alt windows still exist (two cheap lookups, no scan) and goes back
    /// to looking for games if one was closed. Runs on focus changes and before every swap.
    /// </summary>
    private void CheckKnownWindows()
    {
        bool mainGone = _mainHandle != IntPtr.Zero && !Native.IsWindow(_mainHandle);
        bool altGone = _altHandle != IntPtr.Zero && !Native.IsWindow(_altHandle);
        if (!mainGone && !altGone) return;

        if (mainGone) _mainHandle = IntPtr.Zero;
        if (altGone) _altHandle = IntPtr.Zero;
        _windows.RemoveAll(w => !Native.IsWindow(w.Handle));
        if (_lastGame != _mainHandle && _lastGame != _altHandle) _lastGame = IntPtr.Zero;
        FillCombos();
        UpdateSearching();
        UpdateStatus();
    }

    private void FillCombos()
    {
        _updatingCombos = true;
        try
        {
            Fill(_mainCombo, _mainHandle);
            Fill(_altCombo, _altHandle);
        }
        finally
        {
            _updatingCombos = false;
        }

        _swapRolesButton.Enabled = _windows.Count >= 2;
        _flashMain.Enabled = _mainHandle != IntPtr.Zero;
        _flashAlt.Enabled = _altHandle != IntPtr.Zero;
    }

    private void Fill(ComboBox combo, IntPtr selected)
    {
        combo.BeginUpdate();
        combo.Items.Clear();
        foreach (var w in _windows) combo.Items.Add(new ClientItem(w));
        combo.SelectedIndex = _windows.FindIndex(w => w.Handle == selected);
        combo.EndUpdate();
    }

    private void OnMainPicked(object? sender, EventArgs e)
    {
        if (_updatingCombos || _mainCombo.SelectedItem is not ClientItem item) return;
        var h = item.Window.Handle;
        if (h == _altHandle) _altHandle = _mainHandle;   // picked the current alt: swap the two
        _mainHandle = h;
        AfterClientChange(h);
    }

    private void OnAltPicked(object? sender, EventArgs e)
    {
        if (_updatingCombos || _altCombo.SelectedItem is not ClientItem item) return;
        var h = item.Window.Handle;
        if (h == _mainHandle) _mainHandle = _altHandle;  // picked the current main: swap the two
        _altHandle = h;
        AfterClientChange(h);
    }

    private void SwapRoles()
    {
        (_mainHandle, _altHandle) = (_altHandle, _mainHandle);
        AfterClientChange(_mainHandle);
    }

    private void AfterClientChange(IntPtr flash)
    {
        BeginInvoke(FillCombos);   // outside the combo's own event so the list can be rebuilt safely
        ApplyLayout();             // the roles changed, so the windows trade places
        UpdateStatus();
        if (flash != IntPtr.Zero) Native.Flash(flash, 4);
    }

    // ---------------------------------------------------------------- window layout

    private sealed record LayoutItem(WindowLayout Layout, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>The saved layout, or both on the main monitor while the monitor it needs is missing.</summary>
    private WindowLayout EffectiveLayout(IReadOnlyList<MonitorInfo> monitors) =>
        Layouts.Available(monitors).Contains(_settings.Layout) ? _settings.Layout : WindowLayout.BothOnMain;

    /// <summary>Lists the layouts the connected monitors allow; reruns when monitors change.</summary>
    private void FillLayouts()
    {
        var monitors = Layouts.CurrentMonitors();
        var effective = EffectiveLayout(monitors);
        _updatingLayouts = true;
        try
        {
            _layoutCombo.BeginUpdate();
            _layoutCombo.Items.Clear();
            foreach (var layout in Layouts.Available(monitors))
                _layoutCombo.Items.Add(new LayoutItem(layout, Layouts.Describe(layout, monitors)));
            _layoutCombo.SelectedIndex = Layouts.Available(monitors).ToList().IndexOf(effective);
            _layoutCombo.EndUpdate();
        }
        finally
        {
            _updatingLayouts = false;
        }
    }

    private void OnLayoutPicked(object? sender, EventArgs e)
    {
        if (_updatingLayouts || _layoutCombo.SelectedItem is not LayoutItem item) return;
        bool wasQuarter = _settings.Layout is WindowLayout.AltCornerLeft or WindowLayout.AltCornerRight;
        _settings.Layout = item.Layout;
        _settings.Save();
        ApplyLayout();

        // WoW stays at the quarter's resolution after growing back; its own display reset fixes that.
        if (wasQuarter && item.Layout is not (WindowLayout.AltCornerLeft or WindowLayout.AltCornerRight or WindowLayout.None))
            ShowAlert("Alt is back to full size. Press your display reset macro in the alt " +
                      "(/console gxMaximize 1 then /console gxRestart) to restore its resolution.");
    }

    /// <summary>Moves the main and alt windows where the layout puts them. Only moves; never activates or sends input.</summary>
    private void ApplyLayout()
    {
        _layoutTimer.Stop();
        PlaceWindows();
        // A window that lands on a monitor with different scaling resizes itself for the new scale
        // and loses the size it was given, so the layout is set once more after that settles.
        _layoutSettleTimer.Stop();
        _layoutSettleTimer.Start();
    }

    private void PlaceWindows()
    {
        var monitors = Layouts.CurrentMonitors();
        var placement = Layouts.Compute(EffectiveLayout(monitors), monitors);
        if (_mainHandle != IntPtr.Zero && placement.Main is { } main)
            WindowPlacer.Place(_mainHandle, main, wholeMonitor: monitors.Any(m => m.Bounds == main));
        if (_altHandle != IntPtr.Zero && placement.Alt is { } alt)
            WindowPlacer.Place(_altHandle, alt, wholeMonitor: monitors.Any(m => m.Bounds == alt));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => BeginInvoke(FillLayouts);

    // ---------------------------------------------------------------- status

    private void ShowAlert(string text)
    {
        _alert = text;
        _alertUntil = DateTime.Now.AddSeconds(15);
        _alertTimer.Stop();
        _alertTimer.Start();   // clears the alert again; nothing else refreshes the status on a timer
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_alert is not null && DateTime.Now < _alertUntil)
        {
            _statusLabel.ForeColor = _theme.Alert;
            _statusLabel.Text = _alert;
            return;
        }
        _alert = null;
        _statusLabel.ForeColor = _theme.Text;

        var hotkey = _listener.Hotkey;
        var main = _windows.FirstOrDefault(w => w.Handle == _mainHandle);
        var alt = _windows.FirstOrDefault(w => w.Handle == _altHandle);

        if (_listener.Capturing)
        {
            _statusLabel.Text = "Waiting for you to press the new swap key…";
            return;
        }
        if (!hotkey.IsSet)
        {
            _statusLabel.Text = "Click \"Set key…\" and press the key or mouse button you want to swap with.";
            return;
        }
        if (main is null && alt is null)
        {
            _statusLabel.Text = "No games found. Start both clients; they are picked up automatically. " +
                                "If they never show up, add the client's process name above.";
            return;
        }
        if (main is null || alt is null)
        {
            _statusLabel.Text = "Waiting for the second game. Start it and it is picked up automatically.";
            return;
        }

        IntPtr front = Native.GetForegroundWindow();
        string where = front == main.Handle ? "your main" : front == alt.Handle ? "your alt" : "another window";
        _statusLabel.Text = $"Ready. Press {hotkey} in either game to swap. In front now: {where}.\n" +
                            $"Swaps so far: {_settings.SwapCount}, of which needed the fallback: {_settings.FallbackCount}.";
    }

    // ---------------------------------------------------------------- admin

    private static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    private void RestartAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = RestartArg });
            Close();
        }
        catch (Win32Exception)
        {
            // The UAC prompt was cancelled; keep running as we are.
        }
    }

    // ---------------------------------------------------------------- UI construction

    private void BuildUi()
    {
        // Sizes below are 96-DPI pixels; the AutoScale block at the end scales everything to the
        // monitor's DPI in one pass (the designer's suspend/resume pattern), fonts included.
        SuspendLayout();

        Text = WindowTitle;
        // Loaded from the embedded .ico (all sizes) rather than the exe, so the title bar gets the crisp small ones.
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico"))
            if (iconStream is not null) Icon = new Icon(iconStream);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12, 2, 12, 8) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(Section("Swap key", BuildSwapPanel()), 0, 0);
        root.Controls.Add(Section("Games", BuildClientsPanel()), 0, 1);

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.AutoSize = false;
        _statusLabel.Height = 40;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Margin = new Padding(0, 6, 0, 0);
        root.Controls.Add(_statusLabel, 0, 3);
        root.Controls.Add(BuildBottomRow(), 0, 4);

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(680, 500);
        ResumeLayout(false);
        PerformLayout();

        MinimumSize = LogicalToDeviceUnits(new Size(640, 530));
    }

    /// <summary>A titled block: bold header, thin rule, then the content. Themes cleanly, unlike GroupBox.</summary>
    private static Control Section(string title, Control content)
    {
        var section = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 10, 0, 0) };
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        section.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var header = new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI Semibold", 9.75F), Margin = new Padding(0, 0, 0, 3) };
        var rule = new Panel { Height = 1, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 6), Tag = Theme.TagSeparator };
        content.Margin = new Padding(0);

        section.Controls.Add(header, 0, 0);
        section.Controls.Add(rule, 0, 1);
        section.Controls.Add(content, 0, 2);
        return section;
    }

    private Control BuildSwapPanel()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _hotkeyLabel.Tag = Theme.TagHotkey;
        _hotkeyLabel.AutoSize = false;
        _hotkeyLabel.Height = 40;
        _hotkeyLabel.Dock = DockStyle.Fill;
        _hotkeyLabel.BorderStyle = BorderStyle.FixedSingle;
        _hotkeyLabel.TextAlign = ContentAlignment.MiddleCenter;
        _hotkeyLabel.Font = new Font("Segoe UI Semibold", 13F);
        _hotkeyLabel.Margin = new Padding(3, 3, 6, 3);
        layout.Controls.Add(_hotkeyLabel, 0, 0);

        _setKeyButton.Size = new Size(120, 40);
        _setKeyButton.Margin = new Padding(3);
        _setKeyButton.Click += (_, _) => OnSetKeyClicked();
        layout.Controls.Add(_setKeyButton, 1, 0);

        _clearKeyButton.Text = "Clear";
        _clearKeyButton.Size = new Size(70, 40);
        _clearKeyButton.Margin = new Padding(3);
        _clearKeyButton.Click += (_, _) => ClearHotkey();
        layout.Controls.Add(_clearKeyButton, 2, 0);

        _hotkeyInfo.Dock = DockStyle.Fill;
        _hotkeyInfo.AutoSize = false;
        _hotkeyInfo.Height = 36;
        _hotkeyInfo.Tag = Theme.TagMuted;
        _hotkeyInfo.Margin = new Padding(3, 2, 3, 0);
        layout.Controls.Add(_hotkeyInfo, 0, 1);
        layout.SetColumnSpan(_hotkeyInfo, 3);

        _fromAnyWindow.Text = "Also works from other windows (YouTube, Discord…): brings back the game you were last in";
        _fromAnyWindow.AutoSize = true;
        _fromAnyWindow.Margin = new Padding(3, 0, 3, 3);
        _fromAnyWindow.CheckedChanged += OnOptionChanged;
        layout.Controls.Add(_fromAnyWindow, 0, 2);
        layout.SetColumnSpan(_fromAnyWindow, 3);

        return layout;
    }

    private Control BuildClientsPanel()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label { Text = "Main:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 8, 0) }, 0, 0);
        _mainCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _mainCombo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _mainCombo.Margin = new Padding(3, 3, 3, 3);
        _mainCombo.SelectedIndexChanged += OnMainPicked;
        layout.Controls.Add(_mainCombo, 1, 0);
        _flashMain.Text = "Flash";
        _flashMain.AutoSize = true;
        _flashMain.Click += (_, _) => { if (_mainHandle != IntPtr.Zero) Native.Flash(_mainHandle, 6); };
        layout.Controls.Add(_flashMain, 2, 0);

        layout.Controls.Add(new Label { Text = "Alt:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 8, 0) }, 0, 1);
        _altCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _altCombo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _altCombo.Margin = new Padding(3, 3, 3, 3);
        _altCombo.SelectedIndexChanged += OnAltPicked;
        layout.Controls.Add(_altCombo, 1, 1);
        _flashAlt.Text = "Flash";
        _flashAlt.AutoSize = true;
        _flashAlt.Click += (_, _) => { if (_altHandle != IntPtr.Zero) Native.Flash(_altHandle, 6); };
        layout.Controls.Add(_flashAlt, 2, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
        _swapRolesButton.Text = "Swap main/alt";
        _swapRolesButton.AutoSize = true;
        _swapRolesButton.Click += (_, _) => SwapRoles();
        _refreshButton.Text = "Refresh now";
        _refreshButton.AutoSize = true;
        _refreshButton.Click += (_, _) => ForceRefresh();
        _clientsInfo.AutoSize = true;
        _clientsInfo.Tag = Theme.TagMuted;
        _clientsInfo.Margin = new Padding(8, 7, 0, 0);
        buttons.Controls.Add(_swapRolesButton);
        buttons.Controls.Add(_refreshButton);
        buttons.Controls.Add(_clientsInfo);
        layout.Controls.Add(buttons, 0, 2);
        layout.SetColumnSpan(buttons, 3);

        layout.Controls.Add(new Label { Text = "Layout:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 8, 0) }, 0, 3);
        _layoutCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _layoutCombo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _layoutCombo.Margin = new Padding(3, 6, 3, 3);
        _layoutCombo.SelectedIndexChanged += OnLayoutPicked;
        layout.Controls.Add(_layoutCombo, 1, 3);
        _applyLayoutButton.Text = "Apply";
        _applyLayoutButton.AutoSize = true;
        _applyLayoutButton.Margin = new Padding(3, 5, 3, 3);
        _applyLayoutButton.Click += (_, _) => ApplyLayout();
        layout.Controls.Add(_applyLayoutButton, 2, 3);

        layout.Controls.Add(new Label { Text = "Process names:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 8, 0) }, 0, 4);
        _processNamesBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _processNamesBox.Margin = new Padding(3, 6, 3, 3);
        _processNamesBox.TextChanged += OnOptionChanged;
        layout.Controls.Add(_processNamesBox, 1, 4);
        layout.SetColumnSpan(_processNamesBox, 2);

        return layout;
    }

    private Control BuildBottomRow()
    {
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 4, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var note = new Label
        {
            Text = "Only changes which window is in front. Never sends keys or clicks to the games.",
            AutoSize = true,
            Tag = Theme.TagMuted,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 12, 0),
        };
        bottom.Controls.Add(note, 0, 0);

        _adminButton.Text = "Restart as administrator";
        _adminButton.AutoSize = true;
        _adminButton.Margin = new Padding(0, 0, 12, 0);
        _adminButton.Visible = false;   // shown after a swap fails, see SwitchTo
        _adminButton.Click += (_, _) => RestartAsAdmin();
        bottom.Controls.Add(_adminButton, 1, 0);

        bottom.Controls.Add(new Label { Text = "Theme:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 4, 0) }, 2, 0);
        _themeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _themeCombo.Width = 90;
        _themeCombo.Margin = new Padding(0, 2, 0, 2);
        foreach (var t in ThemeChoices) _themeCombo.Items.Add(t);
        _themeCombo.SelectedIndexChanged += OnOptionChanged;
        bottom.Controls.Add(_themeCombo, 3, 0);

        return bottom;
    }
}
