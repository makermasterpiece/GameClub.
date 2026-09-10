using GameClub.Server.Security;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace GameClub.Server.Tests.Security;

public sealed class StationTokenServiceTests
{
    [Fact]
    public void Token_IsValidOnlyForStationItWasIssuedTo()
    {
        var service = new StationTokenService(new EphemeralDataProtectionProvider());
        var stationId = Guid.NewGuid();

        var token = service.Create(stationId);

        Assert.True(service.IsValid(token, stationId));
        Assert.False(service.IsValid(token, Guid.NewGuid()));
        Assert.False(service.IsValid("invalid-token", stationId));
    }
}
