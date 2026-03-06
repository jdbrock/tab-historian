using System.Management;
using System.Runtime.Versioning;

namespace TabHistorian.Services;

/// <summary>
/// Creates and manages VSS (Volume Shadow Copy) snapshots to read files
/// that are exclusively locked by other processes (e.g. Chrome session files).
/// Requires elevation (admin privileges) to create shadow copies.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VssShadowCopy : IDisposable
{
    private readonly ILogger<VssShadowCopy> _logger;
    private string? _shadowId;
    private string? _shadowDevicePath;

    public VssShadowCopy(ILogger<VssShadowCopy> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The device path of the shadow copy (e.g. \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3).
    /// Available after calling Create().
    /// </summary>
    public string? DevicePath => _shadowDevicePath;

    /// <summary>
    /// Creates a VSS shadow copy for the volume containing the given path.
    /// Returns true on success.
    /// </summary>
    public bool Create(string anyPathOnVolume)
    {
        try
        {
            var volume = Path.GetPathRoot(Path.GetFullPath(anyPathOnVolume));
            if (string.IsNullOrEmpty(volume))
                return false;

            // Ensure volume ends with backslash (WMI requirement)
            if (!volume.EndsWith('\\'))
                volume += "\\";

            _logger.LogDebug("Creating VSS shadow copy for volume {Volume}", volume);

            using var shadowClass = new ManagementClass("Win32_ShadowCopy");
            var inParams = shadowClass.GetMethodParameters("Create");
            inParams["Volume"] = volume;
            inParams["Context"] = "ClientAccessible";

            var outParams = shadowClass.InvokeMethod("Create", inParams, null);
            var returnValue = (uint)(outParams["ReturnValue"] ?? 1);

            if (returnValue != 0)
            {
                _logger.LogWarning("VSS shadow copy creation failed with code {Code}", returnValue);
                return false;
            }

            _shadowId = (string)outParams["ShadowID"];
            _logger.LogDebug("Created VSS shadow copy {ShadowId}", _shadowId);

            // Query the shadow copy to get the device path
            using var searcher = new ManagementObjectSearcher(
                $"SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID='{_shadowId}'");
            foreach (var obj in searcher.Get())
            {
                _shadowDevicePath = (string)obj["DeviceObject"];
                break;
            }

            if (_shadowDevicePath == null)
            {
                _logger.LogWarning("Could not find device path for shadow copy {ShadowId}", _shadowId);
                Delete();
                return false;
            }

            _logger.LogDebug("VSS shadow copy device path: {Path}", _shadowDevicePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create VSS shadow copy (elevation required)");
            return false;
        }
    }

    /// <summary>
    /// Converts a real file path to its equivalent path within the shadow copy.
    /// E.g. C:\Users\Joe\file.txt -> \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3\Users\Joe\file.txt
    /// </summary>
    public string? GetShadowPath(string realPath)
    {
        if (_shadowDevicePath == null) return null;

        var fullPath = Path.GetFullPath(realPath);
        var root = Path.GetPathRoot(fullPath);
        if (root == null) return null;

        var relativePath = fullPath[root.Length..];
        return Path.Combine(_shadowDevicePath, relativePath);
    }

    /// <summary>
    /// Copies a file from the shadow copy to a destination path.
    /// Returns true on success.
    /// </summary>
    public bool CopyFile(string sourceRealPath, string destPath)
    {
        var shadowPath = GetShadowPath(sourceRealPath);
        if (shadowPath == null) return false;

        try
        {
            File.Copy(shadowPath, destPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to copy from shadow copy: {Path}", shadowPath);
            return false;
        }
    }

    /// <summary>
    /// Deletes the shadow copy.
    /// </summary>
    public void Delete()
    {
        if (_shadowId == null) return;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_ShadowCopy WHERE ID='{_shadowId}'");
            foreach (var obj in searcher.Get())
            {
                ((ManagementObject)obj).Delete();
                _logger.LogDebug("Deleted VSS shadow copy {ShadowId}", _shadowId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete VSS shadow copy {ShadowId}", _shadowId);
        }

        _shadowId = null;
        _shadowDevicePath = null;
    }

    public void Dispose() => Delete();
}
