using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace KNE;

/// <summary>Hosts the same game debug commands in a dedicated Windows console process.</summary>
internal sealed class GameTerminal : IDisposable
{
    private const string ReadyMessage = "KNE game console ready";
    private readonly NamedPipeServerStream pipe;
    private readonly Process process;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<string> output = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    internal Task Completion { get; }

    internal GameTerminal(string shortcut, Func<string, Task> readCommand)
    {
        string name = "kne-game-console-" + Guid.NewGuid().ToString("N");
        pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            var options = EngineLaunch.CreateStartInfo("--game-console", name, shortcut);
            options.CreateNoWindow = false;
            options.WindowStyle = ProcessWindowStyle.Normal;
            process = Process.Start(options) ?? throw new IOException("Не удалось открыть системную консоль.");
        }
        catch
        {
            pipe.Dispose();
            lifetime.Dispose();
            throw;
        }
        Completion = RunAsync(readCommand, lifetime.Token);
    }

    internal void Write(string message)
    {
        if (message.Length > 0) output.Writer.TryWrite(message.Length <= 200_000 ? message : message[^200_000..]);
    }

    private async Task RunAsync(Func<string, Task> readCommand, CancellationToken token)
    {
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
        connect.CancelAfter(TimeSpan.FromSeconds(10));
        try { await pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("Системная консоль не подключилась."); }
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var lines = new BoundedLineReader(reader);
        try
        {
            string? ready = await lines.ReadLineAsync(connect.Token).ConfigureAwait(false);
            if (ready != ReadyMessage)
                throw new IOException(ready != null && ready.Length <= 1000 ? ready : "Windows не открыла системную консоль.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("Системная консоль не завершила запуск."); }
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        var send = SendAsync(writer, session.Token);
        var receive = ReceiveAsync();
        var completed = await Task.WhenAny(send, receive).ConfigureAwait(false);
        session.Cancel();
        try { await completed.ConfigureAwait(false); }
        catch (IOException) when (!pipe.IsConnected) { }
        finally
        {
            try { await (completed == send ? receive : send).ConfigureAwait(false); }
            catch (OperationCanceledException) when (session.IsCancellationRequested) { }
            catch (IOException) when (!pipe.IsConnected) { }
        }

        async Task ReceiveAsync()
        {
            while (await lines.ReadLineAsync(session.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length > 32768) throw new InvalidDataException("Команда консоли слишком длинная.");
                string command = JsonSerializer.Deserialize<string>(line) ?? "";
                if (command.Length > 4096) throw new InvalidDataException("Команда консоли слишком длинная.");
                await readCommand(command).ConfigureAwait(false);
            }
        }
    }

    private async Task SendAsync(StreamWriter writer, CancellationToken token)
    {
        await foreach (string message in output.Reader.ReadAllAsync(token).ConfigureAwait(false))
            await writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lifetime.Cancel();
        output.Writer.TryComplete();
        pipe.Dispose();
        try { if (!process.HasExited) process.Kill(); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        { Trace.TraceWarning("KNE console cleanup: {0}", error.Message); }
        process.Dispose();
        lifetime.Dispose();
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int AllocConsole();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int FreeConsole();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint attributes, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int SetStdHandle(int id, SafeFileHandle handle);

    private const uint ConsoleReadWrite = 0xC0000000;
    private const uint ShareReadWrite = 3;
    private const uint OpenExisting = 3;
    private const int StandardInput = -10;
    private const int StandardOutput = -11;
    private const int StandardError = -12;

    internal static async Task<int> RunHelperAsync(string name, string shortcut)
    {
        const string prefix = "kne-game-console-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _))
            return 1;
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(8000).ConfigureAwait(false);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        bool allocated = false;
        SafeFileHandle? consoleInput = null, consoleOutput = null;
        try
        {
            try
            {
                FreeConsole();
                allocated = AllocConsole() != 0;
                if (!allocated) throw new Win32Exception(Marshal.GetLastWin32Error());
                // A launcher can pass redirected standard handles even to a GUI process.
                consoleInput = OpenConsoleDevice("CONIN$");
                consoleOutput = OpenConsoleDevice("CONOUT$");
                if (SetStdHandle(StandardInput, consoleInput) == 0 || SetStdHandle(StandardOutput, consoleOutput) == 0 ||
                    SetStdHandle(StandardError, consoleOutput) == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);
                Console.Title = "KNE · Консоль игры";
                Console.TreatControlCAsInput = true;
                Console.WriteLine($"Консоль KNE · {shortcut} или :close — закрыть · :vars — переменные · :stop — остановить");
            }
            catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException)
            {
                await writer.WriteLineAsync("Не удалось открыть консоль Windows. " + error.Message.Replace('\r', ' ').Replace('\n', ' ')).ConfigureAwait(false);
                return 1;
            }
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(ReadyMessage).ConfigureAwait(false);
            var lines = new BoundedLineReader(reader);
            using var session = new CancellationTokenSource();
            var input = ReadKeysAsync(writer, shortcut, session.Token);
            var display = DisplayAsync(session.Token);
            var completed = await Task.WhenAny(input, display).ConfigureAwait(false);
            session.Cancel();
            try { await completed.ConfigureAwait(false); }
            finally
            {
                try { await (completed == input ? display : input).ConfigureAwait(false); }
                catch (OperationCanceledException) when (session.IsCancellationRequested) { }
            }
            return 0;

            async Task DisplayAsync(CancellationToken token)
            {
                while (await lines.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                {
                    string message = JsonSerializer.Deserialize<string>(line) ?? "";
                    Console.WriteLine(new string(message.Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t').ToArray()));
                }
            }
        }
        finally
        {
            if (allocated) FreeConsole();
            consoleInput?.Dispose();
            consoleOutput?.Dispose();
        }
    }

    private static SafeFileHandle OpenConsoleDevice(string name)
    {
        var handle = CreateFileW(name, ConsoleReadWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    private static async Task ReadKeysAsync(StreamWriter writer, string shortcut, CancellationToken token)
    {
        var buffer = new StringBuilder();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!Console.KeyAvailable)
            {
                await Task.Delay(30, token).ConfigureAwait(false);
                continue;
            }
            var key = Console.ReadKey(intercept: true);
            if (GameConsoleShortcut.Matches(key, shortcut)) return;
            if (key.Key == ConsoleKey.Enter)
            {
                string command = buffer.ToString().Trim();
                buffer.Clear();
                Console.WriteLine();
                if (command == ":close") return;
                if (command.Length > 0) await writer.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), token).ConfigureAwait(false);
            }
            else if (key.Key == ConsoleKey.Backspace && buffer.Length > 0)
            {
                buffer.Length--;
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar) && buffer.Length < 4096)
            {
                buffer.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }
    }
}
