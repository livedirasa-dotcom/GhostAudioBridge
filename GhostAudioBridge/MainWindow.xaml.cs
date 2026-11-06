using System.Windows;
using System.Windows.Controls;
using NAudio.Wave;

namespace GhostAudioBridge;

public partial class MainWindow : Window
{
    private AudioEngine? _engine;
    private readonly List<DeviceChoice> _inputs = new();
    private readonly List<DeviceChoice> _outputs = new();

    public MainWindow()
    {
        InitializeComponent();
        RefreshDevices();
        BindSlider(Line1Gain, Line1GainText, "%");
        BindSlider(Line2Gain, Line2GainText, "%");
        BindPan(Line1Pan, Line1PanText);
        BindPan(Line2Pan, Line2PanText);
        BindSlider(MicGain, MicGainText, "%");
        BindSlider(MasterGain, MasterGainText, "%");
        BufferSizeCombo.SelectionChanged += (_, _) => UpdateBufferInfo();
        UpdateBufferInfo();
    }

    private int SelectedBufferSamples()
    {
        if (BufferSizeCombo.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Content?.ToString(), out int samples))
            return samples;
        return 128;
    }

    private void UpdateBufferInfo()
    {
        int samples = SelectedBufferSamples();
        double ms = samples * 1000.0 / 48000.0;
        BufferInfoText.Text = $"{samples} samples ≈ {ms:0.00} ms @ 48 kHz";
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void RefreshDevices()
    {
        try
        {
            _inputs.Clear();
            _outputs.Clear();
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var c = WaveIn.GetCapabilities(i);
                _inputs.Add(new DeviceChoice(i, c.ProductName));
            }
            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                var c = WaveOut.GetCapabilities(i);
                _outputs.Add(new DeviceChoice(i, c.ProductName));
            }

            InputDeviceCombo.ItemsSource = _inputs;
            MicDeviceCombo.ItemsSource = _inputs;
            MonitorDeviceCombo.ItemsSource = _outputs;
            VirtualDeviceCombo.ItemsSource = _outputs;

            InputDeviceCombo.SelectedIndex = FindIndex(_inputs, "M-Audio");
            if (InputDeviceCombo.SelectedIndex < 0 && _inputs.Count > 0) InputDeviceCombo.SelectedIndex = 0;
            MicDeviceCombo.SelectedIndex = FindIndex(_inputs, "Microphone");
            if (MicDeviceCombo.SelectedIndex < 0 && _inputs.Count > 1)
                MicDeviceCombo.SelectedIndex = Math.Min(1, _inputs.Count - 1);
            MonitorDeviceCombo.SelectedIndex = FindIndex(_outputs, "M-Audio");
            if (MonitorDeviceCombo.SelectedIndex < 0 && _outputs.Count > 0) MonitorDeviceCombo.SelectedIndex = 0;
            VirtualDeviceCombo.SelectedIndex = FindIndex(_outputs, "CABLE Input");
            if (VirtualDeviceCombo.SelectedIndex < 0 && _outputs.Count > 1)
                VirtualDeviceCombo.SelectedIndex = _outputs.Count - 1;

            StatusText.Text = $"Found {_inputs.Count} input device(s) and {_outputs.Count} output device(s).";
        }
        catch (Exception ex) { StatusText.Text = "Device scan failed: " + ex.Message; }
    }

    private static int FindIndex(List<DeviceChoice> list, string token) =>
        list.FindIndex(x => x.Name.Contains(token, StringComparison.OrdinalIgnoreCase));

    private void BindSlider(Slider slider, TextBlock label, string suffix)
    {
        slider.ValueChanged += (_, _) =>
        {
            label.Text = $"{slider.Value * 100:0}{suffix}";
            ApplyMixSettings();
        };
    }

    private void BindPan(Slider slider, TextBlock label)
    {
        slider.ValueChanged += (_, _) =>
        {
            label.Text = slider.Value < -0.15 ? "L" : slider.Value > 0.15 ? "R" : "C";
            ApplyMixSettings();
        };
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (InputDeviceCombo.SelectedItem is not DeviceChoice line ||
                MicDeviceCombo.SelectedItem is not DeviceChoice mic)
                throw new InvalidOperationException("Select both the M-Audio input and laptop microphone.");

            if (MonitorEnabled.IsChecked == true && MonitorDeviceCombo.SelectedItem is not DeviceChoice)
                throw new InvalidOperationException("Select a monitor output or turn monitoring off.");
            if (VirtualEnabled.IsChecked == true && VirtualDeviceCombo.SelectedItem is not DeviceChoice)
                throw new InvalidOperationException("Select a virtual cable output or turn its feed off.");
            if (MonitorEnabled.IsChecked == true && VirtualEnabled.IsChecked == true &&
                MonitorDeviceCombo.SelectedItem is DeviceChoice mo &&
                VirtualDeviceCombo.SelectedItem is DeviceChoice vo && mo.Index == vo.Index)
                throw new InvalidOperationException("Monitor and virtual outputs must be different devices.");

            _engine = new AudioEngine(
                line.Index, mic.Index,
                MonitorEnabled.IsChecked == true ? (MonitorDeviceCombo.SelectedItem as DeviceChoice)?.Index : null,
                VirtualEnabled.IsChecked == true ? (VirtualDeviceCombo.SelectedItem as DeviceChoice)?.Index : null,
                SelectedBufferSamples());

            ApplyMixSettings();
            _engine.Start();
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            BufferSizeCombo.IsEnabled = false;
            StateText.Text = "RUNNING";
            StateText.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
            StatusText.Text = "Audio is running. Verify the selected inputs and ChatGPT microphone.";
        }
        catch (Exception ex)
        {
            _engine?.Dispose();
            _engine = null;
            StatusText.Text = "Could not start: " + ex.Message;
            MessageBox.Show(ex.Message, "Ghost Audio Bridge", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => StopEngine();

    private void StopEngine()
    {
        try { _engine?.Dispose(); } catch { }
        _engine = null;
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        BufferSizeCombo.IsEnabled = true;
        StateText.Text = "STOPPED";
        StateText.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted");
        StatusText.Text = "Stopped.";
    }

    private void ApplyMixSettings()
    {
        if (_engine is null) return;
        _engine.Line1Gain = (float)Line1Gain.Value;
        _engine.Line2Gain = (float)Line2Gain.Value;
        _engine.MicGain = (float)MicGain.Value;
        _engine.Line1Pan = (float)Line1Pan.Value;
        _engine.Line2Pan = (float)Line2Pan.Value;
        _engine.MasterGain = (float)MasterGain.Value;
    }

    protected override void OnClosed(EventArgs e)
    {
        StopEngine();
        base.OnClosed(e);
    }

    private sealed record DeviceChoice(int Index, string Name)
    {
        public override string ToString() => $"{Index}: {Name}";
    }
}
