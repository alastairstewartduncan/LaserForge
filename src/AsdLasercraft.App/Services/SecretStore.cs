using System.Runtime.InteropServices;
using System.Text;

namespace AsdLasercraft.App.Services;

/// <summary>
/// Keeps AI provider API keys in Windows Credential Manager (per user, encrypted by Windows) rather than in the
/// settings file, one entry per provider. You can see or remove them under Control Panel → Credential Manager →
/// Windows Credentials.
/// </summary>
public static class SecretStore
{
    public const string ApiKeyTarget = "ASD Lasercraft/Claude API key";

    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    private static readonly Dictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Credential Manager entry for a provider. Claude keeps the name used by earlier versions.</summary>
    public static string TargetFor(string providerId) =>
        string.IsNullOrEmpty(providerId) || providerId == Core.Ai.AiProviderCatalog.AnthropicId ? ApiKeyTarget : $"ASD Lasercraft/AI key/{providerId}";

    /// <summary>The saved Claude key, or null.</summary>
    public static string? ApiKey => GetKey(Core.Ai.AiProviderCatalog.AnthropicId);

    /// <summary>The saved key for a provider, or null. Cached after the first read.</summary>
    public static string? GetKey(string providerId)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(providerId, out var key))
            {
                key = Read(TargetFor(providerId));
                Cache[providerId] = key;
            }
            return key;
        }
    }

    public static bool SaveApiKey(string key) => SaveKey(Core.Ai.AiProviderCatalog.AnthropicId, key, "Claude API");

    public static bool SaveKey(string providerId, string key, string providerName)
    {
        key = key.Trim();
        if (key.Length == 0) return DeleteKey(providerId);
        var bytes = Encoding.Unicode.GetBytes(key);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = TargetFor(providerId),
                Comment = "Used by A·S·D Lasercraft for its AI features.",
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = providerName,
            };
            bool ok;
            try { ok = CredWrite(ref cred, 0); }
            catch (Exception) { ok = false; }
            if (ok) lock (Cache) Cache[providerId] = key;
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public static bool DeleteApiKey() => DeleteKey(Core.Ai.AiProviderCatalog.AnthropicId);

    public static bool DeleteKey(string providerId)
    {
        bool ok;
        try { ok = CredDelete(TargetFor(providerId), CRED_TYPE_GENERIC, 0) || Marshal.GetLastWin32Error() == 1168; } // ERROR_NOT_FOUND
        catch (Exception) { ok = false; }
        lock (Cache) Cache[providerId] = null;
        return ok;
    }

    private static string? Read(string target)
    {
        try
        {
            if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr)) return null;
            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
                if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize <= 0) return null;
                return Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2);
            }
            finally { CredFree(ptr); }
        }
        catch (Exception)
        {
            return null; // e.g. credential service unavailable – AI just shows as not set up
        }
    }

    /// <summary>"sk-ant-…a1b2" for display.</summary>
    public static string Mask(string? key) =>
        string.IsNullOrEmpty(key) ? "" : key.Length <= 12 ? new string('•', key.Length) : key[..7] + "…" + key[^4..];
}
