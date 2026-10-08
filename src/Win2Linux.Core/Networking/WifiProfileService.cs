using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace Win2Linux.Core.Networking;

public sealed record SavedWifiProfile(
    Guid InterfaceId,
    string InterfaceDescription,
    string ProfileName,
    string Ssid,
    string Authentication,
    bool IsOpenNetwork,
    bool IsPersonalNetwork);

public sealed record WifiCredentials(string Ssid, string? Password, bool IsOpenNetwork)
{
    public override string ToString() => $"WifiCredentials {{ Ssid = {Ssid}, IsOpenNetwork = {IsOpenNetwork} }}";
}

/// <summary>
/// Lists saved Windows WLAN profiles and reads a key only after a user selects one.
/// The Windows WLAN API returns profile XML; this service never logs or persists it.
/// </summary>
public sealed class WifiProfileService
{
    private const uint WlanProfileGetPlaintextKey = 0x00000004;
    private static readonly XNamespace ProfileNamespace = "http://www.microsoft.com/networking/WLAN/profile/v1";

    public IReadOnlyList<SavedWifiProfile> GetSavedProfiles()
    {
        using var client = new WlanClient();
        IntPtr interfaceList = IntPtr.Zero;
        try
        {
            Check(WlanEnumInterfaces(client.Handle, IntPtr.Zero, out interfaceList), "Could not enumerate Wi-Fi adapters");
            var interfaceCount = Marshal.ReadInt32(interfaceList);
            var interfaceSize = Marshal.SizeOf<WlanInterfaceInfo>();
            var profiles = new List<SavedWifiProfile>();

            for (var interfaceIndex = 0; interfaceIndex < interfaceCount; interfaceIndex++)
            {
                var interfacePointer = IntPtr.Add(interfaceList, sizeof(uint) * 2 + interfaceIndex * interfaceSize);
                var wlanInterface = Marshal.PtrToStructure<WlanInterfaceInfo>(interfacePointer);
                IntPtr profileList = IntPtr.Zero;
                try
                {
                    var result = WlanGetProfileList(client.Handle, ref wlanInterface.InterfaceGuid, IntPtr.Zero, out profileList);
                    if (result != 0)
                    {
                        // A disconnected or policy-managed adapter can reject profile enumeration;
                        // continue so profiles from other adapters remain available.
                        continue;
                    }

                    var profileCount = Marshal.ReadInt32(profileList);
                    var profileSize = Marshal.SizeOf<WlanProfileInfo>();
                    for (var profileIndex = 0; profileIndex < profileCount; profileIndex++)
                    {
                        var profilePointer = IntPtr.Add(profileList, sizeof(uint) * 2 + profileIndex * profileSize);
                        var profileInfo = Marshal.PtrToStructure<WlanProfileInfo>(profilePointer);
                        if (string.IsNullOrWhiteSpace(profileInfo.ProfileName))
                        {
                            continue;
                        }

                        try
                        {
                            var xml = GetProfileXml(client.Handle, wlanInterface.InterfaceGuid, profileInfo.ProfileName, includePlaintextKey: false);
                            var parsed = ParseProfile(xml);
                            if (string.IsNullOrWhiteSpace(parsed.Ssid))
                            {
                                continue;
                            }

                            var authentication = parsed.Authentication;
                            profiles.Add(new SavedWifiProfile(
                                wlanInterface.InterfaceGuid,
                                wlanInterface.InterfaceDescription ?? "Wi-Fi adapter",
                                profileInfo.ProfileName,
                                parsed.Ssid,
                                authentication,
                                IsOpenAuthentication(authentication),
                                IsPersonalAuthentication(authentication)));
                        }
                        catch
                        {
                            // Skip malformed or inaccessible profiles without exposing their XML.
                        }
                    }
                }
                finally
                {
                    if (profileList != IntPtr.Zero)
                    {
                        WlanFreeMemory(profileList);
                    }
                }
            }

            return profiles
                .OrderBy(profile => profile.Ssid, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(profile => profile.InterfaceDescription, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        finally
        {
            if (interfaceList != IntPtr.Zero)
            {
                WlanFreeMemory(interfaceList);
            }
        }
    }

    public WifiCredentials ReadSelectedCredentials(SavedWifiProfile profile)
    {
        using var client = new WlanClient();
        var xml = GetProfileXml(client.Handle, profile.InterfaceId, profile.ProfileName, includePlaintextKey: true);
        var parsed = ParseProfile(xml);

        if (IsOpenAuthentication(parsed.Authentication))
        {
            return new WifiCredentials(parsed.Ssid, null, IsOpenNetwork: true);
        }

        if (!IsPersonalAuthentication(parsed.Authentication))
        {
            throw new InvalidOperationException(
                $"The selected profile uses {parsed.Authentication}, which this installer cannot configure automatically. Select an open or WPA personal network.");
        }

        if (string.IsNullOrWhiteSpace(parsed.Password))
        {
            throw new InvalidOperationException(
                "Windows did not provide the saved Wi-Fi key. Run Win2Linux as administrator and refresh the saved networks.");
        }

        if (parsed.PasswordIsProtected)
        {
            throw new InvalidOperationException(
                "Windows returned the saved Wi-Fi key in protected form. Run Win2Linux as administrator and select the network again.");
        }

        return new WifiCredentials(parsed.Ssid, parsed.Password, IsOpenNetwork: false);
    }

    private static string GetProfileXml(IntPtr clientHandle, Guid interfaceId, string profileName, bool includePlaintextKey)
    {
        uint flags = includePlaintextKey ? WlanProfileGetPlaintextKey : 0;
        IntPtr profileXml = IntPtr.Zero;
        try
        {
            var result = WlanGetProfile(
                clientHandle,
                ref interfaceId,
                profileName,
                IntPtr.Zero,
                out profileXml,
                ref flags,
                out _);

            if (result != 0)
            {
                var operation = includePlaintextKey
                    ? "Could not read the selected saved Wi-Fi key"
                    : "Could not read a saved Wi-Fi profile";
                throw new InvalidOperationException($"{operation} (Windows error {result}).");
            }

            return Marshal.PtrToStringUni(profileXml)
                ?? throw new InvalidOperationException("Windows returned an empty Wi-Fi profile.");
        }
        finally
        {
            if (profileXml != IntPtr.Zero)
            {
                WlanFreeMemory(profileXml);
            }
        }
    }

    private static ParsedProfile ParseProfile(string xml)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root ?? throw new InvalidOperationException("The saved Wi-Fi profile is invalid.");
        var ssidNode = root.Element(ProfileNamespace + "SSIDConfig")?.Element(ProfileNamespace + "SSID");
        var ssid = ssidNode?.Element(ProfileNamespace + "name")?.Value;
        if (string.IsNullOrWhiteSpace(ssid))
        {
            var encodedSsid = ssidNode?.Element(ProfileNamespace + "hex")?.Value;
            if (!string.IsNullOrWhiteSpace(encodedSsid))
            {
                try
                {
                    ssid = Encoding.UTF8.GetString(Convert.FromHexString(encodedSsid));
                }
                catch (FormatException)
                {
                    ssid = null;
                }
            }
        }

        var security = root.Element(ProfileNamespace + "MSM")?.Element(ProfileNamespace + "security");
        var authentication = security?
            .Element(ProfileNamespace + "authEncryption")?
            .Element(ProfileNamespace + "authentication")?
            .Value ?? "unknown";
        var sharedKey = security?.Element(ProfileNamespace + "sharedKey");
        var password = sharedKey?.Element(ProfileNamespace + "keyMaterial")?.Value;
        var passwordIsProtected = !bool.TryParse(
            sharedKey?.Element(ProfileNamespace + "protected")?.Value,
            out var isProtected) || isProtected;

        return new ParsedProfile(ssid ?? string.Empty, authentication, password, passwordIsProtected);
    }

    private static bool IsOpenAuthentication(string authentication) =>
        authentication.Equals("open", StringComparison.OrdinalIgnoreCase);

    private static bool IsPersonalAuthentication(string authentication) =>
        authentication.Equals("WPAPSK", StringComparison.OrdinalIgnoreCase) ||
        authentication.Equals("WPA2PSK", StringComparison.OrdinalIgnoreCase) ||
        authentication.Equals("WPA3SAE", StringComparison.OrdinalIgnoreCase);

    private static void Check(uint result, string operation)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"{operation} (Windows error {result}).");
        }
    }

    private sealed record ParsedProfile(string Ssid, string Authentication, string? Password, bool PasswordIsProtected);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string InterfaceDescription;

        public uint State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanProfileInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ProfileName;

        public uint Flags;
    }

    private sealed class WlanClient : IDisposable
    {
        public IntPtr Handle { get; }

        public WlanClient()
        {
            var result = WlanOpenHandle(2, IntPtr.Zero, out _, out var handle);
            Check(result, "Could not open the Windows Wi-Fi service");
            Handle = handle;
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                WlanCloseHandle(Handle, IntPtr.Zero);
            }
        }
    }

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanGetProfileList(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr reserved, out IntPtr profileList);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint WlanGetProfile(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        string profileName,
        IntPtr reserved,
        out IntPtr profileXml,
        ref uint flags,
        out uint grantedAccess);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern void WlanFreeMemory(IntPtr memory);
}
