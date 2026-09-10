using GameClub.Contracts.Client;
using Xunit;

namespace GameClub.Server.Tests.Client;

public sealed class BoundedPipeReaderTests
{
    [Fact]
    public async Task ReadsCrLfFrame() => Assert.Equal("{}", await Read("{}\r\n"));

    [Fact]
    public async Task EndOfStreamReturnsNull() => Assert.Null(await Read(""));

    [Fact]
    public async Task RejectsUnterminatedFrame() => await Assert.ThrowsAsync<IOException>(() => Read("{}"));

    [Fact]
    public async Task RejectsOversizeBeforeNewline() => await Assert.ThrowsAsync<IOException>(
        () => Read(new string('a', ClientPipeProtocol.MaximumMessageBytes + 1)));

    [Fact]
    public async Task RejectsOversizeUtf8Frame() => await Assert.ThrowsAsync<IOException>(
        () => Read(new string('я', ClientPipeProtocol.MaximumMessageBytes / 2 + 1) + "\n"));

    private static Task<string?> Read(string text) =>
        BoundedPipeReader.ReadLineAsync(new StringReader(text), CancellationToken.None);
}
