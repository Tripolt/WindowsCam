using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

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
    private const string CameraName = "WindowsCam";
    private const string SourceClsid = "{D9E25520-0B1B-4CE2-9C8E-6F9B4698B1D5}";

    private readonly Label _statusLabel = new();
    private readonly Label _cameraLabel = new();
    private readonly Label _deviceLabel = new();
    private readonly Label _streamLabel = new();
    private readonly Label _linkLabel = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _repairCameraButton = new();
    private readonly Button _removeCameraButton = new();
    private readonly Button _openGuideButton = new();
    private readonly TextBox _logBox = new();

    private readonly CancellationTokenSource _closing = new();
    private readonly StringBuilder _iproxyLog = new();
    private CancellationTokenSource? _runToken;
    private Process? _iproxy;
    private Task? _receiveTask;
    private DateTime _lastThroughputWarningUtc = DateTime.MinValue;

    public MainForm()
    {
        Text = "WindowsCam";
        Width = 780;
        Height = 560;
        MinimumSize = new Size(650, 460);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 9
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(layout, 0, "Status", _statusLabel);
        AddRow(layout, 1, "Camera", _cameraLabel);
        AddRow(layout, 2, "Device", _deviceLabel);
        AddRow(layout, 3, "Input", _streamLabel);
        AddRow(layout, 4, "Raw Link", _linkLabel);

        _statusLabel.Text = "Idle";
        _cameraLabel.Text = "Checking registration";
        _deviceLabel.Text = "No device checked yet";
        _streamLabel.Text = "No iPhone stream";
        _linkLabel.Text = "No raw frames yet";

        var cameraActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true
        };

        _repairCameraButton.Text = "Repair Camera";
        _repairCameraButton.Width = 135;
        _repairCameraButton.Click += async (_, _) => await RunVirtualCameraToolAsync("register");

        _removeCameraButton.Text = "Remove Camera";
        _removeCameraButton.Width = 125;
        _removeCameraButton.Click += async (_, _) => await RunVirtualCameraToolAsync("remove");

        _openGuideButton.Text = "Open Guide";
        _openGuideButton.Width = 110;
        _openGuideButton.Click += (_, _) => OpenGuide();

        cameraActions.Controls.Add(_repairCameraButton);
        cameraActions.Controls.Add(_removeCameraButton);
        cameraActions.Controls.Add(_openGuideButton);
        layout.Controls.Add(new Label { Text = "", Dock = DockStyle.Fill }, 0, 5);
        layout.Controls.Add(cameraActions, 1, 5);

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

        layout.Controls.Add(new Label { Text = "", Dock = DockStyle.Fill }, 0, 6);
        layout.Controls.Add(buttons, 1, 6);

        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Dock = DockStyle.Fill;
        _logBox.Font = new Font(FontFamily.GenericMonospace, 9);
        layout.SetColumnSpan(_logBox, 2);
        layout.Controls.Add(_logBox, 0, 8);

        for (var i = 0; i < 5; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        }

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Controls.Add(layout);
        Shown += (_, _) =>
        {
            LogStartupReadiness();
            StartReceiver();
        };
        FormClosing += (_, _) =>
        {
            _closing.Cancel();
            StopReceiver(updateStatus: false);
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
        _receiveTask = Task.Run(() => RunSupervisorAsync(_runToken.Token));
    }

    private void StopReceiver(bool updateStatus = true)
    {
        var runToken = _runToken;
        _runToken = null;
        runToken?.Cancel();
        CleanupChildProcesses();

        _startButton.Enabled = true;
        _stopButton.Enabled = false;
        if (updateStatus)
        {
            SetStatus("Stopped");
        }
    }

    private async Task RunSupervisorAsync(CancellationToken token)
    {
        var reconnectDelay = TimeSpan.FromSeconds(1);

        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await RunSingleSessionAsync(token);
                    reconnectDelay = TimeSpan.FromSeconds(1);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (EndOfStreamException ex)
                {
                    SetStatus("Disconnected");
                    Log(ex.Message);
                }
                catch (Exception ex)
                {
                    SetStatus("Recovering");
                    Log(ex.Message);
                    Log("WindowsCam will keep retrying. Check iPhone trust, the cable, and that the iOS app is open.");
                }
                finally
                {
                    CleanupChildProcesses();
                }

                if (!token.IsCancellationRequested)
                {
                    Log($"Retrying raw link in {reconnectDelay.TotalSeconds:0}s.");
                    await Task.Delay(reconnectDelay, token);
                    reconnectDelay = TimeSpan.FromSeconds(Math.Min(reconnectDelay.TotalSeconds * 1.5, 8));
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("Receiver stopped.");
        }
        finally
        {
            CleanupChildProcesses();
            InvokeUi(() =>
            {
                if (_runToken is not null && _runToken.IsCancellationRequested)
                {
                    _runToken = null;
                }

                _startButton.Enabled = _runToken is null;
                _stopButton.Enabled = _runToken is not null;
            });
        }
    }

    private async Task RunSingleSessionAsync(CancellationToken token)
    {
        var device = await DetectDeviceAsync(token);
        InvokeUi(() => _deviceLabel.Text = device);

        _iproxy = StartProcess(FindTool("iproxy")!, $"{LocalPort}:{DevicePort}", "iproxy");
        await Task.Delay(750, token);
        ThrowIfExited(_iproxy, "iproxy", _iproxyLog);

        SetStatus("Connecting to iPhone app");
        using var client = new TcpClient { NoDelay = true, ReceiveBufferSize = 16 * 1024 * 1024 };
        await client.ConnectAsync("127.0.0.1", LocalPort, token);
        client.NoDelay = true;

        await using var stream = client.GetStream();
        var helloBytes = await ReadPacketAsync(stream, token);
        var hello = JsonSerializer.Deserialize<StreamHello>(helloBytes, JsonOptions.Default)
            ?? throw new InvalidDataException("The iPhone stream did not send camera metadata.");

        if (!string.Equals(hello.Codec, "nv12-raw", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsupported codec '{hello.Codec}'. This build is raw NV12 only.");
        }

        var mode = CameraMode.FromHello(hello);
        InvokeUi(() =>
        {
            _streamLabel.Text = $"{hello.PixelFormatOrDefault} {mode.Width}x{mode.Height} {mode.Fps}fps";
            _linkLabel.Text = $"Need {mode.RequiredMegabytesPerSecond:0} MB/s";
        });

        using var broker = new LatestFrameBroker(mode);
        broker.PublishPlaceholder();

        SetStatus("Feeding WindowsCam");
        Log($"Raw NV12 mode {mode.Width}x{mode.Height}@{mode.Fps}: {mode.RequiredMegabytesPerSecond:0.0} MB/s required.");
        Log($"Select '{CameraName}' in Teams, Zoom, a browser, or OBS Video Capture Device.");

        await PumpRawNv12FramesAsync(stream, broker, mode, Math.Max(hello.FrameHeaderBytes, RawFrameHeader.MinimumHeaderBytes), token);
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

        string udids;
        try
        {
            udids = await CaptureProcessAsync(ideviceId, "-l", token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"idevice_id.exe could not list devices: {ex.Message}");
            Log("Continuing with iproxy. If connection still fails, install Apple Devices or iTunes so Windows has Apple's USB driver.");
            return "Unknown iPhone";
        }

        var udid = udids.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(udid))
        {
            Log("idevice_id.exe did not report a trusted iPhone. Continuing with iproxy.");
            return "Unknown iPhone";
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

    private async Task PumpRawNv12FramesAsync(NetworkStream stream, LatestFrameBroker broker, CameraMode mode, int frameHeaderBytes, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(mode.Nv12FrameBytes + frameHeaderBytes);
        var stats = new RawLinkStats(mode);

        try
        {
            while (!token.IsCancellationRequested)
            {
                var packetLength = await ReadPacketIntoAsync(stream, buffer, token);
                var frameOffset = 0;
                var frameBytes = packetLength;
                RawFrameHeader? rawHeader = null;

                if (RawFrameHeader.TryRead(buffer.AsSpan(0, packetLength), out var header))
                {
                    rawHeader = header;
                    frameOffset = header.HeaderBytes;
                    frameBytes = header.PayloadBytes;

                    if (header.Width != mode.Width || header.Height != mode.Height || header.Fps != mode.Fps || header.LumaStride != mode.Width)
                    {
                        stats.MalformedFrames++;
                        Log($"Dropped raw frame with mismatched header {header.Width}x{header.Height}@{header.Fps}, stride {header.LumaStride}; expected {mode.Width}x{mode.Height}@{mode.Fps}.");
                        continue;
                    }

                    if (packetLength != header.HeaderBytes + header.PayloadBytes)
                    {
                        stats.MalformedFrames++;
                        Log($"Dropped raw frame with invalid packet size {packetLength}; header says {header.PayloadBytes} payload bytes.");
                        continue;
                    }
                }
                else if (packetLength != mode.Nv12FrameBytes)
                {
                    stats.MalformedFrames++;
                    Log($"Dropped malformed raw frame: expected {mode.Nv12FrameBytes} bytes or raw-v2 header but received {packetLength}.");
                    continue;
                }

                if (frameBytes != mode.Nv12FrameBytes)
                {
                    stats.MalformedFrames++;
                    Log($"Dropped raw frame: expected {mode.Nv12FrameBytes} NV12 bytes but received {frameBytes}.");
                    continue;
                }

                broker.PublishFrame(buffer, frameOffset, frameBytes);
                stats.RecordFrame(packetLength, rawHeader?.Sequence ?? 0);

                if (stats.ShouldPublish(DateTime.UtcNow, out var report))
                {
                    InvokeUi(() => _linkLabel.Text = report.Summary);
                    if (report.IsThroughputLow && DateTime.UtcNow - _lastThroughputWarningUtc > TimeSpan.FromSeconds(10))
                    {
                        _lastThroughputWarningUtc = DateTime.UtcNow;
                        Log(report.Warning);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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

    private static async Task<int> ReadPacketIntoAsync(NetworkStream stream, byte[] buffer, CancellationToken token)
    {
        var lengthBytes = await ReadExactlyAsync(stream, 4, token);
        var length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        if (length == 0 || length > 64 * 1024 * 1024)
        {
            throw new InvalidDataException($"Invalid packet length: {length}");
        }

        if (length > buffer.Length)
        {
            throw new InvalidDataException($"Raw packet length {length} exceeds receiver buffer {buffer.Length}.");
        }

        await ReadExactlyAsync(stream, buffer.AsMemory(0, checked((int)length)), token);
        return checked((int)length);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length, CancellationToken token)
    {
        var buffer = new byte[length];
        await ReadExactlyAsync(stream, buffer.AsMemory(), token);
        return buffer;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], token);
            if (read == 0)
            {
                throw new EndOfStreamException("The raw video stream disconnected.");
            }

            offset += read;
        }
    }

    private void OpenGuide()
    {
        var guidePath = Path.Combine(AppContext.BaseDirectory, "WINDOWS_INSTALL.md");
        if (!File.Exists(guidePath))
        {
            guidePath = Path.Combine(AppContext.BaseDirectory, "README.md");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = File.Exists(guidePath) ? guidePath : "ms-settings:privacy-webcam",
            UseShellExecute = true
        });
    }

    private Process StartProcess(string fileName, string arguments, string label)
    {
        lock (_iproxyLog)
        {
            _iproxyLog.Clear();
        }

        Log($"Starting {label}: {fileName} {arguments}");
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) => AppendToolLog(_iproxyLog, label, e.Data);
        process.ErrorDataReceived += (_, e) => AppendToolLog(_iproxyLog, label, e.Data);
        process.Exited += (_, _) =>
        {
            try
            {
                Log($"{label} exited with code {process.ExitCode}.");
            }
            catch
            {
                Log($"{label} exited.");
            }
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private async Task RunVirtualCameraToolAsync(string command)
    {
        var toolPath = FindTool("WindowsCam.VirtualCamera.Tool", required: false);
        if (toolPath is null)
        {
            Log("WindowsCam.VirtualCamera.Tool.exe is not installed yet. Build and bundle the native virtual camera tool before repairing the camera.");
            return;
        }

        try
        {
            SetCameraButtons(false);
            var output = await CaptureProcessAsync(toolPath, command, _closing.Token);
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Log(line);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"Virtual camera {command} failed: {ex.Message}");
        }
        finally
        {
            SetCameraButtons(true);
            CheckCameraRegistration();
        }
    }

    private void SetCameraButtons(bool enabled)
    {
        InvokeUi(() =>
        {
            _repairCameraButton.Enabled = enabled;
            _removeCameraButton.Enabled = enabled;
        });
    }

    private void AppendToolLog(StringBuilder buffer, string label, string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return;
        }

        lock (buffer)
        {
            buffer.AppendLine(data);
            if (buffer.Length > 6000)
            {
                buffer.Remove(0, buffer.Length - 6000);
            }
        }

        Log($"{label}: {data}");
    }

    private static void ThrowIfExited(Process process, string label, StringBuilder logBuffer)
    {
        if (!process.HasExited)
        {
            return;
        }

        var details = "";
        lock (logBuffer)
        {
            details = logBuffer.ToString().Trim();
        }

        throw new InvalidOperationException(details.Length == 0
            ? $"{label} exited with code {process.ExitCode}."
            : $"{label} exited with code {process.ExitCode}: {details}");
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

        var appPath = Path.Combine(AppContext.BaseDirectory, executableName);
        if (File.Exists(appPath))
        {
            return appPath;
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
            throw new FileNotFoundException($"{executableName} was not found in PATH, the app folder, or the app's Tools folder.");
        }

        return null;
    }

    private void LogStartupReadiness()
    {
        CheckCameraRegistration();

        if (FindTool("iproxy", required: false) is null)
        {
            Log("iproxy.exe not found. Put it in the app's Tools folder or add it to PATH.");
        }

        if (FindTool("WindowsCam.VirtualCamera.Tool", required: false) is null)
        {
            Log("Native virtual camera repair tool not found. The receiver can read raw frames, but Windows apps may not see the camera.");
        }
    }

    private void CheckCameraRegistration()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"Software\Classes\CLSID\{SourceClsid}\InprocServer32");
            var value = key?.GetValue(null) as string;
            var path = string.IsNullOrWhiteSpace(value) ? "" : Environment.ExpandEnvironmentVariables(value);

            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                InvokeUi(() => _cameraLabel.Text = $"Registered ({path})");
                return;
            }

            InvokeUi(() => _cameraLabel.Text = "Not registered - click Repair Camera");
        }
        catch (Exception ex)
        {
            InvokeUi(() => _cameraLabel.Text = "Registration check failed");
            Log($"Could not check virtual camera registration: {ex.Message}");
        }
    }

    private void CleanupChildProcesses()
    {
        KillProcess(_iproxy);
        _iproxy = null;
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
        if (IsDisposed || !IsHandleCreated)
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
        if (IsDisposed || !IsHandleCreated)
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
    public string PixelFormat { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }
    public int LumaStride { get; set; }
    public int BytesPerFrame { get; set; }
    public int BytesPerSecond { get; set; }
    public int FrameHeaderBytes { get; set; }
    public string Orientation { get; set; } = "";
    public string Framing { get; set; } = "";

    public string PixelFormatOrDefault => string.IsNullOrWhiteSpace(PixelFormat) ? "nv12" : PixelFormat;
}

