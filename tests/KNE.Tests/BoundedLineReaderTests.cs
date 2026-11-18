using System.IO;
using System.IO.Pipes;
using System.Text;
using Xunit;

namespace KNE.Tests;

public sealed class BoundedLineReaderTests
{
    [Theory]
    [InlineData(1023, false)] [InlineData(1024, false)] [InlineData(1025, false)] [InlineData(2048, false)] [InlineData(4096, false)]
    [InlineData(1023, true)] [InlineData(1024, true)] [InlineData(1025, true)] [InlineData(2048, true)] [InlineData(4096, true)]
    public async Task CompletePipeLinesReturnAtTheirNewlineWithoutWaitingForTheWriterToClose(int encodedBytes, bool unicode)
    {
        await using var pipe = await OpenPipe.Connect(); string text = Content(encodedBytes - 1, unicode); byte[] wire = Encoding.UTF8.GetBytes(text + "\n");
        Assert.Equal(encodedBytes, wire.Length);
        Task<string?> reading = pipe.Read(); await pipe.Writer.WriteAsync(wire); await pipe.Writer.FlushAsync();
        Assert.Equal(text, await reading.WaitAsync(TimeSpan.FromSeconds(2))); Assert.True(pipe.Writer.IsConnected); Assert.True(pipe.Writer.CanWrite);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TwoCoalescedPipeLinesRemainSeparatelyReadableAtAnExact2048ByteBoundary(bool unicode)
    {
        await using var pipe = await OpenPipe.Connect(); string first = Content(1023, unicode), second = Content(1022, unicode);
        byte[] wire = Encoding.UTF8.GetBytes(first + "\n" + second + "\r\n"); Assert.Equal(2048, wire.Length);
        Task<string?> reading = pipe.Read(); await pipe.Writer.WriteAsync(wire); await pipe.Writer.FlushAsync();
        Assert.Equal(first, await reading.WaitAsync(TimeSpan.FromSeconds(2))); Assert.Equal(second, await pipe.Read().WaitAsync(TimeSpan.FromSeconds(2))); Assert.True(pipe.Writer.IsConnected);
    }

    [Fact]
    public async Task CancelingAPendingPipeReadDoesNotRequireTheWriterToClose()
    {
        await using var pipe = await OpenPipe.Connect(); using var canceled = new CancellationTokenSource();
        Task<string?> reading = pipe.Read(canceled.Token); Assert.False(reading.IsCompleted); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(3))); Assert.True(pipe.Writer.IsConnected);
    }

    [Theory]
    [InlineData(16_000_000, false)] [InlineData(16_000_001, true)]
    public async Task CharacterLimitAcceptsItsExactBoundaryAndRejectsTheAdjacentCharacterWithoutANewline(int length, bool rejected)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', length)));
        using var text = new StreamReader(stream, Encoding.UTF8); var reader = new BoundedLineReader(text);
        if (rejected) { var error = await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadLineAsync()); Assert.Contains("размер", error.Message); }
        else { string? result = await reader.ReadLineAsync(); Assert.Equal(length, result!.Length); Assert.Equal('x', result[0]); Assert.Equal('x', result[^1]); Assert.Null(await reader.ReadLineAsync()); }
    }

    [Fact]
    public async Task EmptyLinesCrLfAndTheFinalUnterminatedLineHaveTheirUsualLineReaderSemantics()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("\n\r\nПривет\r\nlast")); using var text = new StreamReader(stream, Encoding.UTF8); var reader = new BoundedLineReader(text);
        Assert.Equal("", await reader.ReadLineAsync()); Assert.Equal("", await reader.ReadLineAsync()); Assert.Equal("Привет", await reader.ReadLineAsync()); Assert.Equal("last", await reader.ReadLineAsync()); Assert.Null(await reader.ReadLineAsync());
    }

    [Fact]
    public async Task DecoderPreservesAnEmojiSplitAcrossThe4096ByteBoundary()
    {
        string expected = "A" + new string('я', 2046) + "🙂хвост";
        Assert.Equal(4093, Encoding.UTF8.GetByteCount("A" + new string('я', 2046)));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(expected + "\nnext\n")); using var text = new StreamReader(stream, Encoding.UTF8); var reader = new BoundedLineReader(text);
        Assert.Equal(expected, await reader.ReadLineAsync()); Assert.Equal("next", await reader.ReadLineAsync()); Assert.Null(await reader.ReadLineAsync());
    }

    [Fact]
    public async Task DecoderAcceptsAFragmentedUtf8BomAndKeepsLaterBomCharactersAndTheUnterminatedTail()
    {
        byte[] wire = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Привет🙂\n\uFEFFinside\r\nlastЯ")).ToArray();
        using var stream = new ByteAtATimeStream(wire); using var text = new StreamReader(stream, Encoding.UTF8); var reader = new BoundedLineReader(text);
        Assert.Equal("Привет🙂", await reader.ReadLineAsync()); Assert.Equal("\uFEFFinside", await reader.ReadLineAsync()); Assert.Equal("lastЯ", await reader.ReadLineAsync()); Assert.Null(await reader.ReadLineAsync());
        Assert.True(stream.Reads >= wire.Length);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FinalPartialUtf8SequenceUsesTheReadersConfiguredDecoderFallback(bool strict)
    {
        using var stream = new ByteAtATimeStream([0x74, 0x61, 0x69, 0x6C, 0xF0, 0x9F]); using var text = new StreamReader(stream, new UTF8Encoding(false, strict)); var reader = new BoundedLineReader(text);
        if (strict) await Assert.ThrowsAsync<DecoderFallbackException>(() => reader.ReadLineAsync());
        else { Assert.Equal("tail\uFFFD", await reader.ReadLineAsync()); Assert.Null(await reader.ReadLineAsync()); }
    }

    static string Content(int bytes, bool unicode) => unicode ? new string('я', bytes / 2) + (bytes % 2 == 0 ? "" : "x") : new string('x', bytes);

    sealed class ByteAtATimeStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public int Reads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            Reads++; return base.ReadAsync(destination[..Math.Min(destination.Length, 1)], cancellationToken);
        }
    }

    sealed class OpenPipe : IAsyncDisposable
    {
        readonly NamedPipeServerStream server;
        readonly StreamReader text;
        readonly BoundedLineReader lines;
        readonly List<Task<string?>> reads = [];
        public NamedPipeClientStream Writer { get; }
        OpenPipe(NamedPipeServerStream server, NamedPipeClientStream writer)
        {
            this.server = server; Writer = writer; text = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true); lines = new(text);
        }
        public static async Task<OpenPipe> Connect()
        {
            string name = "kne-line-test-" + Guid.NewGuid().ToString("N"); var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var writer = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); Task waiting = server.WaitForConnectionAsync(timeout.Token); await writer.ConnectAsync(timeout.Token); await waiting;
                return new(server, writer);
            }
            catch { writer.Dispose(); server.Dispose(); throw; }
        }
        public Task<string?> Read(CancellationToken token = default)
        {
            var pending = lines.ReadLineAsync(token); reads.Add(pending); return pending;
        }
        public async ValueTask DisposeAsync()
        {
            // Closing after the assertion is cleanup only: the assertion itself requires an open writer.
            await Writer.DisposeAsync();
            foreach (var pending in reads)
                try { await pending.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            text.Dispose(); await server.DisposeAsync();
        }
    }
}
