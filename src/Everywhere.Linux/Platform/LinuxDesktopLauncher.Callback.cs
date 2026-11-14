using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using Tmds.DBus.Protocol;

namespace Everywhere.Linux.Platform;

partial class LinuxDesktopLauncher
{
    private static readonly string[] DesktopDirectoryVariables = ["XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME"];

    /// <inheritdoc />
    public async Task<bool> EnsureUriSchemeHandlerAsync(Uri callbackUri)
    {
        if (callbackUri.Scheme != "sylinko-everywhere")
        {
            return false;
        }
        try
        {
            var desktop = await ReadDesktopEnvironmentAsync().ConfigureAwait(false);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var data = desktop.GetValueOrDefault("XDG_DATA_HOME");
            var applications = Path.Combine(
                data is not null && Path.IsPathFullyQualified(data) ? data : Path.Combine(home, ".local", "share"),
                "applications");
            Directory.CreateDirectory(applications);
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The callback executable is unavailable.");
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable)))[..16];
            var fileName = "com.sylinko.Everywhere.UrlHandler." + identity + ".desktop";
            var arguments = new List<string>
            {
                "/usr/bin/env"
            };
            // Only Everywhere receives its application profile. xdg-mime below
            // runs in the desktop broker's environment, as the browser does.
            foreach (var key in DesktopDirectoryVariables.Concat(new[]
            {
                "HYPRLAND_INSTANCE_SIGNATURE",
                "XDG_RUNTIME_DIR",
                "WAYLAND_DISPLAY",
                "DISPLAY",
                "DBUS_SESSION_BUS_ADDRESS"
            }))
            {
                if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value)
                {
                    arguments.Add(key + "=" + value);
                }
            }
            arguments.Add(executable);
            var command = string.Join(" ", arguments.Select(QuoteDesktopArgument)) + " %u";
            var entry = $"[Desktop Entry]\nType=Application\nName=Everywhere URL callback\nNoDisplay=true\nTerminal=false\nExec={command}\nMimeType=x-scheme-handler/{callbackUri.Scheme};\n";
            var path = Path.Combine(applications, fileName);
            var temporary = path + "." + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, entry).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }

            if (!await RefreshDesktopDatabaseAsync(applications).ConfigureAwait(false))
            {
                return false;
            }

            var start = new ProcessStartInfo("xdg-mime")
            {
                UseShellExecute = false,
                ArgumentList =
                {
                    "default",
                    fileName,
                    "x-scheme-handler/" + callbackUri.Scheme
                }
            };
            foreach (var key in DesktopDirectoryVariables)
            {
                if (desktop.GetValueOrDefault(key) is { } value)
                {
                    start.Environment[key] = value;
                }
                else
                {
                    start.Environment.Remove(key);
                }
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            Log.Warning("The application callback registration failed ({FailureType}).", exception.GetType().Name);
            return false;
        }
    }

    private static async Task<bool> RefreshDesktopDatabaseAsync(string applications)
    {
        var start = new ProcessStartInfo("update-desktop-database")
        {
            UseShellExecute = false,
            ArgumentList =
            {
                applications
            }
        };
        using var process = Process.Start(start);
        if (process is null)
        {
            return false;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill();
            }

            return false;
        }
    }

    private static async Task<Dictionary<string, string?>> ReadDesktopEnvironmentAsync()
    {
        var result = DesktopDirectoryVariables.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        if (DBusAddress.Session is not { } address)
        {
            return result;
        }
        try
        {
            // Resolve the actual OpenURI broker, not an arbitrary desktop process.
            // Only its XDG directory variables are retained; no environment values
            // or URI callback contents are logged.
            using var connection = new DBusConnection(address);
            await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            var pid = await connection.CallMethodAsync(
                    CreateBrokerPidRequest(connection),
                    static (message, _) => message.GetBodyReader().ReadUInt32())
                .WaitAsync(TimeSpan.FromSeconds(3))
                .ConfigureAwait(false);
            var environment = await File.ReadAllTextAsync($"/proc/{pid}/environ").ConfigureAwait(false);
            foreach (var key in DesktopDirectoryVariables)
            {
                result[key] = environment.Split('\0')
                    .FirstOrDefault(item => item.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DBusErrorReplyException or TimeoutException)
        {
            Log.Debug("Desktop callback registration will use the current session directories ({FailureType}).", exception.GetType().Name);
        }

        return result;
    }

    private static MessageBuffer CreateBrokerPidRequest(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: "org.freedesktop.DBus",
            path: "/org/freedesktop/DBus",
            @interface: "org.freedesktop.DBus",
            member: "GetConnectionUnixProcessID",
            signature: "s");
        writer.WriteString(PortalService);
        return writer.CreateMessage();
    }

    private static string QuoteDesktopArgument(string value)
    {
        // xdg-open's generic backend does not unquote simple Exec tokens. Leave
        // safe tokens bare so Hyprland can resolve the executable and env assignments.
        var escaped = value.Replace("%", "%%");
        if (!value.Any(character => char.IsWhiteSpace(character) || "\"'\\><~|&;$*?#()`".Contains(character)))
        {
            return escaped;
        }

        escaped = escaped.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$");
        // Desktop string escaping is applied before Exec argument unquoting.
        return "\"" + escaped.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";
    }
}