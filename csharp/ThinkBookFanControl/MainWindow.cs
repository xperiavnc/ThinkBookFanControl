using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Globalization;
using System.ComponentModel;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ThinkBookFanControl;

public sealed class MainWindow : Window
{
    private const string StartupTaskName = "ThinkBookFanControl";
    private const double HeatSoakEnterTempC = 75;
    private const double HeatSoakExitTempC = 65;
    private static readonly TimeSpan HeatSoakDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HeatSoakExitAverageDuration = TimeSpan.FromSeconds(30);

    private readonly FanController _fanController = new();
    private TemperatureReader? _temperatureReader;
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _trayMenuTimer = new();
    private readonly List<FanProfile> _profiles;
    private readonly AppSettings _settings;

    private readonly TextBlock _cpuMetricTitle = new();
    private readonly TextBlock _gpuMetricTitle = new();
    private readonly TextBlock _vramMetricTitle = new();
    private readonly TextBlock _fan1MetricTitle = new();
    private readonly TextBlock _fan2MetricTitle = new();
    private readonly TextBlock _targetMetricTitle = new();
    private readonly TextBlock _cpuTempText = MetricValue();
    private readonly TextBlock _gpuTempText = MetricValue();
    private readonly TextBlock _vramTempText = MetricValue();
    private readonly TextBlock _fan1Text = MetricValue();
    private readonly TextBlock _fan2Text = MetricValue();
    private readonly TextBlock _targetText = MetricValue();
    private readonly TextBlock _statusText = new() { Text = "Idle", VerticalAlignment = VerticalAlignment.Center };