internal readonly record struct CameraMode(int Width, int Height, int Fps)
{
    public int Nv12FrameBytes => checked(Width * Height * 3 / 2);
    public double RequiredMegabytesPerSecond => Nv12FrameBytes * (double)Fps / 1_000_000;

    public static CameraMode FromHello(StreamHello hello)
    {
        var fps = hello.Fps > 0 ? hello.Fps : 30;
        var mode = new CameraMode(hello.Width, hello.Height, fps);

        if (mode.Width <= 0 || mode.Height <= 0 || mode.Width % 2 != 0 || mode.Height % 2 != 0)
        {
            throw new InvalidDataException($"Invalid raw frame size in hello: {mode.Width}x{mode.Height}.");
        }

        if (mode.Fps is < 1 or > 120)
        {
            throw new InvalidDataException($"Invalid raw frame rate in hello: {mode.Fps}.");
        }

        if (hello.LumaStride > 0 && hello.LumaStride != mode.Width)
        {
            throw new InvalidDataException($"Unsupported luma stride {hello.LumaStride}; packed NV12 stride must equal width.");
        }

        if (hello.BytesPerFrame > 0 && hello.BytesPerFrame != mode.Nv12FrameBytes)
        {
            throw new InvalidDataException($"Hello says {hello.BytesPerFrame} bytes/frame, expected {mode.Nv12FrameBytes} for {mode.Width}x{mode.Height} NV12.");
        }

        return mode;
    }
}

