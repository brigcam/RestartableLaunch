using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace RestartableLaunch.Cli;

internal static class Program
{
    private const string PipeName = "RestartableLaunch.Manager";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions PipeJsonOptions = new();

    private static int Main(string[] args)
    {
        var command = args.Length == 0 ? "list" : args[0].ToLowerInvariant();

        return command switch
        {
            "list" or "-l" or "--list" => PrintList(),
            "gui" or "--gui" => OpenGui(),
            "help" or "-h" or "--help" => PrintHelp(),
            _ => PrintUnknown(command),
        };
    }

    private static int PrintList()
    {
        var response = TryRequestListFromManager();
        Console.WriteLine(string.IsNullOrWhiteSpace(response) ? ReadActiveStateList() : response);
        return 0;
    }

    private static int OpenGui()
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "RestartableLaunch.exe");
        if (!File.Exists(appPath))
        {
            Console.Error.WriteLine($"RestartableLaunch.exe not found next to CLI: {appPath}");
            return 1;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = appPath,
            UseShellExecute = true,
        });
        return 0;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
RestartableLaunchCLI

Commands:
  list        Print monitored applications
  gui         Open the RestartableLaunch tray GUI
  help        Show this help
""");
        return 0;
    }

    private static int PrintUnknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Run RestartableLaunchCLI help for usage.");
        return 2;
    }

    private static string? TryRequestListFromManager()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(750);
            using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(new AppCommand(null, false, CommandMode.List), PipeJsonOptions));
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private static string ReadActiveStateList()
    {
        var activeStatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RestartableLaunch",
            "active.json");

        if (!File.Exists(activeStatePath))
        {
            return "No applications are currently monitored by RestartableLaunch.";
        }

        try
        {
            var state = JsonSerializer.Deserialize<ActiveState>(File.ReadAllText(activeStatePath), JsonOptions);
            if (state is null || state.Apps.Length == 0)
            {
                return "No applications are currently monitored by RestartableLaunch.";
            }

            var liveApps = state.Apps.Where(static app => IsProcessAlive(app.ProcessId)).ToArray();
            if (liveApps.Length == 0)
            {
                return "No applications are currently monitored by RestartableLaunch.";
            }

            var builder = new StringBuilder();
            builder.AppendLine("Applications monitored by RestartableLaunch:");
            builder.AppendLine();

            foreach (var app in liveApps)
            {
                builder.AppendLine($"PID:     {app.ProcessId}");
                builder.AppendLine($"Started: {app.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
                builder.AppendLine($"Command: {FormatRequest(app.Request)}");
                builder.AppendLine($"Desktop: {(app.DesktopId is null ? "unknown" : app.DesktopId)}");
                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Could not read active state: {ex.Message}";
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatRequest(LaunchRequest request)
    {
        return request.Kind == LaunchKind.DefaultOpen
            ? $"default-open {QuoteArgument(request.Executable)}"
            : string.Join(' ', new[] { request.Executable }.Concat(request.Arguments).Select(QuoteArgument));
    }

    private static string QuoteArgument(string arg)
    {
        if (arg.Length > 0 && !arg.Any(char.IsWhiteSpace) && !arg.Contains('"'))
        {
            return arg;
        }

        var builder = new StringBuilder();
        builder.Append('"');

        var backslashes = 0;
        foreach (var character in arg)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            builder.Append(character);
            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private sealed record AppCommand(LaunchRequest? Launch, bool Restore, CommandMode Mode);

    private enum CommandMode
    {
        Launch,
        ShowGui,
        List,
    }

    private enum LaunchKind
    {
        Executable,
        DefaultOpen,
    }

    private sealed record LaunchRequest(string Executable, string[] Arguments, Guid? DesktopId, LaunchKind Kind, WindowBounds? WindowBounds);

    private sealed record ActiveState(ActiveAppState[] Apps);

    private sealed record ActiveAppState(int ProcessId, DateTimeOffset StartedAt, LaunchRequest Request, Guid? DesktopId, WindowBounds? WindowBounds);

    private sealed record WindowBounds(int Left, int Top, int Width, int Height);
}
