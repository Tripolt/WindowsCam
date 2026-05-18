using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

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
    private const string ObsUrl = "udp://127.0.0.1:48651";

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
    private readonly StringBuilder _iproxyLog = new();
    private readonly StringBuilder _ffmpegLog = new();
    private CancellationTokenSource? _runToken;
    private Process? _iproxy;
    private Process? _ffmpeg;
    private Task? _receiveTask;
    private long _droppedFrames;

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
            });
            if (hello is not null)
            {
                InvokeUi(() => _streamLabel.Text = $"{hello.Codec} {hello.Width}x{hello.Height} {hello.Fps}fps");
            }

            _droppedFrames = 0;
            _ffmpeg = StartProcess(FindTool("ffmpeg")!, BuildFfmpegArguments(), "ffmpeg");
            await Task.Delay(500, token);
            ThrowIfExited(_ffmpeg, "ffmpeg", _ffmpegLog);

            SetStatus("Streaming to OBS");
            Log($"Add an OBS Media Source with input: {ObsUrl}");

            var ffmpegInput = _ffmpeg.StandardInput.BaseStream;
            await PumpLiveFramesAsync(stream, ffmpegInput, token);
        }
        catch (OperationCanceledException)
        {
            Log("Receiver stopped.");
        }
        catch (EndOfStreamException)
        {
            SetStatus("Error");
            Log("The USB proxy connected, but the iPhone closed the stream before sending metadata.");
            Log("On the iPhone, the app should show USB port 48650 before you press Start here, then OBS client(s) after connecting.");
            Log("If idevice_id.exe still cannot list devices, install Apple Devices or iTunes from Apple so Windows has Apple Mobile Device support.");
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

    private async Task PumpLiveFramesAsync(NetworkStream stream, Stream ffmpegInput, CancellationToken token)
    {
        using var pumpTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pumpToken = pumpTokenSource.Token;
        var frames = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        var stopwatch = Stopwatch.StartNew();
        var reader = Task.Run(() => ReadFramesAsync(stream, frames, stopwatch, pumpToken), pumpToken);
        var writer = Task.Run(() => WriteFramesAsync(frames.Reader, ffmpegInput, stopwatch, pumpToken), pumpToken);

        try
        {
            await Task.WhenAny(reader, writer);
            await pumpTokenSource.CancelAsync();
            await Task.WhenAll(reader, writer);
        }
        catch
        {
            await pumpTokenSource.CancelAsync();
            frames.Writer.TryComplete();
            throw;
        }
    }

    private async Task ReadFramesAsync(NetworkStream stream, Channel<VideoFrame> channel, Stopwatch stopwatch, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var frame = new VideoFrame(await ReadPacketAsync(stream, token), stopwatch.Elapsed);
                if (channel.Writer.TryWrite(frame))
                {
                    continue;
                }

                if (channel.Reader.TryRead(out _))
                {
                    var dropped = Interlocked.Increment(ref _droppedFrames);
                    if (dropped == 1 || dropped % 30 == 0)
                    {
                        Log($"Dropped {dropped} stale frame(s) to keep latency live.");
                    }

                    if (channel.Writer.TryWrite(frame))
                    {
                        continue;
                    }
                }

                await channel.Writer.WriteAsync(frame, token);
            }
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private async Task WriteFramesAsync(ChannelReader<VideoFrame> reader, Stream ffmpegInput, Stopwatch stopwatch, CancellationToken token)
    {
        var waitingForKeyFrame = false;
        var lastDropCount = 0L;
        var lastStallLog = TimeSpan.Zero;

        await foreach (var frame in reader.ReadAllAsync(token))
        {
            var dropCount = Interlocked.Read(ref _droppedFrames);
            if (dropCount != lastDropCount)
            {
                waitingForKeyFrame = true;
                lastDropCount = dropCount;
            }

            if (waitingForKeyFrame)
            {
                if (!H264AnnexB.IsKeyFrame(frame.Data))
                {
                    continue;
                }

                waitingForKeyFrame = false;
                Log("Recovered stream on next H.264 keyframe after dropping stale frames.");
            }

            var age = stopwatch.Elapsed - frame.CapturedAt;
            if (age > TimeSpan.FromMilliseconds(250) && stopwatch.Elapsed - lastStallLog > TimeSpan.FromSeconds(2))
            {
                lastStallLog = stopwatch.Elapsed;
                Log($"ffmpeg is {age.TotalMilliseconds:0}ms behind the live frame.");
            }

            await ffmpegInput.WriteAsync(frame.Data, token);
            await ffmpegInput.FlushAsync(token);
        }
    }

    private static string BuildFfmpegArguments()
    {
        return "-hide_banner -loglevel error " +
            "-fflags nobuffer+genpts -flags low_delay -avioflags direct " +
            "-probesize 32 -analyzeduration 0 -use_wallclock_as_timestamps 1 " +
            "-f h264 -i pipe:0 -an -c:v copy " +
            "-f mpegts -muxdelay 0 -muxpreload 0 -flush_packets 1 -max_delay 0 " +
            $"\"{ObsUrl}?pkt_size=1316\"";
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
                RedirectStandardInput = label == "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) => AppendToolLog(logBuffer, label, e.Data);
        process.ErrorDataReceived += (_, e) => AppendToolLog(logBuffer, label, e.Data);
        process.Exited += (_, _) => Log($"{label} exited with code {process.ExitCode}.");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
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

internal readonly record struct VideoFrame(byte[] Data, TimeSpan CapturedAt);

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
