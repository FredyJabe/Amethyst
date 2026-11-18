using System.Text;
using System.Threading.Channels;

namespace Amethyst;

public enum ServerLogLevel { Debug, Info, Auth, Chat, Warning, Error }

public readonly record struct ServerLogMessage(DateTimeOffset Timestamp, ServerLogLevel Level, string Text);

/// <summary>
/// Serializes concurrent log messages, keeps a live terminal input prompt visible,
/// and falls back to ordinary input/output when stdin/stdout are redirected.
/// Existing Console.WriteLine/Console.Error.WriteLine calls are captured via TextWriters.
/// </summary>
public sealed class ServerConsole : IAsyncDisposable
{
    private readonly Channel<ServerLogMessage> _messages;
    private readonly Task _logPump;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private readonly TextReader _originalIn;
    private readonly ConsoleLogWriter _stdout;
    private readonly ConsoleLogWriter _stderr;
    private readonly object _displayLock = new();
    private readonly ConsoleLineEditor _editor;
    private readonly bool _interactive;
    private readonly bool _colors;
    private bool _inputRunning;
    private int _inputStarted;
    private int _disposed;
    private long _droppedMessages;

    public bool IsInteractive => _interactive;

    public ServerConsole(string mode = "auto", int historySize = 100)
    {
        if (mode is not ("auto" or "interactive" or "plain"))
            throw new ArgumentException("consoleMode must be auto, interactive or plain.", nameof(mode));

        _originalOut = Console.Out;
        _originalError = Console.Error;
        _originalIn = Console.In;
        _editor = new ConsoleLineEditor(historySize);

        bool terminalReady = !Console.IsInputRedirected && !Console.IsOutputRedirected &&
            !string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);
        _interactive = mode != "plain" && terminalReady;
        _colors = _interactive && Environment.GetEnvironmentVariable("NO_COLOR") is null;

        _messages = Channel.CreateBounded<ServerLogMessage>(new BoundedChannelOptions(8192)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _stdout = new ConsoleLogWriter(this, false);
        _stderr = new ConsoleLogWriter(this, true);
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
        _logPump = Task.Run(PumpLogsAsync);

        if (mode == "interactive" && !terminalReady)
            Console.WriteLine("[WARN] Interactive console unavailable (redirected terminal or TERM=dumb); using plain mode.");
    }

