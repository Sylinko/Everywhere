using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Everywhere.Rpc;
using MessagePack;
#if WINDOWS
using System.Runtime.Versioning;
using Everywhere.Utilities;
#endif

namespace Everywhere.Watchdog;

#if WINDOWS
[SupportedOSPlatform("windows5.1.2600")]
#endif
public static class Program
{
    private static readonly ConcurrentDictionary<long, MonitoredProcess> MonitoredProcesses = new();

    private record MonitoredProcess(Process Process, IDisposable? JobObject);

    public static async Task Main(string[] args)
    {
        // Ensure we use UTF-8 for all I/O to avoid encoding issues (e.g. ??? in output)
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0)
        {
            await Console.Error.WriteLineAsync("No arguments provided. Exiting.");
            Environment.Exit(1);
        }

        var pipeName = args[0];
        Console.WriteLine($"Started. Waiting for main application to connect with pipe name: {pipeName}");

        await using var clientStream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.In,
            PipeOptions.Asynchronous);

        try
        {
            await clientStream.ConnectAsync(5000).ConfigureAwait(false);
            Console.WriteLine("Main application connected. Listening for commands...");

            var lengthBuffer = new byte[4];
            while (clientStream.IsConnected)
            {
                var bytesRead = await clientStream.ReadAsync(lengthBuffer.AsMemory(0, 4));
                if (bytesRead < 4) break;

                var messageLength = BitConverter.ToInt32(lengthBuffer, 0);
                var messageBuffer = new byte[messageLength];
                await clientStream.ReadExactlyAsync(messageBuffer, 0, messageLength);

                var command = MessagePackSerializer.Deserialize<WatchdogCommand>(messageBuffer);
                ProcessCommand(command);
            }
        }
        catch (TimeoutException)
        {
            await Console.Error.WriteLineAsync("Timeout waiting for main application to connect. Exiting...");
        }
        catch (IOException)
        {
            await Console.Error.WriteLineAsync("Connection lost. Main application has likely exited.");
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"An unexpected error occurred: {ex.Message}");
        }
        finally
        {
            TerminateAllSubprocesses();
            Console.WriteLine("Job finished. Exiting...");
        }
    }

    private static void ProcessCommand(WatchdogCommand? command)
    {
        switch (command)
        {
            case RegisterSubprocessCommand registerCmd:
            {
                try
                {
                    MonitoredProcesses.GetOrAdd(
                        registerCmd.ProcessId,
                        id =>
                        {
                            var process = Process.GetProcessById((int)id);

#if WINDOWS
                            WindowsJobObject? windowsJobObject = null;
                            try
                            {
                                windowsJobObject = new WindowsJobObject();
                                windowsJobObject.AssignProcess(process);

                                Console.WriteLine($"Registered process '{process.ProcessName}' (ID: {process.Id}).");
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"Failed to assign process {process.Id} to Job Object: {ex.Message}");
                                DisposeHelper.DisposeToDefault(ref windowsJobObject);
                            }

                            return new MonitoredProcess(process, windowsJobObject);
#else
                            return new MonitoredProcess(process, null);
#endif
                        });
                }
                catch (ArgumentException)
                {
                    Console.WriteLine($"Process with ID {registerCmd.ProcessId} not found.");
                }
                break;
            }
            case UnregisterSubprocessCommand unregisterCmd:
            {
                if (MonitoredProcesses.TryRemove(unregisterCmd.ProcessId, out var p))
                {
                    Console.WriteLine(
                        $"Unregistered process '{p.Process.ProcessName}' (ID: {p.Process.Id}, KillIfRunning: {unregisterCmd.KillIfRunning}).");

                    if (unregisterCmd.KillIfRunning && !p.Process.HasExited)
                    {
                        Console.WriteLine($"Killing process '{p.Process.ProcessName}' (ID: {p.Process.Id}).");
                        TerminateProcessTree(p);
                    }
#if WINDOWS
                    else if (!unregisterCmd.KillIfRunning)
                    {
                        if (p.JobObject is WindowsJobObject winJob)
                        {
                            winJob.ClearKillOnJobClose();
                        }
                    }
#endif

                    p.JobObject?.Dispose();
                    p.Process.Dispose();
                }
                break;
            }
        }
    }

    private static void TerminateAllSubprocesses()
    {
        Console.WriteLine($"Terminating {MonitoredProcesses.Count} monitored process(es)...");
        foreach (var (key, value) in MonitoredProcesses)
        {
            try
            {
                if (value.Process.HasExited) continue;

                Console.WriteLine($"Killing process '{value.Process.ProcessName}' (ID: {key}).");
                TerminateProcessTree(value);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to terminate process {key}: {ex.Message}");
            }
            finally
            {
                value.JobObject?.Dispose();
                value.Process.Dispose();
            }
        }

        MonitoredProcesses.Clear();
    }

    private static void TerminateProcessTree(MonitoredProcess p)
    {
#if WINDOWS
        if (p.JobObject is WindowsJobObject winJob)
        {
            winJob.Terminate();
        }
        else
        {
            // Fallback if Job Object failed to create/assign
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/PID {p.Process.Id} /T /F",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                })?.WaitForExit();
        }
#elif LINUX || MACOS
        TerminateUnixProcessTree(p.Process.Id);
#else
        #error Unsupported platform
#endif
    }

#if LINUX || MACOS
    private static void TerminateUnixProcessTree(int pid)
    {
        try
        {
            // 1. Try to find all children using ps
            var processMap = GetUnixProcessMap();
            var processesToKill = new HashSet<int> { pid };
            var queue = new Queue<int>();
            queue.Enqueue(pid);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!processMap.TryGetValue(current, out var children)) continue;
                foreach (var child in children.Where(processesToKill.Add))
                {
                    queue.Enqueue(child);
                }
            }
            Console.WriteLine($"Terminating process tree for PID {pid}: {string.Join(", ", processesToKill)}");

            // 2. Kill them all
            foreach (var p in processesToKill)
            {
                try
                {
                    Process.GetProcessById(p).Kill();
                }
                catch (Exception)
                {
                    // Fallback to kill command
                    Console.Error.WriteLine($"Failed to terminate process with ID {p}, falling back to 'kill -9'.");
                    Process.Start(
                        new ProcessStartInfo
                        {
                            FileName = "kill",
                            Arguments = $"-9 {p}",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        })?.WaitForExit();
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to terminate process tree for {pid}: {ex.Message}");
            // Fallback to PGID kill if the above failed catastrophically
            try
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = "/bin/sh",
                        Arguments = $"-c \"kill -9 -$(ps -o pgid= -p {pid} | tr -d ' ')\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit();
            }
            catch (Exception ex2)
            {
                Console.Error.WriteLine($"Failed to terminate process group for {pid}: {ex2.Message}");
            }
        }
    }

    private static Dictionary<int, List<int>> GetUnixProcessMap()
    {
        var map = new Dictionary<int, List<int>>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ps",
                Arguments = "-A -o ppid,pid",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                while (!proc.StandardOutput.EndOfStream)
                {
                    var line = proc.StandardOutput.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[0], out var ppid) && int.TryParse(parts[1], out var childPid))
                    {
                        if (!map.TryGetValue(ppid, out var list))
                        {
                            list = [];
                            map[ppid] = list;
                        }

                        list.Add(childPid);
                    }
                }

                proc.WaitForExit();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to get process map: {ex.Message}");
        }

        return map;
    }
#endif
}