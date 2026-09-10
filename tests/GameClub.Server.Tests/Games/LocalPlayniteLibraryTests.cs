using GameClub.Agent.Services.Games;
using GameClub.Contracts.Games;
using Xunit;

namespace GameClub.Server.Tests.Games;

public sealed class LocalPlayniteLibraryTests
{
    [Theory]
    [InlineData("\\\\server\\share\\Playnite.FullscreenApp.exe")]
    [InlineData("C:\\safe\\evil.exe")]
    [InlineData("C:\\safe\\Playnite.FullscreenApp.exe:evil")]
    [InlineData("C:\\safe\\..\\Playnite.FullscreenApp.exe")]
    [InlineData("Playnite.FullscreenApp.exe")]
    public void UnsafePathsRejected(string path) => Assert.Throws<ArgumentException>(() => LocalPlayniteLibrary.ValidatePath(path, true));

    [Fact]
    public void DuplicateLocalIdsRejected()
    {
        var id = Guid.NewGuid();
        var config = new PlayniteLocalConfiguration(@"C:\GameClubTest\Playnite.FullscreenApp.exe", @"C:\GameClubTest\library.json", true,
            [new(Guid.NewGuid(), id), new(Guid.NewGuid(), id)]);
        Assert.Throws<ArgumentException>(() => LocalPlayniteLibrary.ValidateConfiguration(config));
    }

    [Theory]
    [InlineData(-121)]
    [InlineData(1)]
    public void StaleOrFutureManifestRejected(int seconds)
    {
        var now = DateTime.UtcNow;
        Assert.Throws<ArgumentException>(() => LocalPlayniteLibrary.ValidateManifest(new(1, now.AddSeconds(seconds), []), now));
    }

    [Fact]
    public void FreshInstalledManifestAccepted()
    {
        var now = DateTime.UtcNow;
        LocalPlayniteLibrary.ValidateManifest(new(1, now, [new(Guid.NewGuid(), "Game", true)]), now);
    }

    [Fact]
    public void DuplicateManifestIdsRejected()
    {
        var now = DateTime.UtcNow;
        var game = new PlayniteLibraryGame(Guid.NewGuid(), "Game", true);
        Assert.Throws<ArgumentException>(() => LocalPlayniteLibrary.ValidateManifest(new(1, now, [game, game]), now));
    }
}