internal readonly record struct RawFrameHeader(
    int HeaderBytes,
    int Width,
    int Height,
    int Fps,
    int LumaStride,
    int PayloadBytes,
    ulong Sequence,
    ulong CapturedAtNs)
{
    public const int MinimumHeaderBytes = 48;
    private const uint Magic = 0x5746524D; // WFRM
    private const ushort Version = 2;

    public static bool TryRead(ReadOnlySpan<byte> packet, out RawFrameHeader header)
    {
        header = default;
        if (packet.Length < MinimumHeaderBytes)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(packet[..4]) != Magic)
        {
            return false;
        }

        var version = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(4, 2));
        var headerBytes = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2));
        if (version != Version || headerBytes < MinimumHeaderBytes || headerBytes > packet.Length)
        {
            return false;
        }

        var width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(8, 4)));
        var height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(12, 4)));
        var fps = checked((int)BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(16, 4)));
        var lumaStride = checked((int)BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(20, 4)));
        var payloadBytes = checked((int)BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(24, 4)));
        var sequence = BinaryPrimitives.ReadUInt64BigEndian(packet.Slice(28, 8));
        var capturedAtNs = BinaryPrimitives.ReadUInt64BigEndian(packet.Slice(36, 8));

        header = new RawFrameHeader(headerBytes, width, height, fps, lumaStride, payloadBytes, sequence, capturedAtNs);
        return true;
    }
}

