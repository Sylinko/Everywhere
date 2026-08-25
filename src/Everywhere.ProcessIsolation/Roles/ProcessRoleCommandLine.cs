using ZLinq;

namespace Everywhere.ProcessIsolation.Roles;

/// <summary>Short-lived controller operations accepted by <c>--hosts-control</c>.</summary>
public enum HostsControlOperation
{
    /// <summary>Start both fixed Host roles at the current integrity level.</summary>
    Start,

    /// <summary>Request bounded shutdown of the running Host roles.</summary>
    Stop,

    /// <summary>Install or repair the Windows Hosts Control task.</summary>
    Install,

    /// <summary>Remove the Windows Hosts Control task.</summary>
    Uninstall
}

/// <summary>
/// Parses early process-role and Hosts-control switches before any
/// application-wide initialization is performed.
/// </summary>
public static class ProcessRoleCommandLine
{
    private const string RoleOption = "--process-role";
    private const string HostsControlOption = "--hosts-control";
    private const string RpcEndpointOption = "--rpc-endpoint";

    /// <summary>
    /// Parses the optional <c>--process-role</c> switch. The default is
    /// <see cref="ProcessRole.Main"/> so existing normal UI invocations retain
    /// their entry path.
    /// </summary>
    public static ProcessRole Parse(IReadOnlyList<string> args)
    {
        if (ContainsOption(args, HostsControlOption))
        {
            throw new ArgumentException("The --process-role and --hosts-control options cannot be combined.", nameof(args));
        }

        ProcessRole? selectedRole = null;
        for (var index = 0; index < args.Count; index++)
        {
            string? value;
            var argument = args[index];
            if (argument.StartsWith(RoleOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = argument[(RoleOption.Length + 1)..];
            }
            else if (string.Equals(argument, RoleOption, StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= args.Count)
                {
                    throw new ArgumentException("The --process-role option requires a value.", nameof(args));
                }

                value = args[index];
            }
            else
            {
                continue;
            }

            if (!TryParseValue(value, out var parsedRole))
            {
                throw new ArgumentException("The --process-role value must be main, input, or automation.", nameof(args));
            }

            if (selectedRole is not null)
            {
                throw new ArgumentException("The --process-role option may only be specified once.", nameof(args));
            }

            selectedRole = parsedRole;
        }

        return selectedRole ?? ProcessRole.Main;
    }

    /// <summary>
    /// Parses the short-lived Hosts controller command. A null result means the
    /// command-line does not contain <c>--hosts-control</c>.
    /// </summary>
    public static HostsControlOperation? ParseHostsControl(IReadOnlyList<string> args)
    {
        var optionIndex = -1;
        var inlineValue = default(string);
        var separateValue = false;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.StartsWith(HostsControlOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                if (optionIndex >= 0)
                {
                    throw new ArgumentException("The --hosts-control option may only be specified once.", nameof(args));
                }

                optionIndex = index;
                inlineValue = argument[(HostsControlOption.Length + 1)..];
                continue;
            }

            if (!string.Equals(argument, HostsControlOption, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (optionIndex >= 0)
            {
                throw new ArgumentException("The --hosts-control option may only be specified once.", nameof(args));
            }

            optionIndex = index;
            separateValue = true;
            if (++index >= args.Count)
            {
                throw new ArgumentException("The --hosts-control option requires a value.", nameof(args));
            }

            inlineValue = args[index];
        }

        if (optionIndex < 0)
        {
            return null;
        }

        if (ContainsOption(args, RoleOption))
        {
            throw new ArgumentException("The --process-role and --hosts-control options cannot be combined.", nameof(args));
        }

        for (var index = 0; index < args.Count; index++)
        {
            if (index == optionIndex || (separateValue && index == optionIndex + 1))
            {
                continue;
            }

            throw new ArgumentException("The --hosts-control command does not accept additional arguments.", nameof(args));
        }

        var operationValue = inlineValue ?? throw new ArgumentException("The --hosts-control option requires a value.", nameof(args));
        return operationValue.Trim().ToLowerInvariant() switch
        {
            "start" => HostsControlOperation.Start,
            "stop" => HostsControlOperation.Stop,
            "install" => HostsControlOperation.Install,
            "uninstall" => HostsControlOperation.Uninstall,
            _ => throw new ArgumentException("The --hosts-control value must be start, stop, install, or uninstall.", nameof(args))
        };
    }

    /// <summary>
    /// Validates the complete command line accepted by a Host role and returns
    /// its optional diagnostic endpoint override. Normal UI arguments are not
    /// accepted here: a Host must fail before initialization when any unknown,
    /// duplicate, missing, or role-mismatched option is present.
    /// </summary>
    public static string? ParseHostEndpointOverride(ProcessRole role, IReadOnlyList<string> args)
    {
        if (role is ProcessRole.Main)
        {
            throw new ArgumentException("The main role does not have a Host invocation.", nameof(role));
        }

        if (ContainsOption(args, RoleOption) && Parse(args) != role)
        {
            throw new ArgumentException("The process-role option does not match the selected Host role.", nameof(args));
        }

        string? endpoint = null;
        var endpointSeen = false;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.StartsWith(RoleOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(argument, RoleOption, StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            string? configuredEndpoint;
            if (argument.StartsWith(RpcEndpointOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                configuredEndpoint = argument[(RpcEndpointOption.Length + 1)..];
            }
            else if (string.Equals(argument, RpcEndpointOption, StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= args.Count)
                {
                    throw new ArgumentException("The --rpc-endpoint option requires a value.", nameof(args));
                }

                configuredEndpoint = args[index];
            }
            else
            {
                throw new ArgumentException($"The Host invocation contains an unsupported argument: {argument}", nameof(args));
            }

            if (endpointSeen)
            {
                throw new ArgumentException("The --rpc-endpoint option may only be specified once.", nameof(args));
            }

            if (string.IsNullOrWhiteSpace(configuredEndpoint))
            {
                throw new ArgumentException("The --rpc-endpoint option requires a non-empty value.", nameof(args));
            }

            endpointSeen = true;
            endpoint = configuredEndpoint;
        }

        return endpoint;
    }

    private static bool ContainsOption(IReadOnlyList<string> args, string option) =>
        args.AsValueEnumerable().Any(argument =>
            argument.Equals(option, StringComparison.OrdinalIgnoreCase) || argument.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase));

    private static bool TryParseValue(string value, out ProcessRole role)
    {
        var normalizedValue = value.Trim();
        if (normalizedValue.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            role = ProcessRole.Main;
            return true;
        }

        if (normalizedValue.Equals("input", StringComparison.OrdinalIgnoreCase))
        {
            role = ProcessRole.Input;
            return true;
        }

        if (normalizedValue.Equals("automation", StringComparison.OrdinalIgnoreCase))
        {
            role = ProcessRole.Automation;
            return true;
        }

        role = ProcessRole.Main;
        return false;
    }
}