using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Ferry.Core.Files;

/// <summary>
/// Folders and files only the current user can open: an ACL with that user alone (inheritance off) on Windows, where
/// OpenSSH refuses a key other users can read; 0700 and 0600 elsewhere.
/// </summary>
public static class PrivateFiles
{
    /// <summary>Create <paramref name="path"/> (and its parents) if needed, and make it private; what is created in
    /// it afterwards is private too.</summary>
    public static void CreateFolder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var folder = new DirectoryInfo(path);
            if (!folder.Exists)
                folder.Create(FolderSecurity());
            else
                folder.SetAccessControl(FolderSecurity());
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Write <paramref name="text"/> to <paramref name="path"/>, private from the first byte (written to a
    /// private temporary file, then moved into place). A missing folder is created private; one that is there is left
    /// as it is.</summary>
    public static void WriteText(string path, string text)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!Directory.Exists(folder))
            CreateFolder(folder);
        AtomicFile.WriteText(path, text, CreatePrivate);
    }

    /// <summary>Create <paramref name="path"/> for writing, private from the first byte, over whatever is there.</summary>
    public static FileStream CreatePrivate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileInfo(path).Create(FileMode.Create, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, FileSecurity());
        }
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    /// <summary>Make an existing file private (one another program wrote, such as ssh-keygen), unless it is already.</summary>
    public static void Restrict(string path)
    {
        if (IsPrivate(path))
            return; // ssh-keygen makes its keys private itself
        if (OperatingSystem.IsWindows())
            new FileInfo(path).SetAccessControl(FileSecurity(withOwner: false)); // only the DACL: setting the owner needs rights a user may not have
        else
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Whether only the current user can open <paramref name="path"/> (what OpenSSH checks for a key).</summary>
    public static bool IsPrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
            return (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) == 0;
        var user = Me();
        var rules = new FileInfo(path).GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            // OpenSSH allows the owner, SYSTEM and Administrators; anyone else with access makes it refuse the key
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference is SecurityIdentifier sid
                && sid != user && !sid.IsWellKnown(WellKnownSidType.LocalSystemSid) && !sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
                return false;
        }
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier Me()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity FileSecurity(bool withOwner = true)
    {
        var security = new FileSecurity();
        var me = Me();
        if (withOwner)
            security.SetOwner(me); // a new file is ours anyway; saying so is allowed when creating it
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity FolderSecurity()
    {
        var security = new DirectorySecurity();
        var me = Me();
        security.SetOwner(me);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl,
                                                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                                                        PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