    private readonly ComboBox _profileCombo = new() { Width = 150 };
    private readonly TextBox _nameBox = new() { Width = 130 };
    private readonly ComboBox _intervalCombo = OptionCombo("1", "2", "5");
    private readonly ComboBox _smoothingCombo = OptionCombo("1", "2", "3", "5", "10");
    private readonly ComboBox _rampDownCombo = OptionCombo("10", "20", "50", "100", "inf");
    private readonly ComboBox _editFanCombo = OptionCombo("Fan 1", "Fan 2");
    private readonly ComboBox _languageCombo = OptionCombo("\u4e2d\u6587", "English");
    private readonly ComboBox _themeCombo = OptionCombo("Light", "Dark");
    private readonly Button _startButton = new() { Content = "Start", MinWidth = 76 };
    private readonly Button _saveButton = new() { MinWidth = 76, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _refreshButton = new() { MinWidth = 76, Margin = new Thickness(0, 0, 6, 0) };
    private readonly CheckBox _syncFanSpeedsCheck = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox _startupCheck = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 10, 0) };
    private readonly CheckBox _minimizeToTrayCheck = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox _closeToTrayCheck = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };

    private readonly CurveEditor _cpuChart;
    private readonly CurveEditor _gpuChart;
    private readonly TextBlock _profileLabel = Label("");
    private readonly TextBlock _nameLabel = Label("");
    private readonly TextBlock _intervalLabel = Label("");
    private readonly TextBlock _smoothingLabel = Label("");
    private readonly TextBlock _rampDownLabel = Label("");
    private readonly TextBlock _editFanLabel = Label("");
    private readonly TextBlock _languageLabel = Label("");
    private readonly TextBlock _themeLabel = Label("");
    private TabItem? _cpuTab;
    private TabItem? _gpuTab;
    private Grid? _root;
    private TabControl? _tabs;
    private Border? _bottomBorder;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _trayCpuGpuItem;
    private Forms.ToolStripMenuItem? _trayVramItem;
    private Forms.ToolStripMenuItem? _trayFanItem;
    private bool _exitRequested;
    private string _lastCpuText = "--";
    private string _lastGpuText = "--";
    private string _lastVramText = "--";
    private string _lastFan1Text = "--";
    private string _lastFan2Text = "--";
    private readonly List<Border> _metricBorders = [];
    private readonly List<TextBlock> _labels = [];

    private int _profileIndex;
    private bool _loadingProfile;
    private bool _loadingSettings;
    private bool _running;
    private bool _closingAfterRestore;
    private bool _exitRestoreInProgress;
    private bool _temperatureSampling;
    private bool _fanSnapshotSampling;
    private bool _fanWriteInProgress;
    private readonly SemaphoreSlim _fanIoLock = new(1, 1);
    private FanTargets? _lastTarget;
    private FanTargets? _queuedTarget;
    private int _fanMinRpm = 1500;
    private int _fanMaxRpm = 5500;
    private bool _fanRangeDetected;
    private double? _smoothedCpuTempC;
    private double? _smoothedGpuTempC;
    private DateTimeOffset? _lastFan1TargetTime;
    private DateTimeOffset? _lastFan2TargetTime;
    private DateTimeOffset? _highTempSince;
    private bool _heatSoaked;
    private readonly Queue<(DateTimeOffset Timestamp, double TempC)> _heatSoakExitSamples = [];
    private List<int> _cpuFan1Curve;
    private List<int> _cpuFan2Curve;
    private List<int> _gpuFan1Curve;
    private List<int> _gpuFan2Curve;

    public MainWindow()
    {
        Title = "ThinkBook Fan Control";
        Width = 1220;
        Height = 840;
        MinWidth = 820;
        MinHeight = 620;
        FontFamily = new FontFamily("Segoe UI");
        _languageCombo.Width = 72;
        _themeCombo.Width = 64;
        _settings = CurveProfileStore.LoadSettings();
        _profiles = CurveProfileStore.Load();
        _cpuFan1Curve = [.. _profiles[0].CpuFan1Curve];
        _cpuFan2Curve = [.. _profiles[0].CpuFan2Curve];
        _gpuFan1Curve = [.. _profiles[0].GpuFan1Curve];
        _gpuFan2Curve = [.. _profiles[0].GpuFan2Curve];
        _cpuChart = new CurveEditor("CPU fan curve", CurveProfileStore.CpuTemps, _cpuFan1Curve, _cpuFan2Curve);
        _gpuChart = new CurveEditor("GPU fan curve", CurveProfileStore.GpuTemps, _gpuFan1Curve, _gpuFan2Curve);
        _cpuChart.ValuesChanged += (fan1Values, fan2Values) =>
        {
            _cpuFan1Curve = fan1Values;
            _cpuFan2Curve = fan2Values;
        };
        _gpuChart.ValuesChanged += (fan1Values, fan2Values) =>
        {
            _gpuFan1Curve = fan1Values;
            _gpuFan2Curve = fan2Values;
        };

        Content = BuildLayout();
        HookSettingsControls();
        LoadSettingsControls();
        LoadProfile(Math.Max(0, Math.Min(_profiles.Count - 1, _settings.LastProfileIndex)));
        ApplyCurveEditSettings();
        ApplyLanguage();
        ApplyTheme();
        InitializeTrayIcon();
        ApplyStartupSetting();

        SyncTimerIntervals();
        _timer.Tick += async (_, _) => await SampleAsync();
        _timer.Start();

        _trayMenuTimer.Tick += async (_, _) => await RefreshTrayMenuAsync();
        _trayMenuTimer.Start();

        StateChanged += (_, _) => OnStateChanged();
        Closing += OnClosing;
        Closed += (_, _) => OnClosed();

        if (_settings.FanControlWasRunning)
            Dispatcher.BeginInvoke(new Action(async () => await ResumeFanControlAsync()));
    }

    private UIElement BuildLayout()
    {
        var root = new Grid();
        _root = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var metrics = new UniformGrid { Columns = 6, Margin = new Thickness(12, 10, 12, 8) };
        metrics.Children.Add(Metric(_cpuMetricTitle, _cpuTempText));
        metrics.Children.Add(Metric(_gpuMetricTitle, _gpuTempText));
        metrics.Children.Add(Metric(_vramMetricTitle, _vramTempText));
        metrics.Children.Add(Metric(_fan1MetricTitle, _fan1Text));
        metrics.Children.Add(Metric(_fan2MetricTitle, _fan2Text));
        metrics.Children.Add(Metric(_targetMetricTitle, _targetText));
        Grid.SetRow(metrics, 0);
        root.Children.Add(metrics);

        var controls = BuildControls();
        Grid.SetRow(controls, 1);
        root.Children.Add(controls);

        var tabs = new TabControl { Margin = new Thickness(12, 0, 12, 8) };
        _tabs = tabs;
        _cpuTab = new TabItem { Content = _cpuChart };
        _gpuTab = new TabItem { Content = _gpuChart };
        tabs.Items.Add(_cpuTab);
        tabs.Items.Add(_gpuTab);
        Grid.SetRow(tabs, 2);
        root.Children.Add(tabs);

        var bottom = new Border { Padding = new Thickness(12, 0, 12, 12), Child = _statusText };
        _bottomBorder = bottom;
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);

        return root;
    }

    private UIElement BuildControls()
    {
        var panel = new StackPanel { Margin = new Thickness(12, 0, 12, 8) };

        var row1 = new WrapPanel { Orientation = Orientation.Horizontal };
        _profileCombo.ItemsSource = ProfileLabels();
        _profileCombo.SelectionChanged += (_, _) =>
        {
            if (!_loadingProfile && _profileCombo.SelectedIndex >= 0)
                ChangeProfile(_profileCombo.SelectedIndex);
        };
        AddLabeledControl(row1, _profileLabel, _profileCombo);
        AddLabeledControl(row1, _nameLabel, _nameBox);
        AddLabeledControl(row1, _editFanLabel, _editFanCombo);
        row1.Children.Add(_syncFanSpeedsCheck);
        AddLabeledControl(row1, _smoothingLabel, _smoothingCombo);
        AddLabeledControl(row1, _rampDownLabel, _rampDownCombo);
        AddLabeledControl(row1, _intervalLabel, _intervalCombo);
        AddLabeledControl(row1, _languageLabel, _languageCombo);
        AddLabeledControl(row1, _themeLabel, _themeCombo);
        panel.Children.Add(row1);

        var row3 = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _saveButton.Click += (_, _) => SaveCurrentProfile();
        row3.Children.Add(_saveButton);

        _refreshButton.Click += async (_, _) => await SampleAsync(force: true);
        row3.Children.Add(_refreshButton);

        _startButton.Click += async (_, _) => await ToggleRunningAsync();
        _startButton.Margin = new Thickness(0, 0, 6, 0);
        row3.Children.Add(_startButton);

        row3.Children.Add(_startupCheck);
        row3.Children.Add(_minimizeToTrayCheck);
        row3.Children.Add(_closeToTrayCheck);
        panel.Children.Add(row3);

        return panel;
    }

    private async Task SampleAsync(bool force = false)
    {
        if (_temperatureSampling)
            return;

        var profile = UiToProfile();
        SyncTimerIntervals();

        _temperatureSampling = true;
        try
        {
            var temps = await Task.Run(ReadTemperatures);
            _ = RefreshFanSnapshotAsync(force);
            UpdateHeatSoak(temps);

            _smoothedCpuTempC = SmoothTemperature(_smoothedCpuTempC, temps.CpuTempC, profile.TemperatureSmoothing);
            _smoothedGpuTempC = SmoothTemperature(_smoothedGpuTempC, temps.GpuTempC, profile.TemperatureSmoothing);

            var cpuFan1Target = CurveProfileStore.Interpolate(CurveProfileStore.CpuTemps, _cpuFan1Curve, _smoothedCpuTempC);
            var gpuFan1Target = CurveProfileStore.Interpolate(CurveProfileStore.GpuTemps, _gpuFan1Curve, _smoothedGpuTempC);
            var cpuFan2Target = CurveProfileStore.Interpolate(CurveProfileStore.CpuTemps, _cpuFan2Curve, _smoothedCpuTempC);
            var gpuFan2Target = CurveProfileStore.Interpolate(CurveProfileStore.GpuTemps, _gpuFan2Curve, _smoothedGpuTempC);
            var rawTarget = new FanTargets(
                ClampForCurrentRange(Math.Max(cpuFan1Target, gpuFan1Target)),
                ClampForCurrentRange(Math.Max(cpuFan2Target, gpuFan2Target)));
            var target = ApplyRampDown(rawTarget, profile.RampDownRpmPerSecond);

            if (_running && target != _lastTarget)
            {
                var previousTarget = _lastTarget;
                _lastTarget = target;
                var now = DateTimeOffset.Now;
                if (previousTarget is null || target.Fan1Rpm != previousTarget.Fan1Rpm)
                    _lastFan1TargetTime = now;
                if (previousTarget is null || target.Fan2Rpm != previousTarget.Fan2Rpm)
                    _lastFan2TargetTime = now;
                QueueTargetApply(target);
            }
            if (!_running)
            {
                _lastTarget = null;
                _lastFan1TargetTime = null;
                _lastFan2TargetTime = null;
                _queuedTarget = null;
            }

            _cpuTempText.Text = FormatTemp(temps.CpuTempC);
            _gpuTempText.Text = FormatTemp(temps.GpuTempC);
            _vramTempText.Text = FormatTemp(temps.VramTempC);
            _lastCpuText = _cpuTempText.Text;
            _lastGpuText = _gpuTempText.Text;
            _lastVramText = _vramTempText.Text;
            UpdateTrayMenuMetrics();
            _targetText.Text = $"F1 {target.Fan1Rpm} / F2 {target.Fan2Rpm} RPM";
            _cpuChart.SetCurrentTemp(temps.CpuTempC);
            _gpuChart.SetCurrentTemp(temps.GpuTempC);
            _statusText.Text = $"{(_running ? T("Running") : T("Monitoring"))} | {T("HeatSoak")}: {(_heatSoaked ? T("On") : T("Off"))} | CPU: {temps.CpuSensor} | GPU: {temps.GpuSensor} | VRAM: {temps.VramSensor}";
            UpdateTrayText();
        }
        catch (Exception ex)
        {
            if (_running)
            {
                _running = false;
                _startButton.Content = T("Start");
                _queuedTarget = null;
                try
                {
                    await RestoreAutoWithLockAsync();
                    SetFanControlWasRunning(false);
                }
                catch { }
            }
            _statusText.Text = T("MonitorError") + ": " + ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            _temperatureSampling = false;
        }
    }

    private async Task RefreshFanSnapshotAsync(bool force = false)
    {
        if (_fanSnapshotSampling)
            return;

        _fanSnapshotSampling = true;
        try
        {
            await _fanIoLock.WaitAsync();
            FanSnapshot fans;
            try
            {
                fans = await Task.Run(() => _fanController.ReadSnapshot());
            }
            finally
            {
                _fanIoLock.Release();
            }

            UpdateFanRange(fans.Limits);
            _fan1Text.Text = $"{fans.Fan1Rpm} RPM";
            _fan2Text.Text = $"{fans.Fan2Rpm} RPM";
            _lastFan1Text = _fan1Text.Text;
            _lastFan2Text = _fan2Text.Text;
            UpdateTrayMenuMetrics();
            UpdateTrayText();
        }
        catch (Exception ex)
        {
            _statusText.Text = T("FanReadError") + ": " + ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            _fanSnapshotSampling = false;
        }
    }

    private int ClampForCurrentRange(int rpm)
    {
        return Math.Max(_fanMinRpm, Math.Min(_fanMaxRpm, rpm));
    }

    private void QueueTargetApply(FanTargets target)
    {
        _queuedTarget = target;
        if (!_fanWriteInProgress)
            _ = ApplyQueuedTargetsAsync();
    }

    private async Task ApplyQueuedTargetsAsync()
    {
        _fanWriteInProgress = true;
        try
        {
            while (_running && _queuedTarget is FanTargets target)
            {
                _queuedTarget = null;
                await _fanIoLock.WaitAsync();
                try
                {
                    await Task.Run(() => _fanController.Apply(target.Fan1Rpm, target.Fan2Rpm));
                }
                finally
                {
                    _fanIoLock.Release();
                }
            }
        }
        catch (Exception ex)
        {
            _running = false;
            _queuedTarget = null;
            _lastTarget = null;
            _lastFan1TargetTime = null;
            _lastFan2TargetTime = null;
            _startButton.Content = T("Start");
            _statusText.Text = T("FanWriteError") + ": " + ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            _fanWriteInProgress = false;
            if (_running && _queuedTarget is not null)
                _ = ApplyQueuedTargetsAsync();
        }
    }

    private void InitializeTrayIcon()
    {
        _trayMenu = new Forms.ContextMenuStrip();
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _trayIcon.DoubleClick += (_, _) => ShowWindowFromTray();
        _trayMenu.Opening += (_, _) => UpdateTrayMenu();
        UpdateTrayMenu();
        UpdateTrayText();
    }

    private async Task RefreshTrayMenuAsync()
    {
        if (_trayMenu?.Visible == true)
            await SampleAsync(force: true);

        UpdateTrayMenuMetrics();
        UpdateTrayText();
    }

    private void UpdateTrayMenu()
    {
        if (_trayMenu is null)
            return;

        _trayMenu.Items.Clear();
        _trayCpuGpuItem = new Forms.ToolStripMenuItem { Enabled = false };
        _trayVramItem = new Forms.ToolStripMenuItem { Enabled = false };
        _trayFanItem = new Forms.ToolStripMenuItem { Enabled = false };
        _trayMenu.Items.Add(_trayCpuGpuItem);
        _trayMenu.Items.Add(_trayVramItem);
        _trayMenu.Items.Add(_trayFanItem);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());

        var toggleItem = new Forms.ToolStripMenuItem(_running ? T("Stop") : T("Start"));
        toggleItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(async () => await ToggleRunningAsync()));
        _trayMenu.Items.Add(toggleItem);

        var profilesMenu = new Forms.ToolStripMenuItem(T("Profile"));
        for (var i = 0; i < _profiles.Count; i++)
        {
            var index = i;
            var item = new Forms.ToolStripMenuItem($"{i + 1}: {_profiles[i].Name}") { Checked = i == _profileIndex };
            item.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() => ChangeProfile(index)));
            profilesMenu.DropDownItems.Add(item);
        }
        _trayMenu.Items.Add(profilesMenu);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());

        var showItem = new Forms.ToolStripMenuItem(T("ShowWindow"));
        showItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ShowWindowFromTray));
        _trayMenu.Items.Add(showItem);

        var exitItem = new Forms.ToolStripMenuItem(T("Exit"));
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            _exitRequested = true;
            Close();
        }));
        _trayMenu.Items.Add(exitItem);
        UpdateTrayMenuMetrics();
    }

    private void UpdateTrayText()
    {
        if (_trayIcon is null)
            return;

        var text = $"CPU {_lastCpuText} GPU {_lastGpuText} F1 {_lastFan1Text} F2 {_lastFan2Text}";
        _trayIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    private void UpdateTrayMenuMetrics()
    {
        if (_trayCpuGpuItem is not null)
            _trayCpuGpuItem.Text = $"CPU: {_lastCpuText}   GPU: {_lastGpuText}";
        if (_trayVramItem is not null)
            _trayVramItem.Text = $"VRAM: {_lastVramText}";
        if (_trayFanItem is not null)
            _trayFanItem.Text = $"{T("Fan1")}: {_lastFan1Text}   {T("Fan2")}: {_lastFan2Text}";
    }

    private void ShowWindowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnStateChanged()
    {
        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray)
            Hide();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_settings.CloseToTray && !_exitRequested)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        if (_closingAfterRestore || !_running)
            return;

        e.Cancel = true;
        if (_exitRestoreInProgress)
            return;

        _exitRestoreInProgress = true;
        _exitRequested = true;
        _startButton.IsEnabled = false;
        _startButton.Content = T("Stopping");
        _statusText.Text = T("RestoringAuto");
        try
        {
            await RestoreAutoForExitAsync();
            _closingAfterRestore = true;
            Close();
        }
        catch (Exception ex)
        {
            _running = true;
            _exitRequested = false;
            _exitRestoreInProgress = false;
            _startButton.IsEnabled = true;
            _startButton.Content = T("Stop");
            MessageBox.Show(this, ex.Message, T("RestoreAutoFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyStartupSetting()
    {
        try
        {
            DeleteLegacyStartupRunEntry();

            if (_settings.StartWithWindows)
                CreateStartupTask();
            else
                DeleteStartupTask();
        }
        catch (Exception ex)
        {
            _statusText.Text = ex.Message;
        }
    }

    private static void DeleteLegacyStartupRunEntry()
    {
        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
        key?.DeleteValue(StartupTaskName, throwOnMissingValue: false);
    }

    private static void CreateStartupTask()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("Cannot determine executable path for startup task.");

        var xmlPath = Path.Combine(Path.GetTempPath(), StartupTaskName + ".xml");
        try
        {
            File.WriteAllText(xmlPath, BuildStartupTaskXml(executablePath), Encoding.Unicode);
            RunSchtasks(false, "/Create", "/TN", StartupTaskName, "/XML", xmlPath, "/F");
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    private static void DeleteStartupTask()
    {
        RunSchtasks(true, "/Delete", "/TN", StartupTaskName, "/F");
    }

    private static string BuildStartupTaskXml(string executablePath)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
            throw new InvalidOperationException("Cannot determine current user SID for startup task.");

        var escapedSid = SecurityElement.Escape(sid);
        var escapedPath = SecurityElement.Escape(executablePath);
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Author>{escapedSid}</Author>
    <Description>Start ThinkBook Fan Control at user logon.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{escapedSid}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{escapedSid}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{escapedPath}</Command>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static void RunSchtasks(bool ignoreFailure, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start schtasks.exe.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (!ignoreFailure && process.ExitCode != 0)
            throw new InvalidOperationException($"schtasks.exe failed with exit code {process.ExitCode}. {output} {error}".Trim());
    }

    private void UpdateFanRange(IReadOnlyDictionary<string, FanLimit> limits)
    {
        var (minimum, maximum) = FanController.SharedRange(limits);
        if (_fanRangeDetected && minimum == _fanMinRpm && maximum == _fanMaxRpm)
            return;

        _fanRangeDetected = true;
        _fanMinRpm = minimum;
        _fanMaxRpm = maximum;
        _cpuFan1Curve = CurveProfileStore.ClampCurve(_cpuFan1Curve, minimum, maximum);
        _cpuFan2Curve = CurveProfileStore.ClampCurve(_cpuFan2Curve, minimum, maximum);
        _gpuFan1Curve = CurveProfileStore.ClampCurve(_gpuFan1Curve, minimum, maximum);
        _gpuFan2Curve = CurveProfileStore.ClampCurve(_gpuFan2Curve, minimum, maximum);
        _cpuChart.SetRpmRange(minimum, maximum);
        _gpuChart.SetRpmRange(minimum, maximum);
        _cpuChart.SetValues(_cpuFan1Curve, _cpuFan2Curve);
        _gpuChart.SetValues(_gpuFan1Curve, _gpuFan2Curve);
    }

    private void ChangeProfile(int index)
    {
        if (_running)
        {
            MessageBox.Show(this, T("StopBeforeSwitch"), T("FanCurve"), MessageBoxButton.OK, MessageBoxImage.Information);
            _profileCombo.SelectedIndex = _profileIndex;
            return;
        }

        LoadProfile(index);
        _settings.LastProfileIndex = index;
        CurveProfileStore.SaveSettings(_settings);
    }

    private void LoadProfile(int index)
    {
        _loadingProfile = true;
        _profileIndex = index;
        var profile = _profiles[index];
        _profileCombo.SelectedIndex = index;
        _nameBox.Text = profile.Name;
        SelectComboValue(_smoothingCombo, profile.TemperatureSmoothing);
        SelectComboValue(_rampDownCombo, profile.RampDownRpmPerSecond);
        _cpuFan1Curve = _fanRangeDetected ? CurveProfileStore.ClampCurve(profile.CpuFan1Curve, _fanMinRpm, _fanMaxRpm) : [.. profile.CpuFan1Curve];
        _cpuFan2Curve = _fanRangeDetected ? CurveProfileStore.ClampCurve(profile.CpuFan2Curve, _fanMinRpm, _fanMaxRpm) : [.. profile.CpuFan2Curve];
        _gpuFan1Curve = _fanRangeDetected ? CurveProfileStore.ClampCurve(profile.GpuFan1Curve, _fanMinRpm, _fanMaxRpm) : [.. profile.GpuFan1Curve];
        _gpuFan2Curve = _fanRangeDetected ? CurveProfileStore.ClampCurve(profile.GpuFan2Curve, _fanMinRpm, _fanMaxRpm) : [.. profile.GpuFan2Curve];
        _cpuChart.SetValues(_cpuFan1Curve, _cpuFan2Curve);
        _gpuChart.SetValues(_gpuFan1Curve, _gpuFan2Curve);
        _loadingProfile = false;
        UpdateTrayMenu();
    }

    private FanProfile UiToProfile()
    {
        return new FanProfile
        {
            Name = string.IsNullOrWhiteSpace(_nameBox.Text) ? $"Profile {_profileIndex + 1}" : _nameBox.Text.Trim(),
            TemperatureSmoothing = SelectedNumber(_smoothingCombo, 3),
            RampDownRpmPerSecond = SelectedRampDown(),
            CpuFan1Curve = [.. _cpuFan1Curve],
            CpuFan2Curve = [.. _cpuFan2Curve],
            GpuFan1Curve = [.. _gpuFan1Curve],
            GpuFan2Curve = [.. _gpuFan2Curve],
            CpuCurve = [.. _cpuFan1Curve],
            GpuCurve = [.. _gpuFan1Curve],
        };
    }

    private void SaveCurrentProfile()
    {
        _profiles[_profileIndex] = UiToProfile();
        CurveProfileStore.Save(_profiles);
        _profileCombo.ItemsSource = ProfileLabels();
        _profileCombo.SelectedIndex = _profileIndex;
        _statusText.Text = T("Saved") + " " + CurveProfileStore.ProfilePath;
        UpdateTrayMenu();
    }

    private async Task ToggleRunningAsync()
    {
        if (_running)
        {
            await RestoreAutoAsync();
            return;
        }

        SaveCurrentProfile();
        StartFanControl();
    }

    private async Task ResumeFanControlAsync()
    {
        if (_running)
            return;

        StartFanControl();
        _statusText.Text = T("ControllerResumed");
        await SampleAsync(force: true);
    }

    private void StartFanControl()
    {
        _running = true;
        SetFanControlWasRunning(true);
        _lastTarget = null;
        _lastFan1TargetTime = null;
        _lastFan2TargetTime = null;
        _smoothedCpuTempC = null;
        _smoothedGpuTempC = null;
        _highTempSince = null;
        _heatSoaked = false;
        _heatSoakExitSamples.Clear();
        _startButton.Content = T("Stop");
        _statusText.Text = T("ControllerEnabled");
        UpdateTrayMenu();
    }

    private async Task RestoreAutoAsync()
    {
        _running = false;
        _lastTarget = null;
        _lastFan1TargetTime = null;
        _lastFan2TargetTime = null;
        _queuedTarget = null;
        _startButton.IsEnabled = false;
        _startButton.Content = T("Stopping");
        _statusText.Text = T("RestoringAuto");
        try
        {
            await RestoreAutoWithLockAsync();
            SetFanControlWasRunning(false);
            _startButton.Content = T("Start");
            _statusText.Text = T("AutoRestored");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, T("RestoreAutoFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
            _startButton.Content = T("Start");
        }
        finally
        {
            _startButton.IsEnabled = true;
            UpdateTrayMenu();
        }
    }

    private void OnClosed()
    {
        _timer.Stop();
        _trayMenuTimer.Stop();
        if (_running)
        {
            try
            {
                _fanIoLock.Wait();
                try
                {
                    _fanController.RestoreAuto();
                    SetFanControlWasRunning(false);
                }
                finally { _fanIoLock.Release(); }
            }
            catch { }
        }
        _temperatureReader?.Dispose();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _trayMenu?.Dispose();
    }

    private async Task RestoreAutoWithLockAsync()
    {
        await _fanIoLock.WaitAsync();
        try
        {
            await Task.Run(() => _fanController.RestoreAuto());
        }
        finally
        {
            _fanIoLock.Release();
        }
    }

    private async Task RestoreAutoForExitAsync()
    {
        _running = false;
        _queuedTarget = null;
        _lastTarget = null;
        _lastFan1TargetTime = null;
        _lastFan2TargetTime = null;
        await RestoreAutoWithLockAsync();
        SetFanControlWasRunning(false);
    }

    private void SetFanControlWasRunning(bool value)
    {
        if (_settings.FanControlWasRunning == value)
            return;

        _settings.FanControlWasRunning = value;
        CurveProfileStore.SaveSettings(_settings);
    }

    private TemperatureSnapshot ReadTemperatures()
    {
        _temperatureReader ??= new TemperatureReader();
        return _temperatureReader.Read();
    }

    private static double? SmoothTemperature(double? previous, double? current, double smoothingSamples)
    {
        if (current is null)
            return previous;
        if (previous is null)
            return current;

        var samples = Math.Max(1, Math.Min(10, smoothingSamples));
        var alpha = 2.0 / (samples + 1.0);
        return previous.Value + (current.Value - previous.Value) * alpha;
    }

    private void UpdateHeatSoak(TemperatureSnapshot temps)
    {
        var hottest = new[] { temps.CpuTempC, temps.GpuTempC }
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .DefaultIfEmpty(double.NaN)
            .Max();
        if (double.IsNaN(hottest))
            return;

        var now = DateTimeOffset.Now;
        if (hottest >= HeatSoakEnterTempC)
        {
            _highTempSince ??= now;
            _heatSoakExitSamples.Clear();
            if (now - _highTempSince.Value >= HeatSoakDuration)
                _heatSoaked = true;
            return;
        }

        if (hottest < HeatSoakExitTempC)
        {
            TrackHeatSoakExitSample(now, hottest);
            if (CanExitHeatSoak())
            {
                _highTempSince = null;
                _heatSoaked = false;
                _heatSoakExitSamples.Clear();
            }
            return;
        }

        _heatSoakExitSamples.Clear();
        if (!_heatSoaked)
            _highTempSince = null;
    }

    private void TrackHeatSoakExitSample(DateTimeOffset now, double hottest)
    {
        _heatSoakExitSamples.Enqueue((now, hottest));
        while (_heatSoakExitSamples.Count > 0 &&
               now - _heatSoakExitSamples.Peek().Timestamp > HeatSoakExitAverageDuration)
        {
            _heatSoakExitSamples.Dequeue();
        }
    }

    private bool CanExitHeatSoak()
    {
        if (!_heatSoaked || _heatSoakExitSamples.Count < 2)
            return false;

        var span = _heatSoakExitSamples.Last().Timestamp - _heatSoakExitSamples.Peek().Timestamp;
        if (span < HeatSoakExitAverageDuration)
            return false;

        return _heatSoakExitSamples.Average(sample => sample.TempC) < HeatSoakExitTempC;
    }

    private FanTargets ApplyRampDown(FanTargets rawTarget, double rampDownRpmPerSecond)
    {
        if (rampDownRpmPerSecond <= 0)
            return rawTarget;
        if (!_heatSoaked)
            return rawTarget;
        if (!_running || _lastTarget is null)
            return rawTarget;

        var now = DateTimeOffset.Now;
        return new FanTargets(
            ApplyRampDown(rawTarget.Fan1Rpm, _lastTarget.Fan1Rpm, _lastFan1TargetTime, now, rampDownRpmPerSecond),
            ApplyRampDown(rawTarget.Fan2Rpm, _lastTarget.Fan2Rpm, _lastFan2TargetTime, now, rampDownRpmPerSecond));
    }

    private int ApplyRampDown(int rawTarget, int lastTarget, DateTimeOffset? lastTargetTime, DateTimeOffset now, double rampDownRpmPerSecond)
    {
        if (rawTarget >= lastTarget)
            return rawTarget;

        var elapsedSeconds = Math.Max(0.1, (now - (lastTargetTime ?? now)).TotalSeconds);
        var maxDrop = Math.Max(1, rampDownRpmPerSecond) * elapsedSeconds;
        var limited = Math.Max(rawTarget, lastTarget - maxDrop);
        return ClampForCurrentRange((int)Math.Ceiling(limited / 100.0) * 100);
    }

    private static string FormatTemp(double? value)
    {
        return value is null ? "-- \u00B0C" : $"{value:F1} \u00B0C";
    }

    private static ComboBox OptionCombo(params string[] options)
    {
        var comboBox = new ComboBox
        {
            Width = 62,
            IsEditable = false,
            Margin = new Thickness(0, 0, 8, 0)
        };
        foreach (var option in options)
            comboBox.Items.Add(option);
        comboBox.SelectedIndex = 0;
        return comboBox;
    }

    private void HookSettingsControls()
    {
        _languageCombo.SelectionChanged += (_, _) =>
        {
            if (_loadingSettings)
                return;
            _settings.Language = _languageCombo.SelectedIndex == 1 ? "en-US" : "zh-CN";
            CurveProfileStore.SaveSettings(_settings);
            ApplyLanguage();
        };

        _themeCombo.SelectionChanged += (_, _) =>
        {
            if (_loadingSettings)
                return;
            _settings.Theme = _themeCombo.SelectedIndex == 1 ? "dark" : "light";
            CurveProfileStore.SaveSettings(_settings);
            ApplyTheme();
        };

        _intervalCombo.SelectionChanged += (_, _) =>
        {
            if (_loadingSettings)
                return;
            _settings.IntervalSeconds = SelectedNumber(_intervalCombo, 2.0);
            CurveProfileStore.SaveSettings(_settings);
            SyncTimerIntervals();
        };

        _editFanCombo.SelectionChanged += (_, _) =>
        {
            if (_loadingSettings)
                return;
            _settings.EditFan = _editFanCombo.SelectedIndex == 1 ? 2 : 1;
            CurveProfileStore.SaveSettings(_settings);
            ApplyCurveEditSettings();
        };

        _syncFanSpeedsCheck.Checked += (_, _) => UpdateBooleanSetting("syncFanSpeeds", true);
        _syncFanSpeedsCheck.Unchecked += (_, _) => UpdateBooleanSetting("syncFanSpeeds", false);
        _startupCheck.Checked += (_, _) => UpdateBooleanSetting("startup", true);
        _startupCheck.Unchecked += (_, _) => UpdateBooleanSetting("startup", false);
        _minimizeToTrayCheck.Checked += (_, _) => UpdateBooleanSetting("minimizeToTray", true);
        _minimizeToTrayCheck.Unchecked += (_, _) => UpdateBooleanSetting("minimizeToTray", false);
        _closeToTrayCheck.Checked += (_, _) => UpdateBooleanSetting("closeToTray", true);
        _closeToTrayCheck.Unchecked += (_, _) => UpdateBooleanSetting("closeToTray", false);
    }

    private void LoadSettingsControls()
    {
        _loadingSettings = true;
        SelectComboValue(_intervalCombo, _settings.IntervalSeconds);
        _editFanCombo.SelectedIndex = _settings.EditFan == 2 ? 1 : 0;
        _syncFanSpeedsCheck.IsChecked = _settings.SyncFanSpeeds;
        _languageCombo.SelectedIndex = _settings.Language == "en-US" ? 1 : 0;
        _themeCombo.SelectedIndex = _settings.Theme == "dark" ? 1 : 0;
        _startupCheck.IsChecked = _settings.StartWithWindows;
        _minimizeToTrayCheck.IsChecked = _settings.MinimizeToTray;
        _closeToTrayCheck.IsChecked = _settings.CloseToTray;
        _loadingSettings = false;
    }

    private void UpdateBooleanSetting(string setting, bool value)
    {
        if (_loadingSettings)
            return;

        switch (setting)
        {
            case "startup":
                _settings.StartWithWindows = value;
                ApplyStartupSetting();
                break;
            case "minimizeToTray":
                _settings.MinimizeToTray = value;
                break;
            case "closeToTray":
                _settings.CloseToTray = value;
                break;
            case "syncFanSpeeds":
                _settings.SyncFanSpeeds = value;
                ApplyCurveEditSettings();
                break;
        }
        CurveProfileStore.SaveSettings(_settings);
        ApplyLanguage();
        UpdateTrayMenu();
    }

    private void ApplyCurveEditSettings()
    {
        _cpuChart.SetEditFan(_settings.EditFan);
        _gpuChart.SetEditFan(_settings.EditFan);
        _cpuChart.SetSyncFanSpeeds(_settings.SyncFanSpeeds);
        _gpuChart.SetSyncFanSpeeds(_settings.SyncFanSpeeds);
    }

    private void SyncTimerIntervals()
    {
        var interval = TimeSpan.FromSeconds(Math.Max(0.5, _settings.IntervalSeconds));
        _timer.Interval = interval;
        _trayMenuTimer.Interval = interval;
    }

    private bool IsChinese => _settings.Language != "en-US";
    private bool IsDark => _settings.Theme == "dark";

    private string T(string key)
    {
        return IsChinese ? key switch
        {
            "AppTitle" => "ThinkBook \u98ce\u6247\u63a7\u5236",
            "Profile" => "\u65b9\u6848",
            "Name" => "\u540d\u79f0",
            "Interval" => "\u5237\u65b0\u95f4\u9694",
            "TempSmoothing" => "\u6e29\u5ea6\u5e73\u6ed1",
            "RampDown" => "\u964d\u901f\u9650\u5236",
            "EditFan" => "\u7f16\u8f91",
            "SyncFanSpeeds" => "\u540c\u6b65\u8f6c\u901f",
            "Language" => "\u8bed\u8a00",
            "Theme" => "\u4e3b\u9898",
            "Light" => "\u6d45\u8272",
            "Dark" => "\u6df1\u8272",
            "Fan1" => "\u98ce\u6247 1",
            "Fan2" => "\u98ce\u6247 2",
            "Target" => "\u76ee\u6807",
            "CpuCurve" => "CPU \u66f2\u7ebf",
            "GpuCurve" => "GPU \u66f2\u7ebf",
            "TemperatureAxis" => "\u6e29\u5ea6 (\u00B0C)",
            "Save" => "\u4fdd\u5b58",
            "Refresh" => "\u5237\u65b0",
            "Start" => "\u542f\u52a8",
            "Stop" => "\u505c\u6b62",
            "Idle" => "\u7a7a\u95f2",
            "Stopping" => "\u505c\u6b62\u4e2d...",
            "Running" => "\u8fd0\u884c\u4e2d",
            "Monitoring" => "\u76d1\u63a7\u4e2d",
            "HeatSoak" => "\u70ed\u6d78",
            "On" => "\u5f00",
            "Off" => "\u5173",
            "Saved" => "\u5df2\u4fdd\u5b58",
            "ControllerEnabled" => "\u98ce\u6247\u63a7\u5236\u5df2\u542f\u7528",
            "ControllerResumed" => "\u5df2\u6062\u590d\u4e0a\u6b21\u672a\u505c\u6b62\u7684\u98ce\u6247\u63a7\u5236",
            "RestoringAuto" => "\u6b63\u5728\u6062\u590d\u81ea\u52a8\u98ce\u6247\u63a7\u5236...",
            "AutoRestored" => "\u5df2\u6062\u590d\u81ea\u52a8\u98ce\u6247\u63a7\u5236",
            "RestoreAutoFailed" => "\u6062\u590d\u81ea\u52a8\u5931\u8d25",
            "StopBeforeSwitch" => "\u5207\u6362\u65b9\u6848\u524d\u8bf7\u5148\u505c\u6b62\u63a7\u5236\u5668\u3002",
            "FanCurve" => "\u98ce\u6247\u66f2\u7ebf",
            "MonitorError" => "\u76d1\u63a7\u9519\u8bef",
            "FanReadError" => "\u98ce\u6247\u8bfb\u53d6\u9519\u8bef",
            "FanWriteError" => "\u98ce\u6247\u5199\u5165\u9519\u8bef",
            "Startup" => "\u5f00\u673a\u81ea\u542f",
            "MinimizeToTray" => "\u6700\u5c0f\u5316\u5230\u6258\u76d8",
            "CloseToTray" => "\u5173\u95ed\u65f6\u6700\u5c0f\u5316",
            "ShowWindow" => "\u663e\u793a\u7a97\u53e3",
            "Exit" => "\u9000\u51fa",
            _ => key
        } : key switch
        {
            "AppTitle" => "ThinkBook Fan Control",
            "TempSmoothing" => "Temp smoothing",
            "RampDown" => "Ramp down",
            "EditFan" => "Edit",
            "SyncFanSpeeds" => "Sync speeds",
            "Fan1" => "Fan 1",
            "Fan2" => "Fan 2",
            "CpuCurve" => "CPU Curve",
            "GpuCurve" => "GPU Curve",
            "TemperatureAxis" => "Temperature (\u00B0C)",
            "Idle" => "Idle",
            "Stopping" => "Stopping...",
            "HeatSoak" => "Heat soak",
            "ControllerEnabled" => "Controller enabled",
            "ControllerResumed" => "Resumed previously active fan control",
            "RestoringAuto" => "Restoring automatic fan control...",
            "AutoRestored" => "Automatic fan control restored",
            "RestoreAutoFailed" => "Restore auto failed",
            "StopBeforeSwitch" => "Stop the controller before switching profiles.",
            "FanCurve" => "Fan curve",
            "MonitorError" => "Monitor error",
            "FanReadError" => "Fan read error",
            "FanWriteError" => "Fan write error",
            "Startup" => "Start with Windows",
            "MinimizeToTray" => "Minimize to tray",
            "CloseToTray" => "Close to tray",
            "ShowWindow" => "Show window",
            "Exit" => "Exit",
            _ => key
        };
    }

    private void ApplyLanguage()
    {
        Title = T("AppTitle");
        var fontFamilyName = IsChinese ? "Segoe UI, SimSun" : "Segoe UI";
        FontFamily = new FontFamily(fontFamilyName);
        _cpuMetricTitle.Text = "CPU";
        _gpuMetricTitle.Text = "GPU";
        _vramMetricTitle.Text = "VRAM";
        _fan1MetricTitle.Text = T("Fan1");
        _fan2MetricTitle.Text = T("Fan2");
        _targetMetricTitle.Text = T("Target");
        _profileLabel.Text = T("Profile");
        _nameLabel.Text = T("Name");
        _intervalLabel.Text = T("Interval");
        _smoothingLabel.Text = T("TempSmoothing");
        _rampDownLabel.Text = T("RampDown");
        _editFanLabel.Text = T("EditFan");
        _languageLabel.Text = T("Language");
        _themeLabel.Text = T("Theme");
        _saveButton.Content = T("Save");
        _refreshButton.Content = T("Refresh");
        _syncFanSpeedsCheck.Content = T("SyncFanSpeeds");
        _startupCheck.Content = T("Startup");
        _minimizeToTrayCheck.Content = T("MinimizeToTray");
        _closeToTrayCheck.Content = T("CloseToTray");
        _startButton.Content = _running ? T("Stop") : T("Start");
        _cpuTab!.Header = T("CpuCurve");
        _gpuTab!.Header = T("GpuCurve");
        _cpuChart.SetLabels(T("CpuCurve"), T("TemperatureAxis"));
        _gpuChart.SetLabels(T("GpuCurve"), T("TemperatureAxis"));
        _cpuChart.SetFontFamily(fontFamilyName);
        _gpuChart.SetFontFamily(fontFamilyName);
        if (!_running && (_statusText.Text is "Idle" or "\u7a7a\u95f2"))
            _statusText.Text = T("Idle");

        _loadingSettings = true;
        SetComboItems(_languageCombo, ["\u4e2d\u6587", "English"], IsChinese ? 0 : 1);
        SetComboItems(_themeCombo, [T("Light"), T("Dark")], IsDark ? 1 : 0);
        SetComboItems(_editFanCombo, [T("Fan1"), T("Fan2")], _settings.EditFan == 2 ? 1 : 0);
        _loadingSettings = false;
        UpdateTrayMenu();
    }

    private void ApplyTheme()
    {
        var background = Brush(IsDark ? "#111827" : "#ffffff");
        var surface = Brush(IsDark ? "#1f2937" : "#ffffff");
        var border = Brush(IsDark ? "#374151" : "#d1d5db");
        var text = Brush(IsDark ? "#f9fafb" : "#111827");
        var muted = Brush(IsDark ? "#d1d5db" : "#4b5563");

        Background = background;
        if (_root is not null)
            _root.Background = background;
        if (_tabs is not null)
        {
            _tabs.Background = background;
            _tabs.Foreground = text;
            _tabs.BorderBrush = border;
        }
        if (_bottomBorder is not null)
            _bottomBorder.Background = background;
        _statusText.Foreground = muted;

        foreach (var metric in _metricBorders)
        {
            metric.Background = surface;
            metric.BorderBrush = border;
        }

        foreach (var label in _labels)
            label.Foreground = muted;
        foreach (var checkBox in new[] { _syncFanSpeedsCheck, _startupCheck, _minimizeToTrayCheck, _closeToTrayCheck })
            checkBox.Foreground = muted;
        foreach (var value in new[] { _cpuTempText, _gpuTempText, _vramTempText, _fan1Text, _fan2Text, _targetText })
            value.Foreground = text;

        _cpuChart.SetTheme(IsDark);
        _gpuChart.SetTheme(IsDark);
    }

    private static void SetComboItems(ComboBox comboBox, IReadOnlyList<string> items, int selectedIndex)
    {
        comboBox.Items.Clear();
        foreach (var item in items)
            comboBox.Items.Add(item);
        comboBox.SelectedIndex = Math.Max(0, Math.Min(items.Count - 1, selectedIndex));
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void AddLabeledControl(Panel panel, TextBlock label, UIElement control)
    {
        if (!_labels.Contains(label))
            _labels.Add(label);
        var group = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 8, 6)
        };
        group.Children.Add(label);
        group.Children.Add(control);
        panel.Children.Add(group);
    }

    private static void SelectComboValue(ComboBox comboBox, double value)
    {
        if (value <= 0 && comboBox.Items.Contains("inf"))
        {
            comboBox.SelectedItem = "inf";
            return;
        }

        var text = value.ToString("0.##", CultureInfo.InvariantCulture);
        if (comboBox.Items.Contains(text))
        {
            comboBox.SelectedItem = text;
            return;
        }

        var nearest = comboBox.Items
            .OfType<string>()
            .Where(item => double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .OrderBy(item => Math.Abs(double.Parse(item, CultureInfo.InvariantCulture) - value))
            .FirstOrDefault();

        comboBox.SelectedItem = nearest ?? comboBox.Items[0];
    }

    private static double SelectedNumber(ComboBox comboBox, double fallback)
    {
        return comboBox.SelectedItem is string text &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private double SelectedRampDown()
    {
        return string.Equals(_rampDownCombo.SelectedItem as string, "inf", StringComparison.OrdinalIgnoreCase)
            ? 0
            : SelectedNumber(_rampDownCombo, 20);
    }

    private Border Metric(TextBlock title, TextBlock value)
    {
        var panel = new StackPanel();
        if (!_labels.Contains(title))
            _labels.Add(title);
        panel.Children.Add(title);
        panel.Children.Add(value);
        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(209, 213, 219)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Margin = new Thickness(4),
            Child = panel
        };
        _metricBorders.Add(border);
        return border;
    }

    private static TextBlock MetricValue()
    {
        return new TextBlock
        {
            Text = "--",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 2, 0, 0)
        };
    }

    private static TextBlock Label(string text)
    {
        return new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
    }

    private List<string> ProfileLabels()
    {
        return _profiles.Select((profile, index) => $"{index + 1}: {profile.Name}").ToList();
    }
}