internal sealed class RawLinkStats
{
    private readonly CameraMode _mode;
    private readonly Stopwatch _window = Stopwatch.StartNew();
    private long _frames;
    private long _bytes;
    private ulong _lastSequence;

    public long MalformedFrames { get; set; }
    public long MissedFrames { get; private set; }

    public RawLinkStats(CameraMode mode)
    {
        _mode = mode;
    }

    public void RecordFrame(int packetBytes, ulong sequence)
    {
        _frames++;
        _bytes += packetBytes;
        if (sequence > 0 && _lastSequence > 0 && sequence > _lastSequence + 1)
        {
            MissedFrames += checked((long)(sequence - _lastSequence - 1));
        }

        if (sequence > 0)
        {
            _lastSequence = sequence;
        }
    }

    public bool ShouldPublish(DateTime now, out RawLinkReport report)
    {
        _ = now;
        report = default;
        if (_window.Elapsed < TimeSpan.FromSeconds(1))
        {
            return false;
        }

        var seconds = Math.Max(_window.Elapsed.TotalSeconds, 0.001);
        var fps = _frames / seconds;
        var megabytesPerSecond = _bytes / seconds / 1_000_000;
        var required = _mode.RequiredMegabytesPerSecond;
        var low = fps < _mode.Fps * 0.90 || megabytesPerSecond < required * 0.90;

        report = new RawLinkReport(
            Summary: $"{megabytesPerSecond:0} MB/s {fps:0.0} fps (need {required:0})",
            Warning: $"USB throughput warning: measured {megabytesPerSecond:0.0} MB/s and {fps:0.0} fps for {_mode.Width}x{_mode.Height}@{_mode.Fps}. A cable, hub, port, or iPhone capture path is not sustaining raw NV12 in real time.",
            IsThroughputLow: low);

        _frames = 0;
        _bytes = 0;
        _window.Restart();
        return true;
    }
}

