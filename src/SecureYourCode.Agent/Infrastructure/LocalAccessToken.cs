using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
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
            WriteOwnerOnlyAtomically(path, created);
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

    private static void WriteOwnerOnlyAtomically(string path, string value)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = CreateOwnerOnly(temp))
            {
                stream.Write(Encoding.ASCII.GetBytes(value));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: false);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static FileStream CreateOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var user = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("The current Windows user could not be determined.");
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            return new FileInfo(path).Create(
                FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
        }

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }
}
