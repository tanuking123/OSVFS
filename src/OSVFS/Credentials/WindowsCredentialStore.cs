using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OSVFS.ObjectStore;

namespace OSVFS.Credentials;

/// <summary>
/// AWS credential store backed by Windows Credential Manager. The access key id is
/// kept in the credential's UserName field; the secret access key (and optional
/// session token) are JSON-encoded, encrypted with DPAPI under the current user, and
/// written into the credential blob.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsCredentialStore : IAwsCredentialStore
{
    /// <summary>
    /// Target-name prefix that namespaces every entry written by OSVFS, used both for
    /// reads and as the filter passed to CredEnumerate during list.
    /// </summary>
    internal const string TargetPrefix = "OSVFS:AWS:";

    /// <summary>
    /// Comment stamped onto each credential so users can recognize OSVFS-managed entries
    /// in the Windows Credential Manager UI.
    /// </summary>
    private const string CredentialComment = "OSVFS-managed AWS credential";

    /// <summary>
    /// CRED_TYPE_GENERIC: arbitrary application credential, the only type that doesn't
    /// require a domain or smart-card backing.
    /// </summary>
    private const uint CredTypeGeneric = 1;

    /// <summary>
    /// CRED_PERSIST_LOCAL_MACHINE: survives logon sessions on this machine but does not
    /// roam — keeps the encrypted blob tied to the host that wrote it.
    /// </summary>
    private const uint CredPersistLocalMachine = 2;

    /// <summary>
    /// Win32 ERROR_NOT_FOUND, returned by CredRead/CredDelete when the target is absent.
    /// </summary>
    private const int ErrorNotFound = 1168;

    /// <summary>
    /// Maximum size of CREDENTIAL_BLOB, per Win32 docs (5 * 512 bytes). DPAPI output
    /// adds ~32 bytes of overhead, so the practical secret-payload limit is well below.
    /// </summary>
    private const int CredentialBlobSizeLimit = 5 * 512;

    /// <summary>
    /// DPAPI entropy applied to every blob. Distinct from the credential namespace so a
    /// stray DPAPI-encrypted blob written elsewhere can't be passed off as a credential.
    /// </summary>
    private static readonly byte[] DpapiEntropy = "OSVFS:AWS:v1"u8.ToArray();

    /// <inheritdoc/>
    public void Save(string profileName, AwsCredential credential)
    {
        ValidateProfile(profileName);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential.AccessKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential.SecretAccessKey);

        var payload = new StoredSecretPayload
        {
            SecretAccessKey = credential.SecretAccessKey,
            SessionToken = credential.SessionToken,
            ExpiresAtUnix = credential.ExpiresAt?.ToUnixTimeSeconds(),
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(payload, StoredSecretJsonContext.Default.StoredSecretPayload);
        // Current-user scope: only the user that ran `credentials set` can decrypt, even
        // though the credential persists at LocalMachine scope inside Cred Manager.
        var encrypted = ProtectedData.Protect(json, DpapiEntropy, DataProtectionScope.CurrentUser);
        if (encrypted.Length > CredentialBlobSizeLimit)
        {
            throw new InvalidOperationException(
                $"Encrypted credential blob exceeds the Cred Manager limit ({CredentialBlobSizeLimit} bytes).");
        }

        WriteCredential(BuildTargetName(profileName), credential.AccessKeyId, encrypted);
        // Best-effort: zero the plaintext JSON before the GC reclaims it.
        Array.Clear(json);
    }

    /// <inheritdoc/>
    public unsafe AwsCredential? Load(string profileName)
    {
        ValidateProfile(profileName);

        var target = BuildTargetName(profileName);
        if (!CredRead(target, CredTypeGeneric, 0, out var credPtr))
        {
            var err = Marshal.GetLastPInvokeError();
            if (err == ErrorNotFound) return null;
            throw new Win32Exception(err, $"CredRead failed for '{target}'.");
        }

        try
        {
            // The CREDENTIAL struct is fully blittable; Unsafe.Read avoids the
            // CA1421 warning that Marshal.PtrToStructure trips when the assembly
            // opts out of legacy runtime marshalling (DisableRuntimeMarshalling).
            var cred = Unsafe.Read<Credential>((void*)credPtr);
            var accessKeyId = Marshal.PtrToStringUni(cred.UserName) ?? string.Empty;
            if (string.IsNullOrEmpty(accessKeyId)) return null;

            var encrypted = ReadBlob(cred.CredentialBlob, cred.CredentialBlobSize);
            byte[] decrypted;
            try
            {
                decrypted = ProtectedData.Unprotect(encrypted, DpapiEntropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException(
                    $"Failed to decrypt credential '{profileName}'. The entry may have been written by another user.",
                    ex);
            }

            try
            {
                var payload = JsonSerializer.Deserialize(decrypted, StoredSecretJsonContext.Default.StoredSecretPayload)
                    ?? throw new InvalidOperationException($"Credential '{profileName}' is malformed.");
                return new AwsCredential
                {
                    AccessKeyId = accessKeyId,
                    SecretAccessKey = payload.SecretAccessKey,
                    SessionToken = payload.SessionToken,
                    ExpiresAt = payload.ExpiresAtUnix is { } unix
                        ? DateTimeOffset.FromUnixTimeSeconds(unix)
                        : null,
                };
            }
            finally
            {
                Array.Clear(decrypted);
            }
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    /// <inheritdoc/>
    public bool Delete(string profileName)
    {
        ValidateProfile(profileName);

        if (CredDelete(BuildTargetName(profileName), CredTypeGeneric, 0)) return true;

        var err = Marshal.GetLastPInvokeError();
        if (err == ErrorNotFound) return false;
        throw new Win32Exception(err, $"CredDelete failed for profile '{profileName}'.");
    }

    /// <inheritdoc/>
    public unsafe IReadOnlyList<string> List()
    {
        // The Win32 filter uses '*' as a wildcard; "OSVFS:AWS:*" returns every credential
        // whose target starts with our namespace.
        if (!CredEnumerate(TargetPrefix + "*", 0, out var count, out var creds))
        {
            var err = Marshal.GetLastPInvokeError();
            // ERROR_NOT_FOUND is the documented "no matches" path — surface as an empty list.
            if (err == ErrorNotFound) return [];
            throw new Win32Exception(err, "CredEnumerate failed.");
        }

        try
        {
            var profiles = new List<string>(capacity: (int)count);
            for (var i = 0; i < count; i++)
            {
                var entryPtr = Marshal.ReadIntPtr(creds, i * IntPtr.Size);
                // CREDENTIAL is blittable; Unsafe.Read keeps DisableRuntimeMarshalling happy.
                var cred = Unsafe.Read<Credential>((void*)entryPtr);
                var target = Marshal.PtrToStringUni(cred.TargetName);
                if (target is null || !target.StartsWith(TargetPrefix, StringComparison.Ordinal)) continue;
                profiles.Add(target[TargetPrefix.Length..]);
            }
            profiles.Sort(StringComparer.OrdinalIgnoreCase);
            return profiles;
        }
        finally
        {
            CredFree(creds);
        }
    }

    /// <summary>
    /// Builds the namespaced Cred Manager target name from a profile.
    /// </summary>
    private static string BuildTargetName(string profileName) => TargetPrefix + profileName;

    /// <summary>
    /// Validates that the profile name is non-empty and contains no characters that
    /// would conflict with Cred Manager's filter wildcards or our prefix delimiter.
    /// </summary>
    private static void ValidateProfile(string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        if (profileName.AsSpan().IndexOfAny('*', '?', '\0') >= 0)
        {
            throw new ArgumentException(
                "Profile name must not contain wildcard or null characters.", nameof(profileName));
        }
    }

    /// <summary>
    /// Allocates and writes a generic credential, freeing the unmanaged buffers we own
    /// regardless of whether the API call succeeds.
    /// </summary>
    private static void WriteCredential(string target, string accessKeyId, byte[] encryptedBlob)
    {
        var targetPtr = Marshal.StringToHGlobalUni(target);
        var userPtr = Marshal.StringToHGlobalUni(accessKeyId);
        var commentPtr = Marshal.StringToHGlobalUni(CredentialComment);
        var blobPtr = Marshal.AllocHGlobal(encryptedBlob.Length);
        try
        {
            Marshal.Copy(encryptedBlob, 0, blobPtr, encryptedBlob.Length);

            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetPtr,
                Comment = commentPtr,
                CredentialBlobSize = (uint)encryptedBlob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                UserName = userPtr,
            };

            if (!CredWrite(ref cred, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"CredWrite failed for '{target}'.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(userPtr);
            Marshal.FreeHGlobal(commentPtr);
            // Zero the unmanaged copy of the encrypted blob to avoid leaving DPAPI ciphertext
            // in long-lived process memory after the call.
            if (blobPtr != IntPtr.Zero)
            {
                for (var i = 0; i < encryptedBlob.Length; i++)
                {
                    Marshal.WriteByte(blobPtr, i, 0);
                }
                Marshal.FreeHGlobal(blobPtr);
            }
        }
    }

    /// <summary>
    /// Copies <paramref name="size"/> bytes from <paramref name="ptr"/> into a managed array.
    /// </summary>
    private static byte[] ReadBlob(IntPtr ptr, uint size)
    {
        if (ptr == IntPtr.Zero || size == 0) return [];
        var buffer = new byte[size];
        Marshal.Copy(ptr, buffer, 0, (int)size);
        return buffer;
    }

    /// <summary>
    /// CREDENTIALW interop layout — one-to-one with wincred.h.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    /// <summary>
    /// CredWriteW: persists or replaces a credential entry.
    /// </summary>
    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(ref Credential credential, uint flags);

    /// <summary>
    /// CredReadW: looks up a credential by exact target name.
    /// </summary>
    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    /// <summary>
    /// CredDeleteW: removes a credential by exact target name.
    /// </summary>
    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    /// <summary>
    /// CredEnumerateW: returns every credential whose target name matches the filter.
    /// </summary>
    [LibraryImport("advapi32.dll", EntryPoint = "CredEnumerateW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredEnumerate(string? filter, uint flags, out uint count, out IntPtr credentials);

    /// <summary>
    /// CredFree: releases buffers returned by CredRead and CredEnumerate.
    /// </summary>
    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}
