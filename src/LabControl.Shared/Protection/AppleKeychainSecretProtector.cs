using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Protection;

/// <summary>
/// macOS Keychain, through the <c>SecItem</c> family (ARCHITECTURE §3.2). The secret never
/// appears in an argument vector or a temporary file — the alternative, shelling out to
/// <c>/usr/bin/security</c>, would put the private key on a command line for every process
/// on the machine to read.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class AppleKeychainSecretProtector : ISecretProtector
{
    public const string ProtectorName = ProtectorNames.Keychain;

    /// <summary>The keychain service every LabControl item is filed under.</summary>
    private const string ServiceName = "LabControl";

    private const int ErrorSuccess = 0;
    private const int ErrorItemNotFound = -25300;

    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint Utf8Encoding = 0x08000100;

    public string Name => ProtectorName;

    public bool IsAvailable => OperatingSystem.IsMacOS() && Symbols.Loaded;

    public ProtectedSecret Protect(string reference, ReadOnlySpan<byte> secret)
    {
        // An item for this reference may exist from an earlier instance; SecItemAdd would
        // return errSecDuplicateItem rather than replace it.
        Delete(reference);

        var attributes = new List<(IntPtr Key, IntPtr Value)>();
        var owned = new List<IntPtr>();

        try
        {
            var service = CreateString(ServiceName);
            var account = CreateString(reference);
            var data = CreateData(secret);
            owned.AddRange([service, account, data]);

            attributes.Add((Symbols.SecClass, Symbols.SecClassGenericPassword));
            attributes.Add((Symbols.SecAttrService, service));
            attributes.Add((Symbols.SecAttrAccount, account));
            attributes.Add((Symbols.SecValueData, data));

            var query = CreateDictionary(attributes);
            owned.Add(query);

            var status = SecItemAdd(query, IntPtr.Zero);
            if (status != ErrorSuccess)
            {
                throw new InvalidOperationException(
                    $"The macOS Keychain refused to store '{reference}' (OSStatus {status}).");
            }

            return new ProtectedSecret
            {
                Protector = ProtectorName,
                Reference = reference,
            };
        }
        finally
        {
            Release(owned);
        }
    }

    public bool TryUnprotect(ProtectedSecret secret, out byte[] plaintext)
    {
        plaintext = [];
        var owned = new List<IntPtr>();

        try
        {
            var query = CreateQuery(secret.Reference, owned, returnData: true);
            var status = SecItemCopyMatching(query, out var result);
            if (status != ErrorSuccess || result == IntPtr.Zero)
            {
                return false;
            }

            owned.Add(result);
            var length = (int)CFDataGetLength(result);
            var bytes = new byte[length];
            if (length > 0)
            {
                Marshal.Copy(CFDataGetBytePtr(result), bytes, 0, length);
            }

            plaintext = bytes;
            return true;
        }
        finally
        {
            Release(owned);
        }
    }

    public void Forget(ProtectedSecret secret) => Delete(secret.Reference);

    private static void Delete(string reference)
    {
        var owned = new List<IntPtr>();
        try
        {
            var status = SecItemDelete(CreateQuery(reference, owned, returnData: false));
            if (status is not (ErrorSuccess or ErrorItemNotFound))
            {
                throw new InvalidOperationException(
                    $"The macOS Keychain refused to remove '{reference}' (OSStatus {status}).");
            }
        }
        finally
        {
            Release(owned);
        }
    }

    private static IntPtr CreateQuery(string reference, List<IntPtr> owned, bool returnData)
    {
        var service = CreateString(ServiceName);
        var account = CreateString(reference);
        owned.AddRange([service, account]);

        var attributes = new List<(IntPtr, IntPtr)>
        {
            (Symbols.SecClass, Symbols.SecClassGenericPassword),
            (Symbols.SecAttrService, service),
            (Symbols.SecAttrAccount, account),
        };

        if (returnData)
        {
            attributes.Add((Symbols.SecReturnData, Symbols.BooleanTrue));
            attributes.Add((Symbols.SecMatchLimit, Symbols.SecMatchLimitOne));
        }

        var query = CreateDictionary(attributes);
        owned.Add(query);
        return query;
    }

    private static IntPtr CreateDictionary(IReadOnlyList<(IntPtr Key, IntPtr Value)> attributes)
    {
        var keys = attributes.Select(pair => pair.Key).ToArray();
        var values = attributes.Select(pair => pair.Value).ToArray();

        var dictionary = CFDictionaryCreate(
            IntPtr.Zero, keys, values, keys.Length,
            Symbols.TypeDictionaryKeyCallBacks, Symbols.TypeDictionaryValueCallBacks);

        return dictionary != IntPtr.Zero
            ? dictionary
            : throw new InvalidOperationException("CoreFoundation refused to build the Keychain query.");
    }

    private static IntPtr CreateString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var handle = CFStringCreateWithBytes(IntPtr.Zero, bytes, bytes.Length, Utf8Encoding, false);
        return handle != IntPtr.Zero
            ? handle
            : throw new InvalidOperationException("CoreFoundation refused to build a string.");
    }

    private static IntPtr CreateData(ReadOnlySpan<byte> value)
    {
        var handle = CFDataCreate(IntPtr.Zero, value.ToArray(), value.Length);
        return handle != IntPtr.Zero
            ? handle
            : throw new InvalidOperationException("CoreFoundation refused to build a data object.");
    }

    private static void Release(List<IntPtr> handles)
    {
        foreach (var handle in handles)
        {
            if (handle != IntPtr.Zero)
            {
                CFRelease(handle);
            }
        }

        handles.Clear();
    }

    // ------------------------------------------------------------------ interop

    [DllImport(SecurityFramework)]
    private static extern int SecItemAdd(IntPtr attributes, IntPtr result);

    [DllImport(SecurityFramework)]
    private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport(SecurityFramework)]
    private static extern int SecItemDelete(IntPtr query);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFStringCreateWithBytes(
        IntPtr allocator, byte[] bytes, nint length, uint encoding, [MarshalAs(UnmanagedType.U1)] bool isExternal);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundationFramework)]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFDictionaryCreate(
        IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr handle);

    /// <summary>
    /// The Keychain API is driven by exported <c>CFStringRef</c> constants rather than
    /// enums, so they are resolved once from the two frameworks and cached.
    /// </summary>
    private static class Symbols
    {
        static Symbols()
        {
            try
            {
                var security = NativeLibrary.Load(SecurityFramework);
                var coreFoundation = NativeLibrary.Load(CoreFoundationFramework);

                SecClass = Dereference(security, "kSecClass");
                SecClassGenericPassword = Dereference(security, "kSecClassGenericPassword");
                SecAttrService = Dereference(security, "kSecAttrService");
                SecAttrAccount = Dereference(security, "kSecAttrAccount");
                SecValueData = Dereference(security, "kSecValueData");
                SecReturnData = Dereference(security, "kSecReturnData");
                SecMatchLimit = Dereference(security, "kSecMatchLimit");
                SecMatchLimitOne = Dereference(security, "kSecMatchLimitOne");
                BooleanTrue = Dereference(coreFoundation, "kCFBooleanTrue");

                // Callback tables are passed by address, not dereferenced.
                TypeDictionaryKeyCallBacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryKeyCallBacks");
                TypeDictionaryValueCallBacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryValueCallBacks");

                Loaded = true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                Loaded = false;
            }
        }

        public static bool Loaded { get; }

        public static IntPtr SecClass { get; }
        public static IntPtr SecClassGenericPassword { get; }
        public static IntPtr SecAttrService { get; }
        public static IntPtr SecAttrAccount { get; }
        public static IntPtr SecValueData { get; }
        public static IntPtr SecReturnData { get; }
        public static IntPtr SecMatchLimit { get; }
        public static IntPtr SecMatchLimitOne { get; }
        public static IntPtr BooleanTrue { get; }
        public static IntPtr TypeDictionaryKeyCallBacks { get; }
        public static IntPtr TypeDictionaryValueCallBacks { get; }

        private static IntPtr Dereference(IntPtr library, string symbol) =>
            Marshal.ReadIntPtr(NativeLibrary.GetExport(library, symbol));
    }
}
