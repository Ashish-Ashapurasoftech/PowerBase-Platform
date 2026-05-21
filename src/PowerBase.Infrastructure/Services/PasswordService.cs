using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Services;

public class PasswordService : IPasswordService
{
    public string Hash(string plainText) => BCrypt.Net.BCrypt.HashPassword(plainText);

    public bool Verify(string plainText, string hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        try { return BCrypt.Net.BCrypt.Verify(plainText, hash); }
        catch { return false; }
    }
}
