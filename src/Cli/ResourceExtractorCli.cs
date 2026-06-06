using System.Reflection;

namespace GHelper.Linux.Cli;

/// <summary>
/// Install-time CLI dispatcher for extracting embedded native helpers
/// </summary>
public static class ResourceExtractorCli
{
    /// <summary>
    /// If args match a known extractor invocation, run it and return the
    /// exit code. Otherwise returns null (caller proceeds with normal startup).
    /// </summary>
    public static int? TryDispatch(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--extract-helper")
            return ExtractHelper(args);
        if (args.Length >= 1 && args[0] == "--install-gpu-helper")
            return InstallHelperResource("gpu-helper", args);
        return null;
    }

    private static int InstallHelperResource(string resourceName, string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine($"usage: {args[0]} <dest-dir>");
            return 1;
        }
        string destDir = args[1];
        string fullDestDir = Path.GetFullPath(destDir);
        if (!string.Equals(fullDestDir, "/opt/ghelper", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"refusing to install {resourceName} outside /opt/ghelper");
            return 1;
        }

        string destPath = Path.Combine(destDir, resourceName);

        string tmpPath = destPath + ".new";

        int rc = ExtractHelper(new[] { "--extract-helper", resourceName, tmpPath });
        if (rc != 0)
            return rc;

        // chown root:root - we are running as root via pkexec so this succeeds
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chown",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("root:root");
            psi.ArgumentList.Add(tmpPath);
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(5000);
            if (proc?.ExitCode != 0)
            {
                Console.Error.WriteLine($"chown root:root {tmpPath} failed (exit {proc?.ExitCode})");
                try
                { File.Delete(tmpPath); }
                catch { }
                return 4;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"chown failed: {ex.Message}");
            try
            { File.Delete(tmpPath); }
            catch { }
            return 4;
        }

        try
        {
            File.Move(tmpPath, destPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"rename {tmpPath} -> {destPath} failed: {ex.Message}");
            try
            { File.Delete(tmpPath); }
            catch { }
            return 5;
        }

        Console.WriteLine($"installed {destPath} (root:root 755)");
        return 0;
    }

    private static int ExtractHelper(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: --extract-helper <resource-name> <dest-path>");
            return 1;
        }
        string resourceName = args[1];
        string destPath = args[2];

        try
        {
            using var stream = typeof(ResourceExtractorCli).Assembly
                .GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                Console.Error.WriteLine($"embedded resource '{resourceName}' not found - rebuild ghelper");
                return 2;
            }

            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using (var fs = File.Create(destPath))
                stream.CopyTo(fs);

#pragma warning disable CA1416
            File.SetUnixFileMode(destPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416

            Console.WriteLine($"extracted {resourceName} -> {destPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"extract {resourceName} -> {destPath} failed: {ex.Message}");
            return 3;
        }
    }
}
