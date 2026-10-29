using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Input;
using Everywhere.Linux.Desktop;
using Everywhere.Linux.Hyprland;
using Everywhere.Linux.ProcessIsolation;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Hosts.Input;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.Linux.Platform;

/// <summary>
/// Owns session-scoped compositor bindings and enables only its current desired shortcuts.
/// </summary>
public sealed class HyprlandInputHostSession : IProcessRoleSession, IInputHostRpc
{
    private readonly HyprlandConnection _compositor = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _pipeName = "everywhere-hyprland-input-" + Guid.NewGuid().ToString("N");
    private readonly string _bindingsName = "everywhere_" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Environment.ProcessPath ?? "Everywhere")))[..16];
    private readonly Lock _gate = new();
    private HashSet<ulong> _registrations = [];
    private Task _listener = Task.CompletedTask;
    private bool _isDraining;

    /// <inheritdoc />
    public void Bind(RpcConnection connection)
    {
        InputHostRpcBinding.Bind(connection, this);
        _listener = ListenAsync(new InputHostNotificationRpcClient(connection));
    }

    /// <inheritdoc />
    public void OnAuthenticated(RpcHandshake peer)
    {
    }

    /// <inheritdoc />
    public ValueTask<ApplyInputStateResponse> ApplyStateAsync(
        ApplyInputStateRequest request,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Recording is focused in Main's input box on Wayland. The capture lease
            // suspends only our global bindings so the same chord can reach that window.
            // Passive global mouse observation still requires a compositor capability.
            if (_isDraining || request.MouseRegistrations.Length != 0)
            {
                return ValueTask.FromResult(new ApplyInputStateResponse
                {
                    IsApplied = false
                });
            }

            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Application executable is unavailable.");
            var activeRegistrations = request.CaptureId == 0 ? request.KeyboardRegistrations : [];
            var commands = activeRegistrations.Select(item =>
            {
                var key = LinuxKeySymbol.FromKey((Key)item.Key);
                var modifiers = (KeyModifiers)item.Modifiers;
                var parts = new List<string>();
                if (modifiers.HasFlag(KeyModifiers.Control))
                {
                    parts.Add("CTRL");
                }

                if (modifiers.HasFlag(KeyModifiers.Alt))
                {
                    parts.Add("ALT");
                }

                if (modifiers.HasFlag(KeyModifiers.Shift))
                {
                    parts.Add("SHIFT");
                }

                if (modifiers.HasFlag(KeyModifiers.Meta))
                {
                    parts.Add("SUPER");
                }
                parts.Add(key);
                var command = ShellQuote(executable) + " --hyprland-shortcut " + ShellQuote(_pipeName) + " " +
                    item.RegistrationId.ToString(CultureInfo.InvariantCulture);
                return $"[{LuaQuote(string.Join(" + ", parts))}]={LuaQuote(command)}";
            }).ToArray();
            // Hyprland's remove() removes every binding with the same chord, including other
            // owners. Keep one handle per chord for this installation and reuse its callback
            // across Host recovery; disabling a handle affects only that exact binding.
            var script = $$"""
                local state = _G.{{_bindingsName}}
                if not state then
                    state = { bindings = {}, commands = {} }
                    _G.{{_bindingsName}} = state
                end
                local desired = { {{string.Join(",", commands)}} }
                local ok, err = pcall(function()
                    for key in pairs(desired) do
                        if not state.bindings[key] then
                            local chord = key
                            local binding = hl.bind(chord, function()
                                local command = state.commands[chord]
                                if command then
                                    hl.dispatch(hl.dsp.exec_cmd(command))
                                end
                            end)
                            state.bindings[chord] = binding
                            binding:set_enabled(false)
                        end
                    end
                end)
                if not ok then
                    error(err)
                end
                state.commands = desired
                for key, binding in pairs(state.bindings) do
                    binding:set_enabled(desired[key] ~= nil)
                end
                """;
            _compositor.ExecuteLua(script);
            _registrations = activeRegistrations.Select(item => item.RegistrationId).ToHashSet();
            return ValueTask.FromResult(new ApplyInputStateResponse
            {
                IsApplied = true
            });
        }
    }

    /// <inheritdoc />
    public ValueTask BeginDrainingAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_isDraining)
            {
                return ValueTask.CompletedTask;
            }

            _isDraining = true;
            _lifetime.Cancel();
            _registrations.Clear();
            _compositor.ExecuteLua($$"""
                local state = _G.{{_bindingsName}}
                if state then
                    state.commands = {}
                    for _, binding in pairs(state.bindings) do
                        binding:set_enabled(false)
                    end
                end
                """);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await BeginDrainingAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Cancel();
            try
            {
                await _listener.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _lifetime.Dispose();
        }
    }

    /// <summary>
    /// Relays a compositor shortcut from the same executable into the authenticated Input Host.
    /// </summary>
    public static async Task RelayAsync(string pipeName, string registration)
    {
        if (!pipeName.StartsWith("everywhere-hyprland-input-", StringComparison.Ordinal) || !ulong.TryParse(registration, out _))
        {
            throw new ArgumentException("Invalid shortcut relay.");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        new LinuxNamedPipePeerVerifier().VerifyServer(pipe);
        await using var writer = new StreamWriter(pipe);
        await writer.WriteLineAsync(registration.AsMemory(), timeout.Token).ConfigureAwait(false);
    }

    private async Task ListenAsync(InputHostNotificationRpcClient notifications)
    {
        var sequence = 0UL;
        while (!_lifetime.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                new LinuxNamedPipePeerVerifier().VerifyClient(pipe);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(1));
                var bytes = new byte[32];
                var count = 0;
                while (count < bytes.Length && (count == 0 || bytes[count - 1] != '\n'))
                {
                    var received = await pipe.ReadAsync(bytes.AsMemory(count, 1), timeout.Token).ConfigureAwait(false);
                    if (received == 0)
                    {
                        break;
                    }
                    count += received;
                }

                if (count == 0 || bytes[count - 1] != '\n')
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(bytes, 0, count).Trim();
                if (!ulong.TryParse(text, out var id))
                {
                    continue;
                }
                lock (_gate)
                {
                    if (_isDraining || !_registrations.Contains(id))
                    {
                        continue;
                    }
                }

                await notifications.ShortcutTriggeredAsync(new ShortcutTriggeredNotification
                {
                    RegistrationId = id,
                    Sequence = ++sequence,
                    UtcTicks = DateTimeOffset.UtcNow.UtcTicks
                }, _lifetime.Token).ConfigureAwait(false);
            }
            catch (RpcProtocolException)
            {
            }
            catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
            {
            }
        }
    }

    private static string LuaQuote(string value)
    {
        // Lua accepts decimal byte escapes, not JSON's Unicode escapes. Keep UTF-8 text
        // literal and use three digits so a following digit cannot extend an escape.
        var result = new StringBuilder("\"");
        foreach (var character in value)
        {
            if (character is '\\' or '"')
            {
                result.Append('\\').Append(character);
            }
            else if (char.IsControl(character) && character <= 127)
            {
                result.Append('\\').Append(((int)character).ToString("D3", CultureInfo.InvariantCulture));
            }
            else
            {
                result.Append(character);
            }
        }

        return result.Append('"').ToString();
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}