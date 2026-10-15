using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLua;

namespace KNE;

internal sealed record SyntaxCheckRequest(string Source);
internal sealed record SyntaxCheckResult(string? Error = null, int? Line = null);

/// <summary>Compiles text in an isolated process. Project chunks are never invoked.</summary>
internal static class SyntaxCheckWorker
{
    public static async Task<int> RunAsync()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var reader = new BoundedLineReader(input);
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            SyntaxCheckResult result;
            try
            {
                var request = JsonSerializer.Deserialize<SyntaxCheckRequest>(line) ?? throw new InvalidDataException("Пустой запрос анализа.");
                using var lua = new Lua();
                lua.State.Encoding = Encoding.UTF8;
                using var compiled = lua.LoadString(request.Source, "@analysis");
                result = new();
            }
            catch (Exception error)
            {
                string message = error.Message;
                var location = Regex.Match(message, @"analysis:(\d+):", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                result = new(message, location.Success && int.TryParse(location.Groups[1].Value, out int number) ? number : null);
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(result)).ConfigureAwait(false);
        }
        return 0;
    }
}

internal sealed class SyntaxCheckSession : IAsyncDisposable
{
    private Process? worker;
    private BoundedLineReader? responses;

    public async Task<SyntaxCheckResult> CheckAsync(string source, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (worker == null || worker.HasExited)
            {
                worker?.Dispose();
                string assembly = typeof(App).Assembly.Location;
                string executable = Path.ChangeExtension(assembly, ".exe");
                var start = new ProcessStartInfo(File.Exists(executable) ? executable : "dotnet")
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(assembly)!
                };
                if (!File.Exists(executable)) start.ArgumentList.Add(assembly);
                start.ArgumentList.Add("--analyze-syntax");
                worker = Process.Start(start) ?? throw new IOException("Не удалось запустить процесс проверки синтаксиса.");
                responses = new(worker.StandardOutput);
                _ = worker.StandardError.ReadToEndAsync();
            }
            string request = JsonSerializer.Serialize(new SyntaxCheckRequest(source));
            if (request.Length > 16_000_000) return new("Сценарий превышает размер сообщения анализатора.");
            await worker.StandardInput.WriteLineAsync(request.AsMemory(), deadline.Token).ConfigureAwait(false);
            await worker.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            string response = await responses!.ReadLineAsync(deadline.Token).ConfigureAwait(false)
                ?? throw new IOException("Процесс проверки синтаксиса завершился без результата.");
            return JsonSerializer.Deserialize<SyntaxCheckResult>(response) ?? throw new InvalidDataException("Пустой ответ проверки синтаксиса.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Stop();
            return new("Проверка синтаксиса превысила 10 секунд и была остановлена.");
        }
        catch { Stop(); throw; }
    }

    private void Stop()
    {
        if (worker == null) return;
        try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        finally { worker.Dispose(); worker = null; responses = null; }
    }

    public ValueTask DisposeAsync() { Stop(); return ValueTask.CompletedTask; }
}
