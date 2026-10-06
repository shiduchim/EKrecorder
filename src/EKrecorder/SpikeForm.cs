using System.Diagnostics;
using EKrecorder.Capture;
using EKrecorder.Diagnostics;
using EKrecorder.Monitors;
using EKrecorder.Overlays;
using Microsoft.Win32;

namespace EKrecorder;

/// <summary>
/// The small test window: pick a monitor, Identify, run the 10-second capture test, open the results.
/// In self-test mode (used by the build machine) it runs the test on Monitor 1 at once, asks nothing, and closes.
/// </summary>
internal sealed class SpikeForm : Form
{
    private readonly bool _selfTest;
    private readonly string _outputRoot;
    private readonly FlowLayoutPanel _monitorPanel;
    private readonly Button _identifyButton;
    private readonly Button _testButton;
    private readonly Button _resultsButton;
    private readonly Label _status;
    private readonly System.Windows.Forms.Timer _identifyTimer = new() { Interval = 3000 };
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private string? _selectedId;
    private IdentifyOverlays? _identify;
    private string? _lastResultFolder;
    private bool _busy;

    public SpikeForm(bool selfTest, string outputRoot)
    {
        _selfTest = selfTest;
        _outputRoot = outputRoot;

        Text = "EKrecorder – capture test";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        var layout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1 };
        layout.Controls.Add(new Label { Text = "Monitor to capture:", AutoSize = true, Margin = new Padding(3, 0, 3, 2) });

        _monitorPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0),
        };
        layout.Controls.Add(_monitorPanel);

        _identifyButton = new Button { Text = "Identify", AutoSize = true };
        _testButton = new Button { Text = "Run 10-second test", AutoSize = true };
        _resultsButton = new Button { Text = "Open results", AutoSize = true, Enabled = false };
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        buttons.Controls.AddRange([_identifyButton, _testButton, _resultsButton]);
        layout.Controls.Add(buttons);

        _status = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(380, 0),
            Margin = new Padding(3, 6, 3, 0),
            Text = "Pick a monitor, then run the test.",
        };
        layout.Controls.Add(_status);
        Controls.Add(layout);

        _identifyButton.Click += (_, _) => ShowIdentify();
        _testButton.Click += async (_, _) => await RunTestAsync();
        _resultsButton.Click += (_, _) => OpenFolder(_lastResultFolder);
        _identifyTimer.Tick += (_, _) => HideIdentify();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        RefreshMonitors("start");
    }

    /// <summary>0 = every check passed, 1 = a check failed, 3 = the test itself crashed.</summary>
    public int ExitCode { get; private set; }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_selfTest)
        {
            await RunTestAsync();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            HideIdentify();
            _identifyTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void RefreshMonitors(string reason)
    {
        Log.Info($"Finding monitors ({reason}).");
        _monitors = MonitorEnumerator.GetMonitors();
        // Keep the choice by the monitor's identity, not its number: numbers shift when monitors are added or moved.
        if (_selectedId is null || _monitors.All(m => m.StableId != _selectedId))
        {
            _selectedId = _monitors.FirstOrDefault()?.StableId;
        }

        Control[] old = _monitorPanel.Controls.Cast<Control>().ToArray();
        _monitorPanel.SuspendLayout();
        _monitorPanel.Controls.Clear();
        foreach (Control control in old)
        {
            control.Dispose();
        }

        foreach (MonitorInfo monitor in _monitors)
        {
            var choice = new RadioButton
            {
                AutoSize = true,
                Text = monitor.ChoiceLabel,
                Checked = monitor.StableId == _selectedId,
                Tag = monitor.StableId,
                Margin = new Padding(3, 1, 3, 1),
            };
            choice.CheckedChanged += (sender, _) =>
            {
                if (sender is RadioButton { Checked: true, Tag: string id })
                {
                    _selectedId = id;
                    Log.Info($"Selected {_monitors.FirstOrDefault(m => m.StableId == id)?.Name} ({id}).");
                }
            };
            _monitorPanel.Controls.Add(choice);
        }

        if (_monitors.Count == 0)
        {
            _monitorPanel.Controls.Add(new Label { Text = "No monitor found.", AutoSize = true });
        }

        _monitorPanel.ResumeLayout();
        UpdateButtons();
    }

    private void ShowIdentify()
    {
        HideIdentify();
        RefreshMonitors("Identify");
        _identify = IdentifyOverlays.Show(_monitors);
        foreach (string line in _identify.Describe())
        {
            Log.Info($"Identify: {line}");
        }

        SetStatus(_identify.AllExcluded
            ? $"Showing EKrecorder's numbers on {_identify.ShownCount} monitor(s). They are excluded from capture."
            : "Some numbers could not be excluded from capture, so they were not shown. See the log.");
        _identifyTimer.Start();
    }

    private void HideIdentify()
    {
        _identifyTimer.Stop();
        _identify?.Dispose();
        _identify = null;
    }

    private async Task RunTestAsync()
    {
        if (_busy)
        {
            return;
        }

        HideIdentify();
        // Fresh monitor handles and positions, in case anything changed since the window opened.
        RefreshMonitors("test");
        MonitorInfo? monitor = _monitors.FirstOrDefault(m => m.StableId == _selectedId) ?? _monitors.FirstOrDefault();
        if (monitor is null)
        {
            SetStatus("No monitor found.");
            ExitCode = 1;
            if (_selfTest)
            {
                Close();
            }

            return;
        }

        _busy = true;
        UpdateButtons();
        try
        {
            string folder = Path.Combine(_outputRoot, $"{DateTime.Now:yyyy-MM-dd HH-mm-ss} {monitor.Name}");
            var options = new CaptureTestOptions(monitor, _monitors, folder, _selfTest ? null : AskYesNo);
            TestReport report = await new CaptureTest(options, new Progress<string>(SetStatus)).RunAsync();
            _lastResultFolder = folder;
            ExitCode = report.HasFailures ? 1 : 0;
            SetStatus(report.ShortSummary);
            if (!_selfTest)
            {
                OpenFolder(folder);
            }
        }
        catch (Exception ex)
        {
            Log.Error("The capture test stopped with an error", ex);
            SetStatus($"The test stopped with an error: {ex.Message}");
            ExitCode = 3;
        }
        finally
        {
            _busy = false;
            UpdateButtons();
            if (_selfTest)
            {
                Close();
            }
        }
    }

    private bool AskYesNo(string question) =>
        MessageBox.Show(this, question, "EKrecorder test", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnDisplaySettingsChanged(sender, e)));
            return;
        }

        if (_busy)
        {
            Log.Warn("Display settings changed during the test; positions measured at the start may be off.");
            return;
        }

        HideIdentify();
        RefreshMonitors("display settings changed");
    }

    private void UpdateButtons()
    {
        _monitorPanel.Enabled = !_busy;
        _identifyButton.Enabled = !_busy && _monitors.Count > 0;
        _testButton.Enabled = !_busy && _monitors.Count > 0;
        _resultsButton.Enabled = !_busy && _lastResultFolder is not null;
    }

    private void SetStatus(string text) => _status.Text = text;

    private static void OpenFolder(string? folder)
    {
        if (folder is null || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {folder}", ex);
        }
    }
}