    public Task StartInputAsync(Func<string, CancellationToken, Task> execute,
        Func<string, IReadOnlyList<string>> complete, CancellationToken stop)
    {
        if (Interlocked.Exchange(ref _inputStarted, 1) != 0)
            throw new InvalidOperationException("Console input is already running.");
        lock (_displayLock)
        {
            _inputRunning = _interactive;
            if (_inputRunning) DrawPromptUnsafe();
        }
        // Console.ReadKey may block in the runtime even after cancellation. The
        // reader is deliberately a background task, not a server shutdown barrier.
        return Task.Run(async () =>
        {
            try
            {
                if (_interactive)
                    await ReadKeysAsync(execute, complete, stop).ConfigureAwait(false);
                else
                    await ReadLinesAsync(execute, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (IOException ex) when (!stop.IsCancellationRequested)
            {
                Console.Error.WriteLine($"[console] Input failure: {ex.Message}");
            }
            catch (InvalidOperationException ex) when (!stop.IsCancellationRequested)
            {
                Console.Error.WriteLine($"[console] Input unavailable: {ex.Message}");
            }
            finally
            {
                lock (_displayLock) _inputRunning = false;
            }
        }, CancellationToken.None);
    }

    public void Clear()
    {
        if (!_interactive)
        {
            Console.WriteLine("[console] Clear requires an interactive terminal.");
            return;
        }
        lock (_displayLock)
        {
            _originalOut.Write("\u001b[2J\u001b[H");
            if (_inputRunning) DrawPromptUnsafe();
            _originalOut.Flush();
        }
    }

    /// <summary>A structured entry point for new subsystems; old Console.WriteLine calls
    /// are intercepted automatically.</summary>
    public void Log(ServerLogLevel level, string message) =>
        Queue(new ServerLogMessage(DateTimeOffset.Now, level, Sanitize(message)));

    internal void Publish(string text, bool fromError)
    {
        string clean = Sanitize(text);
        Queue(new ServerLogMessage(DateTimeOffset.Now, Classify(clean, fromError), clean));
    }

    private void Queue(ServerLogMessage message)
    {
        if (!_messages.Writer.TryWrite(message))
            Interlocked.Increment(ref _droppedMessages);
    }

    public static ServerLogLevel Classify(string text, bool fromError = false)
    {
        if (fromError || text.Contains("Exception:", StringComparison.Ordinal) ||
            text.Contains("error:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase))
            return ServerLogLevel.Error;
        if (text.StartsWith("[WARN", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Connection closed:", StringComparison.OrdinalIgnoreCase))
            return ServerLogLevel.Warning;
        if (text.StartsWith("[auth]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("[op]", StringComparison.OrdinalIgnoreCase))
            return ServerLogLevel.Auth;
        if (text.StartsWith("[chat]", StringComparison.OrdinalIgnoreCase))
            return ServerLogLevel.Chat;
        if (text.StartsWith("[wire", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("[chunks]", StringComparison.OrdinalIgnoreCase))
            return ServerLogLevel.Debug;
        return ServerLogLevel.Info;
    }

    private static string Sanitize(string text)
    {
        // Player chat/exception strings must not be allowed to inject ANSI cursor
        // escapes or overwrite the prompt. Multi-line output is split by the writer.
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (ch == '\t') sb.Append("    ");
            else if (!char.IsControl(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    private async Task PumpLogsAsync()
    {
        await foreach (ServerLogMessage item in _messages.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            long dropped = Interlocked.Exchange(ref _droppedMessages, 0);
            if (dropped > 0)
                Print(new ServerLogMessage(DateTimeOffset.Now, ServerLogLevel.Warning,
                    $"[console] {dropped} log lines dropped because the output queue was full."));
            Print(item);
        }
    }

    private void Print(ServerLogMessage message)
    {
        lock (_displayLock)
        {
            if (_interactive && _inputRunning)
                _originalOut.Write("\r\u001b[2K");

            TextWriter output = !_interactive && message.Level == ServerLogLevel.Error
                ? _originalError : _originalOut;
            string heading = $"[{message.Timestamp:HH:mm:ss}] [{message.Level.ToString().ToUpperInvariant()}] ";
            if (_colors)
            {
                output.Write(Color(message.Level));
                output.Write(heading);
                output.Write("\u001b[0m");
            }
            else output.Write(heading);
            output.WriteLine(message.Text);
            if (_interactive && _inputRunning) DrawPromptUnsafe();
            output.Flush();
        }
    }

    private static string Color(ServerLogLevel level) => level switch
    {
        ServerLogLevel.Debug => "\u001b[90m",
        ServerLogLevel.Auth => "\u001b[92m",
        ServerLogLevel.Chat => "\u001b[96m",
        ServerLogLevel.Warning => "\u001b[93m",
        ServerLogLevel.Error => "\u001b[91m",
        _ => "\u001b[37m"
    };

    private void DrawPromptUnsafe()
    {
        if (!_interactive || Volatile.Read(ref _disposed) != 0) return;
        int width = 80;
        try { width = Console.WindowWidth; } catch (IOException) { } catch (InvalidOperationException) { }
        int visible = Math.Max(8, width - 4);
        string line = _editor.Text;
        int cursor = _editor.Cursor;
        int start = Math.Max(0, cursor - visible + 1);
        int count = Math.Min(visible, line.Length - start);
        _originalOut.Write("\r\u001b[2K");
        if (_colors) _originalOut.Write("\u001b[92m> \u001b[0m");
        else _originalOut.Write("> ");
        _originalOut.Write(line.AsSpan(start, count));
        int toLeft = start + count - cursor;
        if (toLeft > 0) _originalOut.Write($"\u001b[{toLeft}D");
        _originalOut.Flush();
    }

    private async Task ReadKeysAsync(Func<string, CancellationToken, Task> execute,
        Func<string, IReadOnlyList<string>> complete, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (stop.IsCancellationRequested) break;
            ConsoleEditResult result;
            lock (_displayLock)
            {
                result = _editor.Handle(key, complete);
                if (result.Redraw) DrawPromptUnsafe();
            }
            if (result.Submitted is { } command)
            {
                Publish($"> {command}", false);
                await InvokeAsync(execute, command, stop).ConfigureAwait(false);
            }
        }
    }

    private async Task ReadLinesAsync(Func<string, CancellationToken, Task> execute,
        CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            string? line = await _originalIn.ReadLineAsync(stop).ConfigureAwait(false);
            if (line is null) break; // EOF should not shut down a headless server.
            if (line.Length == 0) continue;
            await InvokeAsync(execute, line, stop).ConfigureAwait(false);
        }
    }

    private static async Task InvokeAsync(Func<string, CancellationToken, Task> execute,
        string command, CancellationToken stop)
    {
        try { await execute(command, stop).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { Console.Error.WriteLine($"[console] Command failed: {ex}"); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stdout.Flush();
        _stderr.Flush();
        _messages.Writer.TryComplete();
        await _logPump.ConfigureAwait(false);
        lock (_displayLock)
        {
            if (_interactive && _inputRunning)
            {
                _originalOut.Write("\r\u001b[2K");
                _originalOut.Flush();
            }
            _inputRunning = false;
            Console.SetOut(_originalOut);
            Console.SetError(_originalError);
        }
        _stdout.Dispose();
        _stderr.Dispose();
    }

    private sealed class ConsoleLogWriter : TextWriter
    {
        private readonly ServerConsole _owner;
        private readonly bool _isError;
        private readonly ThreadLocal<StringBuilder> _buffers = new(() => new StringBuilder(), true);
        public override Encoding Encoding => Encoding.UTF8;

        public ConsoleLogWriter(ServerConsole owner, bool isError)
        {
            _owner = owner;
            _isError = isError;
        }

        public override void Write(char value) => Append(value.ToString());
        public override void Write(string? value) { if (value is not null) Append(value); }
        public override void Write(ReadOnlySpan<char> buffer) => Append(buffer.ToString());
        public override void Write(char[]? buffer, int index, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            Append(new string(buffer, index, count));
        }
        public override void WriteLine() => Append("\n");
        public override void WriteLine(string? value) => Append((value ?? "") + "\n");
        public override void WriteLine(ReadOnlySpan<char> buffer) => Append(buffer.ToString() + "\n");
        public override void Flush()
        {
            foreach (StringBuilder buffer in _buffers.Values)
            {
                if (buffer.Length == 0) continue;
                _owner.Publish(buffer.ToString(), _isError);
                buffer.Clear();
            }
        }

        private void Append(string text)
        {
            StringBuilder buffer = _buffers.Value!;
            foreach (char ch in text)
            {
                if (ch == '\n')
                {
                    string line = buffer.ToString().TrimEnd('\r');
                    buffer.Clear();
                    _owner.Publish(line, _isError);
                }
                else buffer.Append(ch);
            }
        }
    }
}
