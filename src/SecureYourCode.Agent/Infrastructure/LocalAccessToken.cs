using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace SecureYourCode.Agent.Infrastructure;

/// <summary>The local access token (plan §4.2): required on every mutating endpoint, compared in constant time.</summary>
public sealed class LocalAccessToken
{
    public const string HeaderName = "X-SecureYourCode-Token";
    private const int TokenBytes = 32;

    private readonly byte[] _expected;

    private LocalAccessToken(string value)
    {
        Value = value;
        _expected = Encoding.ASCII.GetBytes(value);
    }

    /// <summary>The token itself, only for copying into the demo repo's gitignored hook file. Never log it.</summary>
    public string Value { get; }

    public bool Matches(StringValues header)
    {
        if (header.Count != 1 || header[0] is not { } provided)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(provided), _expected);
    }

    /// <summary>EnsureAccessToken(): create the token on first start, load it afterwards, refuse to start if it is malformed.</summary>
    public static LocalAccessToken Ensure(string path)
    {
        if (!File.Exists(path))
        {
            var created = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenBytes));
            SecureFile.WriteOwnerOnly(path, created, overwrite: false);
            return new LocalAccessToken(created);
        }

        var existing = File.ReadAllText(path).Trim();
        if (existing.Length != TokenBytes * 2 || !existing.All(char.IsAsciiHexDigit))
        {
            throw new InvalidOperationException(
                $"The access token file '{path}' is empty or is not {TokenBytes * 2} hexadecimal characters. " +
                "Delete it to generate a new token, then restart.");
        }

        return new LocalAccessToken(existing);
    }
}
