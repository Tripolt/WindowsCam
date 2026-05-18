using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace WindowsCamReceiver;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private const int DevicePort = 48650;
    private const int LocalPort = 48650;
    private const int ObsPort = 48651;
    private const string ObsUrl = $"udp://127.0.0.1:{ObsPort}";

    private readonly Label _statusLabel = new();
    private readonly Label _deviceLabel = new();
    private readonly Label _streamLabel = new();
    private readonly TextBox _obsUrlBox = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _copyObsButton = new();
    private readonly Button _openGuideButton = new();
    private readonly TextBox _logBox = new();

    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _runToken;
    private Process? _iproxy;
    private Process? _ffmpeg;
    private Task? _receiveTask;

    public MainForm()
    {
        Text = "WindowsCam Receiver";
        Width = 680;
        Height = 470;
        MinimumSize = new Size(560, 390);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 8
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(layout, 0, "Status", _statusLabel);
        AddRow(layout, 1, "Device", _deviceLabel);
        AddRow(layout, 2, "Stream", _streamLabel);
        AddRow(layout, 3, "OBS URL", _obsUrlBox);

        _statusLabel.Text = "Idle";
        _deviceLabel.Text = "No device checked yet";
        _streamLabel.Text = "No stream";
        _obsUrlBox.Text = ObsUrl;
        _obsUrlBox.ReadOnly = true;
        _obsUrlBox.Dock = DockStyle.Fill;

        var obsActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true
        };

        _copyObsButton.Text = "Copy OBS URL";
        _copyObsButton.Width = 130;
        _copyObsButton.Click += (_, _) =>
        {
            Clipboard.SetText(ObsUrl);
            Log("OBS URL copied.");
        };

        _openGuideButton.Text = "Open Guide";
        _openGuideButton.Width = 110;
        _openGuideButton.Click += (_, _) => OpenGuide();

        obsActions.Controls.Add(_copyObsButton);
        obsActions.Controls.Add(_openGuideButton);
        layout.Controls.Add(new Label { Text = "", Dock = DockStyle.Fill }, 0, 4);
        layout.Controls.Add(obsActions, 1, 4);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true
        };

        _startButton.Text = "Start";
        _startButton.Width = 110;
        _startButton.Click += (_, _) => StartReceiver();

        _stopButton.Text = "Stop";
        _stopButton.Width = 110;
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => StopReceiver();

        buttons.Controls.Add(_startButton);
        buttons.Controls.Add(_stopButton);

        layout.Controls.Add(new Label { Text = "", Dock = DockStyle.Fill }, 0, 5);
        layout.Controls.Add(buttons, 1, 5);

        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Dock = DockStyle.Fill;
        _logBox.Font = new Font(FontFamily.GenericMonospace, 9);
        layout.SetColumnSpan(_logBox, 2);
        layout.Controls.Add(_logBox, 0, 7);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Controls.Add(layout);
        Shown += (_, _) => LogMissingOptionalTools();
        FormClosing += (_, _) =>
        {
            _closing.Cancel();
            StopReceiver();
        };
    }

    private static void AddRow(TableLayoutPanel layout, int row, string title, Control value)
    {
        layout.Controls.Add(new Label
        {
            Text = title,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill
        }, 0, row);

        value.TextAlignIfLabel(ContentAlignment.MiddleLeft);
        value.Dock = DockStyle.Fill;
        layout.Controls.Add(value, 1, row);
    }

    private void StartReceiver()
    {
        if (_runToken is not null)
        {
            return;
        }

        _runToken = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _startButton.Enabled = false;
        _stopButton.Enabled = true;
        SetStatus("Starting");
        _receiveTask = Task.Run(() => RunAsync(_runToken.Token));
    }

    private void OpenGuide()
    {
        var guidePath = Path.Combine(AppContext.BaseDirectory, "WINDOWS_INSTALL.md");
        if (!File.Exists(guidePath))
        {
            guidePath = Path.Combine(AppContext.BaseDirectory, "README.md");
        }

        if (File.Exists(guidePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = guidePath,
                UseShellExecute = true
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://obsproject.com/kb/media-source",
                UseShellExecute = true
            });
        }
    }

    private void StopReceiver(bool updateStatus = true)
    {
        _runToken?.Cancel();
        _runToken = null;
        KillProcess(_ffmpeg);
        KillProcess(_iproxy);
        _ffmpeg = null;
        _iproxy = null;
        _startButton.Enabled = true;
        _stopButton.Enabled = false;
        if (updateStatus)
        {
            SetStatus("Stopped");
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            var device = await DetectDeviceAsync(token);
            InvokeUi(() => _deviceLabel.Text = device);

            _iproxy = StartProcess(FindTool("iproxy")!, $"{LocalPort} {DevicePort}", "iproxy");
            await Task.Delay(1000, token);

            _ffmpeg = StartProcess(FindTool("ffmpeg")!,
                $"-hide_banner -loglevel warning -fflags nobuffer -flags low_delay -f h264 -i pipe:0 -an -c:v copy -f mpegts \"{ObsUrl}?pkt_size=1316\"",
                "ffmpeg");

            SetStatus("Connecting to iPhone app");
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", LocalPort, token);
            await using var stream = client.GetStream();

            var helloBytes = await ReadPacketAsync(stream, token);
            var hello = JsonSerializer.Deserialize<StreamHello>(helloBytes, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (hello is not null)
            {
                InvokeUi(() => _streamLabel.Text = $"{hello.Codec} {hello.Width}x{hello.Height} {hello.Fps}fps");
            }

            SetStatus("Streaming to OBS");
            Log($"Add an OBS Media Source with input: {ObsUrl}");

            var ffmpegInput = _ffmpeg.StandardInput.BaseStream;
            while (!token.IsCancellationRequested)
            {
                var frame = await ReadPacketAsync(stream, token);
                await ffmpegInput.WriteAsync(frame, token);
                await ffmpegInput.FlushAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            Log("Receiver stopped.");
        }
        catch (Exception ex)
        {
            SetStatus("Error");
            Log(ex.Message);
            Log("Check that the iPhone is trusted, the iPhone app is open, and the required tools are installed.");
        }
        finally
        {
            InvokeUi(() => StopReceiver(updateStatus: false));
        }
    }

    private async Task<string> DetectDeviceAsync(CancellationToken token)
    {
        SetStatus("Checking iPhone");

        var ideviceId = FindTool("idevice_id", required: false);
        if (ideviceId is null)
        {
            Log("idevice_id.exe not found. Continuing with iproxy only.");
            return "Unknown iPhone";
        }

        var udids = await CaptureProcessAsync(ideviceId, "-l", token);
        var udid = udids.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(udid))
        {
            throw new InvalidOperationException("No trusted iPhone found over USB.");
        }

        var ideviceName = FindTool("idevicename", required: false);
        if (ideviceName is null)
        {
            return udid;
        }

        var name = await CaptureProcessAsync(ideviceName, $"-u {udid}", token);
        name = name.Trim();
        return string.IsNullOrWhiteSpace(name) ? udid : $"{name} ({udid})";
    }

    private static async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken token)
    {
        var lengthBytes = await ReadExactlyAsync(stream, 4, token);
        var length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        if (length == 0 || length > 64 * 1024 * 1024)
        {
            throw new InvalidDataException($"Invalid packet length: {length}");
        }

        return await ReadExactlyAsync(stream, checked((int)length), token);
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int length, CancellationToken token)
    {
        var buffer = new byte[length];
        var offset = 0;

        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), token);
            if (read == 0)
            {
                throw new EndOfStreamException("The iPhone stream disconnected.");
            }

            offset += read;
        }

        return buffer;
    }

    private Process StartProcess(string fileName, string arguments, string label)
    {
        Log($"Starting {label}: {fileName} {arguments}");
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = label == "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log($"{label}: {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log($"{label}: {e.Data}"); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private async Task<string> CaptureProcessAsync(string fileName, string arguments, CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(token);
        var error = await process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(error.Trim().Length == 0 ? $"{Path.GetFileName(fileName)} exited with {process.ExitCode}" : error.Trim());
        }

        return output;
    }

    private string? FindTool(string name, bool required = true)
    {
        var executableName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.exe";
        var localPath = Path.Combine(AppContext.BaseDirectory, "Tools", executableName);
        if (File.Exists(localPath))
        {
            return localPath;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory.Trim(), executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (required)
        {
            throw new FileNotFoundException($"{executableName} was not found in PATH or the app's Tools folder.");
        }

        return null;
    }

    private void LogMissingOptionalTools()
    {
        var requiredTools = new[] { "iproxy", "ffmpeg" };
        foreach (var tool in requiredTools)
        {
            if (FindTool(tool, required: false) is null)
            {
                Log($"{tool}.exe not found. Put it in the app's Tools folder or add it to PATH.");
            }
        }
    }

    private static void KillProcess(Process? process)
    {
        if (process is null || process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may already be exiting.
        }
    }

    private void SetStatus(string status)
    {
        InvokeUi(() => _statusLabel.Text = status);
        Log(status);
    }

    private void Log(string message)
    {
        if (IsDisposed)
        {
            return;
        }

        InvokeUi(() =>
        {
            _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        });
    }

    private void InvokeUi(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(action);
        }
        else
        {
            action();
        }
    }
}

internal sealed class StreamHello
{
    public int Version { get; set; }
    public string Codec { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }
    public string Orientation { get; set; } = "";
    public string Framing { get; set; } = "";
}

internal static class ControlExtensions
{
    public static void TextAlignIfLabel(this Control control, ContentAlignment alignment)
    {
        if (control is Label label)
        {
            label.TextAlign = alignment;
        }
    }
}
