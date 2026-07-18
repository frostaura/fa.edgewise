namespace Edgewise.Infrastructure.Security;

/// <summary>BCrypt password hashing, work factor 11.</summary>
public static class PasswordHasher
{
    private const int WorkFactor = 11;

    public static string Hash(string password) =>
        BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);

    public static bool Verify(string password, string hash) =>
        BCrypt.Net.BCrypt.Verify(password, hash);
}
