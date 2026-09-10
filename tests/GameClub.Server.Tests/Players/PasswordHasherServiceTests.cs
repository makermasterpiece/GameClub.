using GameClub.Domain.Users;
using GameClub.Server.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Xunit;

namespace GameClub.Server.Tests.Players;

public sealed class PasswordHasherServiceTests
{
    [Fact]
    public void HashAndVerify_WithCorrectPassword_Succeeds()
    {
        var service = CreateService();
        var user = CreateUser();
        user.SetPasswordHash(service.HashPassword(user, "StrongPassword123!"));

        var result = service.VerifyPassword(user, "StrongPassword123!");

        Assert.NotEqual(PasswordVerificationResult.Failed, result);
    }

    [Fact]
    public void Verify_WithWrongPassword_Fails()
    {
        var service = CreateService();
        var user = CreateUser();
        user.SetPasswordHash(service.HashPassword(user, "StrongPassword123!"));

        var result = service.VerifyPassword(user, "WrongPassword123!" );

        Assert.Equal(PasswordVerificationResult.Failed, result);
    }

    private static AspNetPasswordHasherService CreateService() =>
        new(new PasswordHasher<User>(Options.Create(new PasswordHasherOptions())));

    private static User CreateUser() =>
        new(Guid.NewGuid(), "nur", "Nur", null, null, DateTime.UtcNow);
}
