using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net.Sockets;
using System.Text;
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
    private const string CameraName = "WindowsCam";

    private readonly Label _statusLabel = new();
    private readonly Label _cameraLabel = new();
    private readonly Label _deviceLabel = new();
    private readonly Label _streamLabel = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _registerCameraButton = new();
    private readonly Button _removeCameraButton = new();
    private readonly Button _openGuideButton = new();
    private readonly TextBox _logBox = new();

    private readonly CancellationTokenSource _closing = new();
    private readonly StringBuilder _iproxyLog = new();
    private readonly StringBuilder _ffmpegLog = new();
    private CancellationTokenSource? _runToken;
    private Process? _iproxy;
    private Process? _ffmpeg;
    private Task? _receiveTask;
    private long _droppedFrames;

    public MainForm()
    {
        Text = "WindowsCam";
        Width = 700;
        Height = 500;
        MinimumSize = new Size(580, 420);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 8
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(layout, 0, "Status", _statusLabel);
        AddRow(layout, 1, "Camera", _cameraLabel);
        AddRow(layout, 2, "Device", _deviceLabel);
        AddRow(layout, 3, "Input", _streamLabel);

        _statusLabel.Text = "Idle";
        _cameraLabel.Text = "WindowsCam virtual camera";
        _deviceLabel.Text = "No device checked yet";
        _streamLabel.Text = "No iPhone stream";

        var cameraActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true
        };

        _registerCameraButton.Text = "Register Camera";
        _registerCameraButton.Width = 135;
        _registerCameraButton.Click += async (_, _) => await RunVirtualCameraToolAsync("register");

        _removeCameraButton.Text = "Remove Camera";
        _removeCameraButton.Width = 125;
        _removeCameraButton.Click += async (_, _) => await RunVirtualCameraToolAsync("remove");

        _openGuideButton.Text = "Open Guide";
        _openGuideButton.Width = 110;
        _openGuideButton.Click += (_, _) => OpenGuide();

        cameraActions.Controls.Add(_registerCameraButton);
        cameraActions.Controls.Add(_removeCameraButton);
        cameraActions.Controls.Add(_openGuideButton);
        layout.Controls.Add(new Label { Text = "", Dock = DockStyle.Fill }, 0, 4);
        layout.Controls.Add(cameraActions, 1, 4);

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
        Shown += (_, _) => LogStartupReadiness();
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
                FileName = "ms-settings:privacy-webcam",
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

            _iproxy = StartProcess(FindTool("iproxy")!, $"{LocalPort}:{DevicePort}", "iproxy");
            await Task.Delay(1000, token);
            ThrowIfExited(_iproxy, "iproxy", _iproxyLog);

            SetStatus("Connecting to iPhone app");
            using var client = new TcpClient();
            client.NoDelay = true;
            await client.ConnectAsync("127.0.0.1", LocalPort, token);
            client.NoDelay = true;
            await using var stream = client.GetStream();

            var helloBytes = await ReadPacketAsync(stream, token);
            var hello = JsonSerializer.Deserialize<StreamHello>(helloBytes, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidDataException("The iPhone stream did not send camera metadata.");

            var mode = CameraMode.FromHello(hello);
            InvokeUi(() => _streamLabel.Text = $"{hello.Codec} {mode.Width}x{mode.Height} {mode.Fps}fps");

            using var broker = new LatestFrameBroker(mode);
            broker.PublishPlaceholder();
            _droppedFrames = 0;

            _ffmpeg = StartProcess(
                FindTool("ffmpeg")!,
                BuildFfmpegDecodeArguments(mode),
                "ffmpeg",
                redirectStandardInput: true,
                redirectStandardOutput: true);

            await Task.Delay(500, token);
            ThrowIfExited(_ffmpeg, "ffmpeg", _ffmpegLog);

            SetStatus("Feeding WindowsCam");
            Log($"Select '{CameraName}' in Teams, Zoom, a browser, or OBS Video Capture Device.");
            await PumpDecodedFramesAsync(stream, _ffmpeg, broker, mode, token);
        }
        catch (OperationCanceledException)
        {
            Log("Receiver stopped.");
        }
        catch (EndOfStreamException)
        {
            SetStatus("Disconnected");
            Log("The iPhone stream ended. Keep the iPhone app open and reconnect the cable if needed.");
        }
        catch (Exception ex)
        {
            SetStatus("Error");
            Log(ex.Message);
            Log("Check that the iPhone is trusted, the iPhone app is open, and required tools/components are installed.");
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

    private async Task PumpDecodedFramesAsync(NetworkStream stream, Process ffmpeg, LatestFrameBroker broker, CameraMode mode, CancellationToken token)
    {
        using var pumpTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pumpToken = pumpTokenSource.Token;
        var encodedFrames = new LiveFrameMailbox();

        var reader = Task.Run(() => ReadEncodedFramesAsync(stream, encodedFrames, pumpToken), pumpToken);
        var decoderInput = Task.Run(() => WriteEncodedFramesAsync(encodedFrames, ffmpeg.StandardInput.BaseStream, pumpToken), pumpToken);
        var decoderOutput = Task.Run(() => PublishDecodedFramesAsync(ffmpeg.StandardOutput.BaseStream, broker, mode, pumpToken), pumpToken);

        try
        {
            await Task.WhenAny(reader, decoderInput, decoderOutput);
            await pumpTokenSource.CancelAsync();
            encodedFrames.Complete();
            await Task.WhenAll(reader, decoderInput, decoderOutput);
        }
        catch
        {
            await pumpTokenSource.CancelAsync();
            encodedFrames.Complete();
            throw;
        }
    }

    private async Task ReadEncodedFramesAsync(NetworkStream stream, LiveFrameMailbox frames, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var dropped = frames.Publish(new VideoFrame(await ReadPacketAsync(stream, token), Stopwatch.GetTimestamp()));
                if (dropped)
                {
                    var droppedFrames = Interlocked.Increment(ref _droppedFrames);
                    if (droppedFrames == 1 || droppedFrames % 30 == 0)
                    {
                        Log($"Dropped {droppedFrames} stale encoded frame(s) to keep latency live.");
                    }
                }
            }
        }
        finally
        {
            frames.Complete();
        }
    }

    private async Task WriteEncodedFramesAsync(LiveFrameMailbox frames, Stream ffmpegInput, CancellationToken token)
    {
        var waitingForKeyFrame = false;
        var lastDropCount = 0L;

        while (!token.IsCancellationRequested)
        {
            var frame = await frames.ReadAsync(token);
            if (frame is null)
            {
                return;
            }

            var dropCount = Interlocked.Read(ref _droppedFrames);
            if (dropCount != lastDropCount)
            {
                waitingForKeyFrame = true;
                lastDropCount = dropCount;
            }

            if (waitingForKeyFrame)
            {
                if (!H264AnnexB.IsKeyFrame(frame.Value.Data))
                {
                    continue;
                }

                waitingForKeyFrame = false;
                Log("Recovered decoder on next H.264 keyframe after dropping stale frames.");
            }

            await ffmpegInput.WriteAsync(frame.Value.Data, token);
            await ffmpegInput.FlushAsync(token);
        }
    }

    private async Task PublishDecodedFramesAsync(Stream ffmpegOutput, LatestFrameBroker broker, CameraMode mode, CancellationToken token)
    {
        var frame = new byte[mode.Nv12FrameBytes];
        var frameCount = 0L;

        while (!token.IsCancellationRequested)
        {
            await ReadExactlyAsync(ffmpegOutput, frame, token);
            broker.PublishFrame(frame);
            frameCount++;

            if (frameCount == 1 || frameCount % (mode.Fps * 10) == 0)
            {
                Log($"Published {frameCount} decoded NV12 frame(s) to WindowsCam at {mode.Width}x{mode.Height}.");
            }
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

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length, CancellationToken token)
    {
        var buffer = new byte[length];
        await ReadExactlyAsync(stream, buffer, token);
        return buffer;
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token);
            if (read == 0)
            {
                throw new EndOfStreamException("The video stream disconnected.");
            }

            offset += read;
        }
    }

    private static string BuildFfmpegDecodeArguments(CameraMode mode)
    {
        return "-hide_banner -loglevel error " +
            "-fflags nobuffer+genpts -flags low_delay -avioflags direct " +
            "-probesize 32 -analyzeduration 0 -fpsprobesize 0 " +
            $"-r {mode.Fps} -f h264 -i pipe:0 -an " +
            $"-vf scale={mode.Width}:{mode.Height}:flags=fast_bilinear,format=nv12 " +
            "-f rawvideo -pix_fmt nv12 pipe:1";
    }

    private Process StartProcess(
        string fileName,
        string arguments,
        string label,
        bool redirectStandardInput = false,
        bool redirectStandardOutput = false)
    {
        var logBuffer = label == "iproxy" ? _iproxyLog : _ffmpegLog;
        lock (logBuffer)
        {
            logBuffer.Clear();
        }

        Log($"Starting {label}: {fileName} {arguments}");
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = redirectStandardInput,
                RedirectStandardOutput = redirectStandardOutput || label != "ffmpeg",
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        if (!redirectStandardOutput)
        {
            process.OutputDataReceived += (_, e) => AppendToolLog(logBuffer, label, e.Data);
        }

        process.ErrorDataReceived += (_, e) => AppendToolLog(logBuffer, label, e.Data);
        process.Exited += (_, _) => Log($"{label} exited with code {process.ExitCode}.");
        process.Start();
        if (!redirectStandardOutput)
        {
            process.BeginOutputReadLine();
        }

        process.BeginErrorReadLine();
        return process;
    }

    private async Task RunVirtualCameraToolAsync(string command)
    {
        var toolPath = FindTool("WindowsCam.VirtualCamera.Tool", required: false);
        if (toolPath is null)
        {
            Log("WindowsCam.VirtualCamera.Tool.exe is not installed yet. Build and bundle the native virtual camera tool before registering the camera.");
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
        }
    }

    private void SetCameraButtons(bool enabled)
    {
        InvokeUi(() =>
        {
            _registerCameraButton.Enabled = enabled;
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
        foreach (var tool in new[] { "iproxy", "ffmpeg" })
        {
            if (FindTool(tool, required: false) is null)
            {
                Log($"{tool}.exe not found. Put it in the app's Tools folder or add it to PATH.");
            }
        }

        if (FindTool("WindowsCam.VirtualCamera.Tool", required: false) is null)
        {
            Log("Native virtual camera registration tool not found. The receiver can decode frames, but Windows apps will not see a camera until the native component is bundled.");
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

internal readonly record struct CameraMode(int Width, int Height, int Fps)
{
    public int Nv12FrameBytes => checked(Width * Height * 3 / 2);

    public static CameraMode FromHello(StreamHello hello)
    {
        var fps = hello.Fps > 0 ? hello.Fps : 30;
        var requested = new CameraMode(hello.Width, hello.Height, fps);

        return requested switch
        {
            { Width: >= 3840, Height: >= 2160 } => new CameraMode(3840, 2160, 30),
            { Width: >= 1920, Height: >= 1080 } => new CameraMode(1920, 1080, 30),
            _ => new CameraMode(1280, 720, 30)
        };
    }
}

internal sealed class LatestFrameBroker : IDisposable
{
    public const int HeaderBytes = 64;
    private const ulong Magic = 0x5743414D4652414D; // WCAMFRAM
    private const int Version = 1;

    private readonly CameraMode _mode;
    private readonly string _filePath;
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

        _filePath = Path.Combine(directory, "latest-frame.mmf");
        var totalBytes = HeaderBytes + mode.Nv12FrameBytes;
        using (var file = new FileStream(_filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            file.SetLength(totalBytes);
        }

        _mappedFile = MemoryMappedFile.CreateFromFile(_filePath, FileMode.Open, null, totalBytes, MemoryMappedFileAccess.ReadWrite);
        _accessor = _mappedFile.CreateViewAccessor(0, totalBytes, MemoryMappedFileAccess.ReadWrite);
    }

    public void PublishPlaceholder()
    {
        var frame = new byte[_mode.Nv12FrameBytes];
        var yBytes = _mode.Width * _mode.Height;
        Array.Fill<byte>(frame, 16, 0, yBytes);
        Array.Fill<byte>(frame, 128, yBytes, frame.Length - yBytes);
        PublishFrame(frame);
    }

    public void PublishFrame(byte[] nv12Frame)
    {
        if (nv12Frame.Length != _mode.Nv12FrameBytes)
        {
            throw new InvalidDataException($"Expected {_mode.Nv12FrameBytes} NV12 bytes but received {nv12Frame.Length}.");
        }

        var sequence = Interlocked.Increment(ref _sequence);
        _accessor.WriteArray(HeaderBytes, nv12Frame, 0, nv12Frame.Length);
        _accessor.Write(0, Magic);
        _accessor.Write(8, Version);
        _accessor.Write(12, _mode.Width);
        _accessor.Write(16, _mode.Height);
        _accessor.Write(20, _mode.Fps);
        _accessor.Write(24, _mode.Width);
        _accessor.Write(28, nv12Frame.Length);
        _accessor.Write(32, sequence);
        _accessor.Write(40, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _accessor.Flush();
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mappedFile.Dispose();
    }
}

internal sealed class LiveFrameMailbox
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private VideoFrame? _latest;
    private bool _completed;

    public bool Publish(VideoFrame frame)
    {
        var dropped = false;
        var shouldSignal = false;

        lock (_gate)
        {
            if (_completed)
            {
                return false;
            }

            dropped = _latest.HasValue;
            shouldSignal = !_latest.HasValue;
            _latest = frame;
        }

        if (shouldSignal)
        {
            _signal.Release();
        }

        return dropped;
    }

    public void Complete()
    {
        var shouldSignal = false;

        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            shouldSignal = !_latest.HasValue;
        }

        if (shouldSignal)
        {
            _signal.Release();
        }
    }

    public async ValueTask<VideoFrame?> ReadAsync(CancellationToken token)
    {
        while (true)
        {
            await _signal.WaitAsync(token);

            lock (_gate)
            {
                if (_latest.HasValue)
                {
                    var frame = _latest.Value;
                    _latest = null;
                    return frame;
                }

                if (_completed)
                {
                    return null;
                }
            }
        }
    }
}

internal readonly record struct VideoFrame(byte[] Data, long CapturedAt);

internal static class H264AnnexB
{
    public static bool IsKeyFrame(byte[] frame)
    {
        var data = frame.AsSpan();
        var offset = 0;

        while (TryFindStartCode(data, offset, out var startCodeOffset, out var nalOffset))
        {
            if (nalOffset >= data.Length)
            {
                return false;
            }

            var nalType = data[nalOffset] & 0x1F;
            if (nalType == 5)
            {
                return true;
            }

            offset = Math.Max(nalOffset + 1, startCodeOffset + 3);
        }

        return false;
    }

    private static bool TryFindStartCode(ReadOnlySpan<byte> data, int offset, out int startCodeOffset, out int nalOffset)
    {
        for (var i = offset; i + 3 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0)
            {
                continue;
            }

            if (data[i + 2] == 1)
            {
                startCodeOffset = i;
                nalOffset = i + 3;
                return true;
            }

            if (i + 4 < data.Length && data[i + 2] == 0 && data[i + 3] == 1)
            {
                startCodeOffset = i;
                nalOffset = i + 4;
                return true;
            }
        }

        startCodeOffset = -1;
        nalOffset = -1;
        return false;
    }
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