internal readonly record struct RawLinkReport(string Summary, string Warning, bool IsThroughputLow);

internal sealed class LatestFrameBroker : IDisposable
{
    public const int HeaderBytes = 64;
    private const ulong Magic = 0x5743414D4652414D; // WCAMFRAM
    private const int Version = 1;

    private readonly CameraMode _mode;
    private readonly MemoryMappedFile _mappedFile;
    private readonly MemoryMappedViewAccessor _accessor;
    private long _sequence;

    public LatestFrameBroker(CameraMode mode)
    {
        _mode = mode;
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WindowsCam");
        Directory.CreateDirectory(directory);

        var filePath = Path.Combine(directory, "latest-frame.mmf");
        var totalBytes = HeaderBytes + mode.Nv12FrameBytes;
        using (var file = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            file.SetLength(totalBytes);
        }

        _mappedFile = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, totalBytes, MemoryMappedFileAccess.ReadWrite);
        _accessor = _mappedFile.CreateViewAccessor(0, totalBytes, MemoryMappedFileAccess.ReadWrite);
    }

    public void PublishPlaceholder()
    {
        var frame = new byte[_mode.Nv12FrameBytes];
        var yBytes = _mode.Width * _mode.Height;
        Array.Fill<byte>(frame, 16, 0, yBytes);
        Array.Fill<byte>(frame, 128, yBytes, frame.Length - yBytes);
        PublishFrame(frame, 0, frame.Length);
    }

    public void PublishFrame(byte[] nv12Frame, int offset, int count)
    {
        if (count != _mode.Nv12FrameBytes)
        {
            throw new InvalidDataException($"Expected {_mode.Nv12FrameBytes} NV12 bytes but received {count}.");
        }

        var sequence = Interlocked.Increment(ref _sequence);
        _accessor.Write(32, 0L);
        _accessor.WriteArray(HeaderBytes, nv12Frame, offset, count);
        _accessor.Write(0, Magic);
        _accessor.Write(8, Version);
        _accessor.Write(12, _mode.Width);
        _accessor.Write(16, _mode.Height);
        _accessor.Write(20, _mode.Fps);
        _accessor.Write(24, _mode.Width);
        _accessor.Write(28, count);
        _accessor.Write(40, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _accessor.Write(32, sequence);
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mappedFile.Dispose();
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true
    };
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
