using HidSharp;

internal enum TemperHumProtocol
{
    TemperXV31
}

internal sealed record DeviceProfile(
    string Id,
    string DisplayName,
    int VendorId,
    int ProductId,
    string PreferredInterfaceToken,
    int MinimumInputReportLength,
    int MinimumOutputReportLength,
    TemperHumProtocol Protocol)
{
    public string VidPid => $"{VendorId:X4}:{ProductId:X4}";

    public bool IsCompatibleInterface(HidDevice device)
    {
#pragma warning disable CS0612
        return device.MaxInputReportLength >= MinimumInputReportLength &&
               device.MaxOutputReportLength >= MinimumOutputReportLength;
#pragma warning restore CS0612
    }
}

internal static class DeviceProfiles
{
    public const string Auto = "auto";

    public static DeviceProfile TemperX413D2107 { get; } = new(
        Id: "pcsensor-413d-2107-temperx-v31",
        DisplayName: "PCsensor TEMPerHUM/TEMPerX 413D:2107 (TEMPerX_V3.1 protocol)",
        VendorId: 0x413D,
        ProductId: 0x2107,
        PreferredInterfaceToken: "mi_01",
        MinimumInputReportLength: 9,
        MinimumOutputReportLength: 9,
        Protocol: TemperHumProtocol.TemperXV31);

    public static IReadOnlyList<DeviceProfile> Supported { get; } =
        [TemperX413D2107];

    // These VID/PID pairs have appeared in public TEMPer/TEMPerHUM tooling,
    // but are NOT treated as supported until their protocol is implemented and
    // validated. Keeping them here makes --probe output useful without guessing.
    private static readonly HashSet<(int VendorId, int ProductId)> KnownUnsupportedCandidates =
    [
        (0x1A86, 0xE025),
        (0x0C45, 0x7402)
    ];

    public static DeviceProfile Resolve(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) ||
            profileId.Equals(Auto, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The 'auto' device profile must be resolved from attached hardware.");
        }

        return Supported.FirstOrDefault(profile =>
                   profile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException(
                   $"Unknown device profile '{profileId}'. Supported profile IDs: {string.Join(", ", Supported.Select(p => p.Id))}.");
    }

    public static bool IsKnownUnsupportedCandidate(int vendorId, int productId) =>
        KnownUnsupportedCandidates.Contains((vendorId, productId));

    public static bool IsSupportedVidPid(int vendorId, int productId) =>
        Supported.Any(profile => profile.VendorId == vendorId && profile.ProductId == productId);

    public static string SupportLabel(HidDevice device)
    {
        var profile = Supported.FirstOrDefault(p =>
            p.VendorId == device.VendorID && p.ProductId == device.ProductID);

        if (profile is not null)
        {
            return profile.IsCompatibleInterface(device)
                ? $"SUPPORTED ({profile.Id})"
                : $"known VID/PID, interface shape not supported ({profile.Id})";
        }

        if (IsKnownUnsupportedCandidate(device.VendorID, device.ProductID))
        {
            return "known TEMPer-family candidate; protocol NOT supported";
        }

        return "unknown / not supported";
    }
}

internal static class DeviceProbe
{
    public static int Run(bool includeAll, int? vendorFilter, int? productFilter)
    {
        Console.WriteLine($"TemperHumAlpaca v{AppInfo.Version} HID probe");
        Console.WriteLine("Probe mode is read-only: no TEMPerHUM measurement command is sent.\n");

        var all = DeviceList.Local.GetHidDevices().ToList();
        var devices = all
            .Where(device => MatchesFilter(device, vendorFilter, productFilter))
            .Where(device => includeAll || IsCandidate(device))
            .OrderBy(device => device.VendorID)
            .ThenBy(device => device.ProductID)
            .ThenBy(device => device.DevicePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (devices.Count == 0)
        {
            Console.WriteLine(includeAll
                ? "No HID interfaces matched the requested filter."
                : "No likely TEMPerHUM HID interfaces were identified.");
            Console.WriteLine("If the sensor is attached but absent here, run --probe-all and optionally filter with --vid/--pid.");
            return 0;
        }

        Console.WriteLine($"Found {devices.Count} HID interface(s):\n");
        for (var i = 0; i < devices.Count; i++)
        {
            PrintDevice(i, devices[i]);
        }

        Console.WriteLine("Supported device profiles:");
        foreach (var profile in DeviceProfiles.Supported)
        {
            Console.WriteLine($"  {profile.Id}  VID:PID {profile.VidPid}  {profile.DisplayName}");
        }

        Console.WriteLine("\nUnknown or candidate devices are diagnostics only; TemperHumAlpaca will not auto-connect to them.");
        return 0;
    }

    private static bool MatchesFilter(HidDevice device, int? vendorFilter, int? productFilter) =>
        (!vendorFilter.HasValue || device.VendorID == vendorFilter.Value) &&
        (!productFilter.HasValue || device.ProductID == productFilter.Value);

    private static bool IsCandidate(HidDevice device)
    {
        if (DeviceProfiles.IsSupportedVidPid(device.VendorID, device.ProductID) ||
            DeviceProfiles.IsKnownUnsupportedCandidate(device.VendorID, device.ProductID))
        {
            return true;
        }

        var metadata = $"{SafeGet(device.GetManufacturer)} {SafeGet(device.GetProductName)}";
        return metadata.Contains("temper", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("pcsensor", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("humidity", StringComparison.OrdinalIgnoreCase);
    }

    private static void PrintDevice(int index, HidDevice device)
    {
        Console.WriteLine($"[{index}] VID:PID {device.VendorID:X4}:{device.ProductID:X4}");
        Console.WriteLine($"    support: {DeviceProfiles.SupportLabel(device)}");
        Console.WriteLine($"    manufacturer: {SafeGet(device.GetManufacturer)}");
        Console.WriteLine($"    product: {SafeGet(device.GetProductName)}");
        Console.WriteLine($"    serial: {SafeGet(device.GetSerialNumber)}");
#pragma warning disable CS0612
        Console.WriteLine($"    reports: input={device.MaxInputReportLength}, output={device.MaxOutputReportLength}, feature={device.MaxFeatureReportLength}");
#pragma warning restore CS0612
        Console.WriteLine($"    path: {device.DevicePath}\n");
    }

    private static string SafeGet(Func<string> getter)
    {
        try
        {
            var value = getter();
            return string.IsNullOrWhiteSpace(value) ? "(not reported)" : value;
        }
        catch (Exception ex)
        {
            return $"(unavailable: {ex.GetType().Name})";
        }
    }
}
