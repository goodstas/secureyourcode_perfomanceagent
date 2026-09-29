using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace SecureYourCode.Agent.Infrastructure;

/// <summary>Writes a small secret file atomically (temp file + rename) with owner-only permissions.</summary>
public static class SecureFile
{
    public static void WriteOwnerOnly(string path, string content, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = CreateOwnerOnly(temp))
            {
                stream.Write(Encoding.ASCII.GetBytes(content));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite);
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
