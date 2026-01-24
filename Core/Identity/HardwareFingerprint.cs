using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MeshAI.Core.Identity;

/// <summary>
/// Generates a deterministic hardware-derived fingerprint for client identification.
/// The fingerprint is non-reversible and unique per machine.
/// </summary>
public static class HardwareFingerprint
{
    private static string? _cachedFingerprint;
    private static readonly object _lock = new();

    /// <summary>
    /// Gets the hardware fingerprint for this machine.
    /// Cached after first computation.
    /// </summary>
    public static string GetFingerprint()
    {
        if (_cachedFingerprint != null)
            return _cachedFingerprint;

        lock (_lock)
        {
            if (_cachedFingerprint != null)
                return _cachedFingerprint;

            _cachedFingerprint = ComputeFingerprint();
            return _cachedFingerprint;
        }
    }

    /// <summary>
    /// Computes the hardware fingerprint from available hardware identifiers.
    /// </summary>
    private static string ComputeFingerprint()
    {
        var components = new StringBuilder();

        // Collect hardware identifiers
        components.AppendLine(GetProcessorId());
        components.AppendLine(GetMotherboardId());
        components.AppendLine(GetMacAddresses());
        components.AppendLine(GetOsIdentifier());

        // Hash the combined components
        var bytes = Encoding.UTF8.GetBytes(components.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Gets processor identification string.
    /// </summary>
    private static string GetProcessorId()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return GetWindowsWmiValue("Win32_Processor", "ProcessorId");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return ReadLinuxFile("/proc/cpuinfo", "model name");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return ExecuteCommand("sysctl", "-n machdep.cpu.brand_string");
            }
        }
        catch
        {
            // Fall through to default
        }
        return $"CPU:{Environment.ProcessorCount}";
    }

    /// <summary>
    /// Gets motherboard/system UUID.
    /// </summary>
    private static string GetMotherboardId()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return GetWindowsWmiValue("Win32_BaseBoard", "SerialNumber");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var uuid = ReadFile("/sys/class/dmi/id/product_uuid");
                if (string.IsNullOrEmpty(uuid))
                    uuid = ReadFile("/etc/machine-id");
                return uuid;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return ExecuteCommand("ioreg", "-rd1 -c IOPlatformExpertDevice")
                    .Split('\n')
                    .FirstOrDefault(l => l.Contains("IOPlatformUUID"))
                    ?? "UNKNOWN";
            }
        }
        catch
        {
            // Fall through to default
        }
        return $"MB:{Environment.MachineName}";
    }

    /// <summary>
    /// Gets MAC addresses of network interfaces.
    /// </summary>
    private static string GetMacAddresses()
    {
        try
        {
            var macs = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                           && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(nic => nic.GetPhysicalAddress().ToString())
                .Where(mac => !string.IsNullOrEmpty(mac) && mac != "000000000000")
                .OrderBy(mac => mac)
                .Take(3); // Take first 3 for stability

            var result = string.Join(",", macs);
            return string.IsNullOrEmpty(result) ? "MAC:NONE" : result;
        }
        catch
        {
            return "MAC:UNAVAILABLE";
        }
    }

    /// <summary>
    /// Gets OS-specific identifier for additional uniqueness.
    /// </summary>
    private static string GetOsIdentifier()
    {
        return $"{RuntimeInformation.OSDescription}:{Environment.UserName}";
    }

    /// <summary>
    /// Reads a WMI value on Windows.
    /// </summary>
    private static string GetWindowsWmiValue(string wmiClass, string property)
    {
        try
        {
            var output = ExecuteCommand("wmic", $"{wmiClass} get {property} /value");
            var line = output.Split('\n')
                .FirstOrDefault(l => l.Contains('='));
            return line?.Split('=').LastOrDefault()?.Trim() ?? "UNKNOWN";
        }
        catch
        {
            return "UNKNOWN";
        }
    }

    /// <summary>
    /// Reads a value from a Linux /proc or /sys file.
    /// </summary>
    private static string ReadLinuxFile(string path, string key)
    {
        try
        {
            var content = File.ReadAllText(path);
            var line = content.Split('\n')
                .FirstOrDefault(l => l.StartsWith(key, StringComparison.OrdinalIgnoreCase));
            return line?.Split(':').LastOrDefault()?.Trim() ?? ReadFile(path);
        }
        catch
        {
            return "UNKNOWN";
        }
    }

    /// <summary>
    /// Reads entire file content.
    /// </summary>
    private static string ReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Executes a command and returns stdout.
    /// </summary>
    private static string ExecuteCommand(string command, string args)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = command,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output.Trim();
        }
        catch
        {
            return string.Empty;
        }
    }
}
