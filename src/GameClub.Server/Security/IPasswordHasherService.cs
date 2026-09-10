using GameClub.Domain.Users;
using Microsoft.AspNetCore.Identity;

namespace GameClub.Server.Security;

public interface IPasswordHasherService
{
    string HashPassword(User user, string password);
    PasswordVerificationResult VerifyPassword(User user, string password);
}

public sealed class AspNetPasswordHasherService(IPasswordHasher<User> passwordHasher)
    : IPasswordHasherService
{
    public string HashPassword(User user, string password) =>
        passwordHasher.HashPassword(user, password);

    public PasswordVerificationResult VerifyPassword(User user, string password) =>
        passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
}
