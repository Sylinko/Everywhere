using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using Everywhere.AI;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Extensions;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Everywhere.Mac.Chat.Plugin;

public sealed class SystemPlugin : BuiltInChatPlugin
{
    public override IDynamicLocaleKey HeaderKey { get; } = new DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_Header);

    public override IDynamicLocaleKey DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_Description);

    public override LucideIconKind? Icon => LucideIconKind.AppWindowMac;

    private readonly ILogger<SystemPlugin> _logger;

    public SystemPlugin(ILogger<SystemPlugin> logger) : base("system")
    {
        _logger = logger;

        _functionsSource.Edit(list =>
        {
            list.Add(
                new BuiltInChatFunction(
                    ManageRemindersAsync,
                    ChatFunctionPermissions.None));
            list.Add(
                new BuiltInChatFunction(
                    ManageCalendarAsync,
                    ChatFunctionPermissions.None));
            list.Add(
                new BuiltInChatFunction(
                    SendEmailAsync,
                    ChatFunctionPermissions.None));
            list.Add(
                new BuiltInChatFunction(
                    OpenMapsAsync,
                    ChatFunctionPermissions.None));
            list.Add(
                new BuiltInChatFunction(
                    ManageNotesAsync,
                    ChatFunctionPermissions.None));
            list.Add(
                new BuiltInChatFunction(
                    OpenUrlAsync,
                    ChatFunctionPermissions.NetworkAccess));
            list.Add(
                new BuiltInChatFunction(
                    ExecuteAppleScriptAsync,
                    ChatFunctionPermissions.ShellExecute));
        });
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SystemAction
    {
        Create,
        List,
        Update,
        Delete,
        Complete
    }

    private static DynamicLocaleKey GetActionResourceKey(SystemAction action) => action switch
    {
        SystemAction.Create => new DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_Action_Create),
        SystemAction.List => new DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_Action_List),
        SystemAction.Update => new DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_Action_Update),
        SystemAction.Delete => new DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_Action_Delete),
        SystemAction.Complete => new DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_Action_Complete),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
    };

    [KernelFunction("manage_reminders")]
    [Description("Manage reminders: create, list, update, delete, or complete.")]
    [DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_ManageReminders_Header)]
    private async Task<string> ManageRemindersAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [Description("Action: Create, List, Update, Delete, Complete")] SystemAction action,
        [Description("Title (required for create)")] string? title,
        [Description("Notes (optional)")] string? notes,
        [Description("Due date (optional)")] DateTime? dueDate,
        [Description("Reminder ID (required for update/delete/complete)")] string? id,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Managing reminder: {Action}, Title: {Title}, ID: {ID}", action, title, id);

        if (action is not SystemAction.Create and not SystemAction.List && id.IsNullOrWhiteSpace())
        {
            throw new ArgumentException($"ID is required for action {action}.");
        }

        var detailBlock = new ChatPluginContainerDisplayBlock
        {
            new ChatPluginDynamicLocaleKeyDisplayBlock(
                new FormattedDynamicLocaleKey(
                    LocaleKey.MacOS_BuiltInChatPlugin_System_Action,
                    GetActionResourceKey(action))),
        };

        // Get title for display
        if (action is SystemAction.Delete or SystemAction.Update or SystemAction.Complete)
        {
            try
            {
                title = (await RunAppleScriptAsync(
                    $"""
                     tell application "Reminders"
                         return name of (first reminder whose id is "{id}")
                     end tell
                     """,
                    cancellationToken)).Trim();
            }
            catch
            {
                // Ignore errors, just don't show title
            }
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            detailBlock.Add(
                new ChatPluginDynamicLocaleKeyDisplayBlock(
                    new FormattedDynamicLocaleKey(
                        LocaleKey.MacOS_BuiltInChatPlugin_System_ManageReminders_Detail_Title,
                        new DirectLocaleKey(title))));
        }

        // Only show consent for actions that modify data
        if (action is SystemAction.Delete or SystemAction.Update or SystemAction.Complete)
        {
            var consent = await userInterface.RequestConsentAsync(
                action.ToString(),
                new DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_ManageReminders_Consent_Header),
                detailBlock,
                cancellationToken: cancellationToken);

            if (!consent)
            {
                throw new HandledException(
                    new UnauthorizedAccessException(consent.FormatReason("User denied consent for managing reminders.")),
                    new DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_ManageReminders_DenyMessage),
                    showDetails: false);
            }
        }

        displaySink.AppendBlocks(detailBlock);

        FormattableString script;
        switch (action)
        {
            case SystemAction.Create:
            {
                script = $$"""
                           tell application "Reminders"
                               set newReminder to make new reminder with properties {name:"{{title}}"}
                               {{(!notes.IsNullOrWhiteSpace() ? $"set body of newReminder to \"{notes}\"" : "")}}
                               {{(dueDate.HasValue ? $"set due date of newReminder to date \"{dueDate.Value:G}\"" : "")}}
                               return id of newReminder
                           end tell
                           """;
                break;
            }
            case SystemAction.List:
            {
                script = $"""
                          tell application "Reminders"
                              set output to ""
                              set remList to every reminder of default list whose completed is false
                              repeat with r in remList
                                  set output to output & "ID: " & id of r & "|Title: " & name of r & "|Due: " & (get due date of r) & "\n"
                              end repeat
                              return output
                          end tell
                          """;
                break;
            }
            case SystemAction.Delete:
            {
                script = $"""
                          tell application "Reminders"
                              delete (first reminder whose id is "{id}")
                              return "Deleted"
                          end tell
                          """;
                break;
            }
            case SystemAction.Complete:
            {
                script = $"""
                          tell application "Reminders"
                              set completed of (first reminder whose id is "{id}") to true
                              return "Completed"
                          end tell
                          """;
                break;
            }
            case SystemAction.Update:
            {
                script = $"""
                          tell application "Reminders"
                              set targetReminder to (first reminder whose id is "{id}")
                              {(!title.IsNullOrWhiteSpace() ? $"set name of targetReminder to \"{title}\"" : "")}
                              {(!notes.IsNullOrWhiteSpace() ? $"set body of targetReminder to \"{notes}\"" : "")}
                              {(dueDate.HasValue ? $"set due date of targetReminder to date \"{dueDate.Value:G}\"" : "")}
                              return "Updated"
                          end tell
                          """;
                break;
            }
            default:
                throw new ArgumentException($"Unknown action: {action}");
        }

        var remindersResult = await RunAppleScriptAsync(script, cancellationToken);
        return action == SystemAction.List
            ? TokenHelper.Omit(remindersResult, maxTokenCount: 40000)
            : remindersResult;
    }

    [KernelFunction("manage_calendar")]
    [Description(
        "Manage calendar events: create, list, or delete. When listing, you can specify a date range to filter events. Default is the next 2 weeks. Maximum range is 31 days.")]
    [DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_ManageCalendar_Header)]
    private async Task<string> ManageCalendarAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [Description("Action: Create, List, Delete")] SystemAction action,
        [Description("Title (required for create)")] string? title,
        [Description("Start date and time (required for create, optional for list)")] DateTime? startDate,
        [Description("End date and time (required for create, optional for list)")] DateTime? endDate,
        [Description("Location (optional)")] string? location,
        [Description("Notes (optional)")] string? notes,
        [Description("Event ID (required for delete)")] string? id,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Managing calendar event: {Action}, Title: {Title}, ID: {ID}", action, title, id);

        if (action is not (SystemAction.Create or SystemAction.List or SystemAction.Delete))
        {
            throw new ArgumentException($"Action {action} is not supported for Calendar.");
        }

        // Validate required parameters
        switch (action)
        {
            case SystemAction.Delete when id.IsNullOrWhiteSpace():
                throw new ArgumentException("ID is required for delete action.");
            case SystemAction.Create when title.IsNullOrWhiteSpace() || !startDate.HasValue || !endDate.HasValue:
                throw new ArgumentException("Title, StartDate, and EndDate are required for create action.");
        }

        var detailBlock = new ChatPluginContainerDisplayBlock
        {
            new ChatPluginDynamicLocaleKeyDisplayBlock(
                new FormattedDynamicLocaleKey(
                    LocaleKey.MacOS_BuiltInChatPlugin_System_Action,
                    GetActionResourceKey(action))),
        };

        // Get title for display
        if (action is SystemAction.Delete)
        {
            try
            {
                title = (await RunAppleScriptAsync(
                    $"""
                     tell application "Calendar"
                         repeat with c in calendars
                             tell c
                                 try
                                     return summary of (first event whose uid is "{id}")
                                 end try
                             end tell
                         end repeat
                         return ""
                     end tell
                     """,
                    cancellationToken)).Trim();
            }
            catch
            {
                // Ignore errors, just don't show title
            }
        }

        if (!title.IsNullOrWhiteSpace())
        {
            detailBlock.Add(
                new ChatPluginDynamicLocaleKeyDisplayBlock(
                    new FormattedDynamicLocaleKey(
                        LocaleKey.MacOS_BuiltInChatPlugin_System_ManageCalendar_Detail_Title,
                        new DirectLocaleKey(title))));
        }

        if (startDate.HasValue && endDate.HasValue)
        {
            detailBlock.Add(
                new ChatPluginDynamicLocaleKeyDisplayBlock(
                    new FormattedDynamicLocaleKey(
                        LocaleKey.MacOS_BuiltInChatPlugin_System_ManageCalendar_Detail_Time,
                        new DirectLocaleKey($"{startDate:f} - {endDate:f}"))));
        }

        if (!location.IsNullOrWhiteSpace())
        {
            detailBlock.Add(
                new ChatPluginDynamicLocaleKeyDisplayBlock(
                    new FormattedDynamicLocaleKey(
                        LocaleKey.MacOS_BuiltInChatPlugin_System_ManageCalendar_Detail_Location,
                        new DirectLocaleKey(location))));
        }

        // Only show consent for actions that modify data
        if (action is SystemAction.Delete)
        {
            var consent = await userInterface.RequestConsentAsync(
                action.ToString(),
                new DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_ManageCalendar_Consent_Header),
                detailBlock,
                cancellationToken: cancellationToken);

            if (!consent)
            {
                throw new HandledException(
                    new UnauthorizedAccessException(consent.FormatReason("User denied consent for managing calendar.")),
                    new DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_ManageCalendar_DenyMessage),
                    showDetails: false);
            }
        }

        displaySink.AppendBlocks(detailBlock);

        FormattableString script;
        switch (action)
        {
            case SystemAction.Create:
            {
                script =
                    $$"""
                      tell application "Calendar"
                          if exists calendar "Calendar" then
                              set targetCalendar to calendar "Calendar"
                          else
                              set targetCalendar to first calendar whose writable is true
                          end if
                          tell targetCalendar
                              set newEvent to make new event at end with properties {summary:"{{title}}", start date:date "{{startDate.GetValueOrDefault():D}}", end date:date "{{endDate.GetValueOrDefault():D}}"}
                              {{(!location.IsNullOrWhiteSpace() ? $"set location of newEvent to \"{location}\"" : "")}}
                              {{(!notes.IsNullOrWhiteSpace() ? $"set description of newEvent to \"{notes}\"" : "")}}
                              return uid of newEvent
                          end tell
                      end tell
                      """;
                break;
            }
            case SystemAction.List:
            {
                var effectiveStart = startDate ?? DateTime.Now.Date;
                var effectiveEnd = endDate ?? effectiveStart.AddDays(14);

                // Limit the query range to at most 31 days to prevent performance issues
                if ((effectiveEnd - effectiveStart).TotalDays > 31)
                {
                    effectiveEnd = effectiveStart.AddDays(31);
                }

                script = $"""
                          tell application "Calendar"
                              set output to ""
                              set searchStart to date "{effectiveStart:G}"
                              set searchEnd to date "{effectiveEnd:G}"
                              repeat with c in calendars
                                  tell c
                                      set eventList to (every event whose start date is greater than or equal to searchStart and start date is less than or equal to searchEnd)
                                      repeat with e in eventList
                                          set output to output & "ID: " & uid of e & "|Title: " & summary of e & "|Start: " & (start date of e) & "\n"
                                      end repeat
                                  end tell
                              end repeat
                              return output
                          end tell
                          """;
                break;
            }
            case SystemAction.Delete:
            {
                script = $"""
                          tell application "Calendar"
                              repeat with c in calendars
                                  tell c
                                      try
                                          delete (first event whose uid is "{id}")
                                          return "Deleted"
                                      end try
                                  end tell
                              end repeat
                              return "Event not found"
                          end tell
                          """;
                break;
            }
            default:
                throw new ArgumentException($"Unknown action: {action}");
        }

        var calendarResult = await RunAppleScriptAsync(script, cancellationToken);
        return action == SystemAction.List
            ? TokenHelper.Omit(calendarResult, maxTokenCount: 40000)
            : calendarResult;
    }

    [KernelFunction("send_email")]
    [Description("Compose a new email in the Mail app.")]
    [DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_SendEmail_Header)]
    private async Task<string> SendEmailAsync(
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [Description("The recipient email address")] string recipient,
        [Description("The subject of the email")] string subject,
        [Description("The content of the email")] string content,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Sending email to: {Recipient}", recipient);

        var detailBlock = new ChatPluginContainerDisplayBlock
        {
            new ChatPluginDynamicLocaleKeyDisplayBlock(
                new FormattedDynamicLocaleKey(
                    LocaleKey.MacOS_BuiltInChatPlugin_System_SendEmail_Detail_Recipient,
                    new DirectLocaleKey(recipient))),
            new ChatPluginDynamicLocaleKeyDisplayBlock(
                new FormattedDynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_SendEmail_Detail_Subject, new DirectLocaleKey(subject))),
        };

        // Don't show consent because it already shows before calling this function
        displaySink.AppendBlocks(detailBlock);

        return await RunAppleScriptAsync(
            $$"""
              tell application "Mail"
                  set newMessage to make new outgoing message with properties {subject:"{{subject}}", content:"{{content}}", visible:true}
                  tell newMessage
                      make new to recipient at end of to recipients with properties {address:"{{recipient}}"}
                  end tell
                  activate
              end tell
              """,
            cancellationToken);
    }

    [KernelFunction("open_maps")]
    [Description("Open Apple Maps and search for a location.")]
    [DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_OpenMaps_Header)]
    private async Task<string> OpenMapsAsync(
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [Description("The location or query to search for")] string query,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Opening Maps for: {Query}", query);

        var detailBlock = new ChatPluginContainerDisplayBlock
        {
            new ChatPluginDynamicLocaleKeyDisplayBlock(
                new FormattedDynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_OpenMaps_Detail_Query, new DirectLocaleKey(query))),
        };
        displaySink.AppendBlocks(detailBlock);

        return await RunAppleScriptAsync(
            $"""
             tell application "Maps"
                 activate
                 search {query}"
             end tell
             """,
            cancellationToken);
    }

    [KernelFunction("manage_notes")]
    [Description("Manage notes: create, list, or delete.")]
    [DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_ManageNotes_Header)]
    private async Task<string> ManageNotesAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [Description("Action: Create, List, Delete")] SystemAction action,
        [Description("Title (required for create)")] string? title,
        [Description("Content (required for create)")] string? content,
        [Description("Note ID (required for delete)")] string? id,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Managing note: {Action}, Title: {Title}, ID: {ID}", action, title, id);

        if (action is not (SystemAction.Create or SystemAction.List or SystemAction.Delete))
        {
            throw new ArgumentException($"Action {action} is not supported for Notes.");
        }

        switch (action)
        {
            case SystemAction.Delete when id.IsNullOrWhiteSpace():
                throw new ArgumentException("ID is required for delete action.");
            case SystemAction.Create when string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content):
                throw new ArgumentException("Title and Content are required for create action.");
        }

        var detailBlock = new ChatPluginContainerDisplayBlock
        {
            new ChatPluginDynamicLocaleKeyDisplayBlock(
                new FormattedDynamicLocaleKey(
                    LocaleKey.MacOS_BuiltInChatPlugin_System_Action,
                    GetActionResourceKey(action))),
        };

        if (action is SystemAction.Delete)
        {
            try
            {
                FormattableString actionScript = $"""
                                                  tell application "Notes"
                                                      return name of (first note whose id is "{id}")
                                                  end tell
                                                  """;
                title = (await RunAppleScriptAsync(
                    actionScript,
                    cancellationToken)).Trim();
            }
            catch
            {
                // Ignore errors, just don't show title
            }
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            detailBlock.Add(
                new ChatPluginDynamicLocaleKeyDisplayBlock(
                    new FormattedDynamicLocaleKey(
                        LocaleKey.MacOS_BuiltInChatPlugin_System_ManageNotes_Detail_Title,
                        new DirectLocaleKey(title))));
        }

        var consent = await userInterface.RequestConsentAsync(
            null,
            new DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_ManageNotes_Consent_Header),
            detailBlock,
            cancellationToken: cancellationToken);

        if (!consent)
        {
            throw new HandledException(
                new UnauthorizedAccessException(consent.FormatReason("User denied consent for managing notes.")),
                new DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_ManageNotes_DenyMessage),
                showDetails: false);
        }

        displaySink.AppendBlocks(detailBlock);

        FormattableString script;
        switch (action)
        {
            case SystemAction.Create:
            {
                script = $$"""
                           tell application "Notes"
                               activate
                               set newNote to make new note with properties {name:"{{title}}", body:"{{content}}"}
                               return id of newNote
                           end tell
                           """;
                break;
            }
            case SystemAction.List:
            {
                script = $"""
                          tell application "Notes"
                              set output to ""
                              set noteList to every note
                              repeat with n in noteList
                                  set output to output & "ID: " & id of n & "|Title: " & name of n & "\n"
                              end repeat
                              return output
                          end tell
                          """;
                break;
            }
            case SystemAction.Delete:
            {
                script = $"""
                          tell application "Notes"
                              delete (first note whose id is "{id}")
                              return "Deleted"
                          end tell
                          """;
                break;
            }
            default:
                throw new ArgumentException($"Unknown action: {action}");
        }

        var notesResult = await RunAppleScriptAsync(script, cancellationToken);
        return action == SystemAction.List
            ? TokenHelper.Omit(notesResult, maxTokenCount: 40000)
            : notesResult;
    }

    [KernelFunction("open_url")]
    [Description("Open a URL in the default browser.")]
    [DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_OpenUrl_Header)]
    private async Task<string> OpenUrlAsync(
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [Description("The http or https URL to open")] string url,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Opening URL: {Url}", url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUri) ||
            (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Invalid or unsupported URL scheme. Only HTTP and HTTPS are allowed");
        }

        var detailBlock = new ChatPluginContainerDisplayBlock
        {
            new ChatPluginDynamicLocaleKeyDisplayBlock(
                new FormattedDynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_OpenUrl_Detail_Url, new DirectLocaleKey(url))),
        };
        displaySink.AppendBlocks(detailBlock);

        return await RunAppleScriptAsync($"open location \"{url}\"", cancellationToken);
    }

    [KernelFunction("execute_applescript")]
    [Description("Execute raw AppleScript. Use this only when other specific functions are not applicable.")]
    [DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_ExecuteScript_Header)]
    private async Task<string> ExecuteAppleScriptAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [Description("A concise description for user, explaining what you are doing")] string description,
        [Description("The AppleScript code")] string script,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Executing AppleScript with description: {Description}", description);

        if (string.IsNullOrWhiteSpace(script))
        {
            throw new ArgumentException("Script cannot be null or empty.", nameof(script));
        }

        var detailBlock = new ChatPluginContainerDisplayBlock
        {
            new ChatPluginTextDisplayBlock(description),
            new ChatPluginCodeBlockDisplayBlock(script, "applescript"),
        };

        var consent = await userInterface.RequestConsentAsync(
            null,
            new DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_ExecuteScript_ScriptConsent_Header),
            detailBlock,
            cancellationToken: cancellationToken);
        if (!consent)
        {
            throw new HandledException(
                new UnauthorizedAccessException(consent.FormatReason("User denied consent for AppleScript execution.")),
                new DynamicLocaleKey(LocaleKey.MacOS_BuiltInChatPlugin_System_ExecuteScript_DenyMessage),
                showDetails: false);
        }

        displaySink.AppendBlocks(detailBlock);

        var rawResult = await RunRawAppleScriptAsync(script, cancellationToken);
        return TokenHelper.Omit(rawResult, maxTokenCount: 40000);
    }

    private static async Task<string> RunAppleScriptAsync(FormattableString script, CancellationToken cancellationToken)
    {
        return await RunRawAppleScriptAsync(script.ToString(AppleScriptFormatter.Shared), cancellationToken);
    }

    private static async Task<string> RunRawAppleScriptAsync(string script, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/osascript",
            Arguments = "-",
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            throw new SystemException("Failed to start AppleScript execution process.");
        }

        await process.StandardInput.WriteAsync(script);
        process.StandardInput.Close();

        var result = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorOutput = await process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new HandledException(
                new SystemException($"AppleScript execution failed: {errorOutput}"),
                new FormattedDynamicLocaleKey(
                    LocaleKey.MacOS_BuiltInChatPlugin_System_ExecuteScript_ErrorMessage,
                    new DirectLocaleKey(errorOutput)),
                showDetails: false);
        }

        return result;
    }

    private sealed class AppleScriptFormatter : IFormatProvider, ICustomFormatter
    {
        public static AppleScriptFormatter Shared { get; } = new();

        private AppleScriptFormatter() { }

        public object? GetFormat(Type? formatType) => formatType == typeof(ICustomFormatter) ? this : null;

        public string Format(string? format, object? arg, IFormatProvider? formatProvider)
        {
            return arg switch
            {
                null => string.Empty,
                RawScript rs => rs.Value,
                FormattableString fs => fs.ToString(this),
                string s => s.Replace("\\", "\\\\").Replace("\"", "\\\""),
                IFormattable formattable => formattable.ToString(format, CultureInfo.InvariantCulture),
                _ => arg.ToString() ?? string.Empty
            };
        }
    }

    private readonly struct RawScript(string value)
    {
        public string Value { get; } = value;
        public override string ToString() => Value;
    }
}