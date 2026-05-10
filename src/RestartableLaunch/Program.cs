using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace RestartableLaunch;

internal static partial class Program
{
    private const string MutexName = @"Local\RestartableLaunch.Manager";
    private const string PipeName = "RestartableLaunch.Manager";
    private const int SwHide = 0;
    private const int WindowWaitTimeoutMilliseconds = 30000;
    private const int WindowWaitPollMilliseconds = 250;
    private const int RestoreLaunchWindowWaitMilliseconds = 12000;
    private const int RestoreLaunchSpacingMilliseconds = 1500;
    private const int RestorePlacementProtectionMilliseconds = 45000;
    private const int WindowPlacementTolerancePixels = 16;
    private const string StartupRunName = "RestartableLaunch";
    private const int WmGetIcon = 0x007F;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int IconSmall2 = 2;
    private const int GclpHIcon = -14;
    private const int GclpHIconSmall = -34;
    private const int SwShownormal = 1;
    private const int SwShowminimized = 2;
    private const int SwShowmaximized = 3;
    private const int SwRestore = 9;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions PipeJsonOptions = new();
    private static readonly Icon AppIcon = LoadAppIcon();
    private static readonly object LogLock = new();
    private static readonly int[] RestorePlacementRetryDelaysMilliseconds = [0, 250, 750, 1500, 3000, 5000];

    [STAThread]
    private static int Main(string[] args)
    {
        InstallExceptionLogging();
        ApplicationConfiguration.Initialize();
        LogMessage("startup", $"RestartableLaunch starting. Args: {string.Join(" ", args)}");

        var command = AppCommand.Parse(args);
        using var mutex = new Mutex(false, MutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            var response = SendToExistingInstance(command);
            return 0;
        }

        HideConsoleWindow();

        var context = new ManagerContext();
        _ = StartPipeServer(context);

        if (command.Restore)
        {
            context.RestoreSavedSession();
        }
        else if (command.Launch is not null)
        {
            context.Launch(command.Launch);
        }

        if (command.Mode == CommandMode.ShowGui || (!command.Restore && command.Launch is null))
        {
            context.ShowMainWindow();
        }

        try
        {
            Application.Run(context);
            return 0;
        }
        catch (Exception ex)
        {
            LogException("application-run", ex);
            throw;
        }
    }

    private static void InstallExceptionLogging()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogException("winforms-thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = e.ExceptionObject as Exception;
            LogException("appdomain-unhandled", exception, $"IsTerminating: {e.IsTerminating}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogException("task-unobserved", e.Exception);
            e.SetObserved();
        };
    }

    private static void LogMessage(string source, string message)
    {
        WriteLog(source, message);
    }

    private static void LogException(string source, Exception? exception, string? details = null)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(details))
        {
            builder.AppendLine(details);
        }

        builder.AppendLine(exception?.ToString() ?? "(non-Exception object)");
        WriteLog(source, builder.ToString());
    }

    private static void WriteLog(string source, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            CleanupOldLogs();
            var path = Path.Combine(LogDirectory, $"RestartableLaunch-{DateTime.Now:yyyy-MM-dd}.log");
            var entry = new StringBuilder()
                .AppendLine($"[{DateTimeOffset.Now:O}] {source}")
                .AppendLine(message.TrimEnd())
                .AppendLine()
                .ToString();

            lock (LogLock)
            {
                File.AppendAllText(path, entry, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never become another source of crashes.
        }
    }

    private static void CleanupOldLogs()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(LogDirectory, "RestartableLaunch-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-21))
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static async Task StartPipeServer(ManagerContext context)
    {
        while (!context.IsExiting)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync();
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var json = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(json))
                {
                    continue;
                }

                var command = JsonSerializer.Deserialize<AppCommand>(json, PipeJsonOptions);
                if (command is null)
                {
                    continue;
                }

                if (command.Mode == CommandMode.List)
                {
                    using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
                    writer.Write(FormatApps(context.Apps));
                    continue;
                }

                context.Post(() =>
                {
                    if (command.Restore)
                    {
                        context.RestoreSavedSession();
                    }
                    else if (command.Launch is not null)
                    {
                        context.Launch(command.Launch);
                    }

                    if (command.Mode == CommandMode.ShowGui || (!command.Restore && command.Launch is null))
                    {
                        context.ShowMainWindow();
                    }
                });
            }
            catch
            {
                await Task.Delay(500);
            }
        }
    }

    private static string SendToExistingInstance(AppCommand command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(1500);
            using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(command, PipeJsonOptions));

            if (command.Mode != CommandMode.List)
            {
                return string.Empty;
            }

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            if (command.Mode == CommandMode.List)
            {
                return $"RestartableLaunch is already running, but it did not respond: {ex.Message}";
            }

            MessageBox(IntPtr.Zero, $"RestartableLaunch is already running, but it did not respond: {ex.Message}", "RestartableLaunch", 0x10);
            return string.Empty;
        }
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

    private static string FormatCommand(string executable, string[] arguments)
    {
        return string.Join(' ', new[] { executable }.Concat(arguments).Select(QuoteArgument));
    }

    private static string FormatRequest(LaunchRequest request)
    {
        return request.Kind == LaunchKind.DefaultOpen
            ? $"default-open {QuoteArgument(request.Executable)}"
            : FormatCommand(request.Executable, request.Arguments);
    }

    private static string FormatApps(IReadOnlyList<MonitoredApp> apps)
    {
        if (apps.Count == 0)
        {
            return "No applications are currently monitored by RestartableLaunch.";
        }

        var builder = new StringBuilder();
        builder.AppendLine("Applications monitored by RestartableLaunch:");
        builder.AppendLine();

        foreach (var app in apps)
        {
            builder.AppendLine($"PID:     {app.Process.Id}");
            builder.AppendLine($"Started: {app.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"Command: {FormatRequest(app.Request)}");
            builder.AppendLine($"Desktop: {(app.DesktopId is null ? "unknown" : app.DesktopId)}");
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static void HideConsoleWindow()
    {
        var consoleWindow = GetConsoleWindow();
        if (consoleWindow != IntPtr.Zero)
        {
            ShowWindow(consoleWindow, SwHide);
        }
    }

    private static Icon LoadAppIcon()
    {
        var path = Environment.ProcessPath ?? Application.ExecutablePath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is not null)
            {
                return icon;
            }
        }

        return SystemIcons.Application;
    }

    private static Icon CloneAppIcon()
    {
        return (Icon)AppIcon.Clone();
    }

    private static void AddIconImage(ImageList imageList, string key, Icon icon)
    {
        var bitmap = new Bitmap(imageList.ImageSize.Width, imageList.ImageSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.DrawIcon(icon, new Rectangle(Point.Empty, imageList.ImageSize));
        imageList.Images.Add(key, bitmap);
    }

    private static ImageList CreateSmallIconList()
    {
        return new ImageList
        {
            ColorDepth = ColorDepth.Depth32Bit,
            ImageSize = new Size(16, 16),
        };
    }

    private static Icon TryGetProcessIcon(Process process)
    {
        var processName = TryGetProcessName(process);
        var fileName = TryGetProcessExecutablePath(process);
        if (TryExtractAndCacheIcon(fileName, processName, out var icon)
            || TryLoadCachedIcon("path", fileName, out icon)
            || TryLoadCachedIcon("process", processName, out icon))
        {
            return icon;
        }

        return CloneAppIcon();
    }

    private static Icon TryGetRuleIcon(ProcessMonitorRule rule)
    {
        if (TryExtractAndCacheIcon(rule.ExecutablePath, rule.ProcessName, out var icon)
            || TryLoadCachedIcon("path", rule.ExecutablePath, out icon)
            || TryLoadCachedIcon("process", rule.ProcessName, out icon))
        {
            return icon;
        }

        return CloneAppIcon();
    }

    private static bool TryExtractAndCacheIcon(string fileName, string processName, out Icon icon)
    {
        icon = null!;
        try
        {
            if (!string.IsNullOrWhiteSpace(fileName) && File.Exists(fileName))
            {
                var extracted = Icon.ExtractAssociatedIcon(fileName);
                if (extracted is not null)
                {
                    SaveIconToCache("path", fileName, extracted);
                    SaveIconToCache("process", processName, extracted);
                    icon = extracted;
                    return true;
                }
            }
        }
        catch
        {
            // Fall back to the persistent cache.
        }

        return false;
    }

    private static bool TryLoadCachedIcon(string kind, string value, out Icon icon)
    {
        icon = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var path = GetIconCachePath(kind, value);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var cached = new Icon(path);
            icon = (Icon)cached.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SaveIconToCache(string kind, string value, Icon icon)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(IconCacheDirectory);
            using var stream = File.Create(GetIconCachePath(kind, value));
            icon.Save(stream);
        }
        catch
        {
            // Icon cache is opportunistic; UI can always fall back to the app icon.
        }
    }

    private static string GetIconCachePath(string kind, string value)
    {
        var normalized = $"{kind}:{NormalizeIconCacheValue(kind, value)}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return Path.Combine(IconCacheDirectory, $"{kind}-{hash}.ico");
    }

    private static string NormalizeIconCacheValue(string kind, string value)
    {
        if (kind == "path")
        {
            try
            {
                value = Path.GetFullPath(value);
            }
            catch
            {
                // Use the original value below.
            }
        }

        return value.Trim().ToLowerInvariant();
    }

    private static string TryGetProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string TryGetProcessExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<IntPtr> WaitForMainWindowAsync(Process process, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(WindowWaitTimeoutMilliseconds);

        while (!process.HasExited && DateTimeOffset.UtcNow < deadline)
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return process.MainWindowHandle;
            }

            var window = FindTopLevelWindow(process.Id);
            if (window != IntPtr.Zero)
            {
                return window;
            }

            await Task.Delay(WindowWaitPollMilliseconds, cancellationToken);
        }

        return IntPtr.Zero;
    }

    private static async Task<IntPtr> WaitForLaunchWindowAsync(
        MonitoredApp app,
        HashSet<IntPtr> existingWindows,
        Func<int, bool> isAlreadyMonitoredProcess,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var directWindow = app.Process.HasExited ? IntPtr.Zero : FindTopLevelWindow(app.Process.Id);
            if (directWindow != IntPtr.Zero)
            {
                return directWindow;
            }

            var replacementWindow = FindReplacementLaunchWindow(app, existingWindows, isAlreadyMonitoredProcess);
            if (replacementWindow != IntPtr.Zero)
            {
                return replacementWindow;
            }

            if (app.Process.HasExited && DateTimeOffset.Now - app.StartedAt > TimeSpan.FromSeconds(3))
            {
                return IntPtr.Zero;
            }

            await Task.Delay(WindowWaitPollMilliseconds, cancellationToken);
        }

        return IntPtr.Zero;
    }

    private static IntPtr FindReplacementLaunchWindow(MonitoredApp app, HashSet<IntPtr> existingWindows, Func<int, bool> isAlreadyMonitoredProcess)
    {
        var result = IntPtr.Zero;
        var requestedProcessName = Path.GetFileNameWithoutExtension(app.Request.Executable);

        EnumWindows((window, _) =>
        {
            if (existingWindows.Contains(window) || !IsWindowVisible(window))
            {
                return true;
            }

            var title = GetWindowTitle(window);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(window, out var processId);
            if (processId == Environment.ProcessId || isAlreadyMonitoredProcess(processId))
            {
                return true;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.StartTime.ToUniversalTime() < app.StartedAt.UtcDateTime.AddSeconds(-5))
                {
                    return true;
                }

                if (!string.Equals(process.ProcessName, requestedProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                result = window;
                return false;
            }
            catch
            {
                return true;
            }
        }, IntPtr.Zero);

        return result;
    }

    private static IntPtr FindTopLevelWindow(int processId)
    {
        var result = IntPtr.Zero;

        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var windowProcessId);
            if (windowProcessId == processId && IsWindowVisible(window))
            {
                result = window;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        if (result != IntPtr.Zero)
        {
            return result;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            process.Refresh();
            return process.MainWindowHandle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static HashSet<IntPtr> GetTopLevelWindowHandles()
    {
        var shellWindow = GetShellWindow();
        var windows = new HashSet<IntPtr>();

        EnumWindows((window, _) =>
        {
            if (window != shellWindow && IsWindowVisible(window))
            {
                windows.Add(window);
            }

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static List<WindowCandidate> GetOpenWindowCandidates()
    {
        var currentProcessId = Environment.ProcessId;
        var shellWindow = GetShellWindow();
        var candidates = new List<WindowCandidate>();

        EnumWindows((window, _) =>
        {
            if (window == shellWindow || !IsWindowVisible(window))
            {
                return true;
            }

            GetWindowThreadProcessId(window, out var processId);
            if (processId == currentProcessId)
            {
                return true;
            }

            var title = GetWindowTitle(window);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                candidates.Add(new WindowCandidate(window, processId, process.ProcessName, title, GetWindowClass(window)));
            }
            catch
            {
                // Ignore windows whose process disappears while enumerating.
            }

            return true;
        }, IntPtr.Zero);

        return candidates
            .OrderBy(static candidate => candidate.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetWindowTitle(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        GetWindowText(window, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetWindowClass(IntPtr window)
    {
        var builder = new StringBuilder(256);
        return GetClassName(window, builder, builder.Capacity) > 0
            ? builder.ToString()
            : string.Empty;
    }

    private static Icon TryGetWindowIcon(IntPtr window, int processId)
    {
        var iconHandle = SendMessage(window, WmGetIcon, (IntPtr)IconSmall2, IntPtr.Zero);
        if (iconHandle == IntPtr.Zero)
        {
            iconHandle = SendMessage(window, WmGetIcon, (IntPtr)IconSmall, IntPtr.Zero);
        }

        if (iconHandle == IntPtr.Zero)
        {
            iconHandle = SendMessage(window, WmGetIcon, (IntPtr)IconBig, IntPtr.Zero);
        }

        if (iconHandle == IntPtr.Zero)
        {
            iconHandle = GetClassLongPtr(window, GclpHIconSmall);
        }

        if (iconHandle == IntPtr.Zero)
        {
            iconHandle = GetClassLongPtr(window, GclpHIcon);
        }

        if (iconHandle != IntPtr.Zero)
        {
            try
            {
                using var icon = Icon.FromHandle(iconHandle);
                return (Icon)icon.Clone();
            }
            catch
            {
                // Fall back to the executable icon below.
            }
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var fileName = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                var icon = Icon.ExtractAssociatedIcon(fileName);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch
        {
            // Fall back to the app icon when process details are unavailable.
        }

        return CloneAppIcon();
    }

    private static WindowBounds? TryGetWindowBounds(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return null;
        }

        NativeRect rect;
        var state = WindowShowState.Normal;
        var placement = WindowPlacement.Create();
        if (GetWindowPlacement(window, ref placement))
        {
            rect = placement.NormalPosition;
            state = placement.ShowCommand switch
            {
                SwShowmaximized => WindowShowState.Maximized,
                SwShowminimized => WindowShowState.Minimized,
                _ => WindowShowState.Normal,
            };
        }
        else if (!GetWindowRect(window, out rect))
        {
            return null;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        return width > 0 && height > 0
            ? new WindowBounds(rect.Left, rect.Top, width, height, state)
            : null;
    }

    private static void TryMoveWindow(IntPtr window, WindowBounds? bounds)
    {
        if (window == IntPtr.Zero || bounds is null)
        {
            return;
        }

        if (TrySetWindowPlacement(window, bounds))
        {
            return;
        }

        MoveWindow(window, bounds.Left, bounds.Top, bounds.Width, bounds.Height, true);
        ShowWindow(window, GetShowCommand(bounds.State));
    }

    private static bool TrySetWindowPlacement(IntPtr window, WindowBounds bounds)
    {
        var placement = WindowPlacement.Create();
        if (!GetWindowPlacement(window, ref placement))
        {
            return false;
        }

        placement.Flags = 0;
        placement.ShowCommand = GetShowCommand(bounds.State);
        placement.NormalPosition = new NativeRect
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Right = bounds.Left + bounds.Width,
            Bottom = bounds.Top + bounds.Height,
        };

        return SetWindowPlacement(window, ref placement);
    }

    private static int GetShowCommand(WindowShowState state)
    {
        return state switch
        {
            WindowShowState.Maximized => SwShowmaximized,
            WindowShowState.Minimized => SwShowminimized,
            _ => SwShownormal,
        };
    }

    private static void TryBringWindowToFront(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return;
        }

        if (VirtualDesktopPlacement.TryGetDesktopId(window) is { } desktopId)
        {
            VirtualDesktopPlacement.TrySwitchToDesktop(desktopId);
        }

        if (IsIconic(window))
        {
            ShowWindow(window, SwRestore);
        }

        BringWindowToTop(window);
        SetForegroundWindow(window);
    }

    private static LaunchRequest? TryCreateRequestFromProcess(Process process, bool requireCommandLine = false)
    {
        var commandLine = TryGetProcessCommandLine(process.Id);
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return requireCommandLine ? null : TryCreateFallbackRequestFromProcess(process);
        }

        var args = CommandLineToArguments(commandLine);

        if (args.Length > 0)
        {
            return new LaunchRequest(args[0], args.Skip(1).ToArray(), null, LaunchKind.Executable, null);
        }

        return requireCommandLine ? null : TryCreateFallbackRequestFromProcess(process);
    }

    private static LaunchRequest? TryCreateFallbackRequestFromProcess(Process process)
    {
        try
        {
            return new LaunchRequest(process.MainModule?.FileName ?? process.ProcessName, [], null, LaunchKind.Executable, null);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetProcessCommandLine(int processId)
    {
        try
        {
            var wmiType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (wmiType is null)
            {
                return null;
            }

            dynamic locator = Activator.CreateInstance(wmiType)!;
            dynamic service = locator.ConnectServer(".", "root\\cimv2");
            dynamic results = service.ExecQuery($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");

            foreach (dynamic result in results)
            {
                var commandLine = result.CommandLine as string;
                if (!string.IsNullOrWhiteSpace(commandLine))
                {
                    return commandLine;
                }
            }
        }
        catch
        {
            // Fall back below.
        }

        return TryGetProcessCommandLineFromPowerShell(processId);
    }

    private static string? TryGetProcessCommandLineFromPowerShell(int processId)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            process.StartInfo.ArgumentList.Add("Bypass");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add($"(Get-CimInstance Win32_Process -Filter \"ProcessId = {processId}\").CommandLine");

            if (!process.Start())
            {
                return null;
            }

            if (!process.WaitForExit(2500))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort.
                }

                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    private static string[] CommandLineToArguments(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var argc);
        if (argv == IntPtr.Zero || argc <= 0)
        {
            return [];
        }

        try
        {
            var args = new string[argc];
            for (var i = 0; i < argc; i++)
            {
                var pointer = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                args[i] = Marshal.PtrToStringUni(pointer) ?? string.Empty;
            }

            return args;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    private static string AppDataDirectory
    {
        get
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RestartableLaunch");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private static string SessionPath => Path.Combine(AppDataDirectory, "session.json");

    private static string ActiveStatePath => Path.Combine(AppDataDirectory, "active.json");

    private static string IconCacheDirectory => Path.Combine(AppDataDirectory, "icons");

    private static string LogDirectory => Path.Combine(AppDataDirectory, "logs");

    private static class ExplorerContextMenu
    {
        private static readonly string[] ContextMenuSubkeys =
        [
            @"Software\Classes\*\shell\RestartableLaunch",
            @"Software\Classes\Directory\shell\RestartableLaunch",
            @"Software\Classes\Drive\shell\RestartableLaunch",
        ];

        public static bool IsRegistered()
        {
            var expectedCommand = GetCommandValue();

            return ContextMenuSubkeys.All(subkey =>
            {
                using var key = Registry.CurrentUser.OpenSubKey(subkey);
                using var commandKey = Registry.CurrentUser.OpenSubKey(subkey + @"\command");
                return string.Equals(key?.GetValue("") as string, "RestartableLaunch", StringComparison.Ordinal)
                    && string.Equals(key?.GetValue("Icon") as string, QuoteArgument(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(commandKey?.GetValue("") as string, expectedCommand, StringComparison.OrdinalIgnoreCase);
            });
        }

        public static void Register()
        {
            foreach (var subkey in ContextMenuSubkeys)
            {
                using var key = Registry.CurrentUser.CreateSubKey(subkey);
                key.SetValue("", "RestartableLaunch", RegistryValueKind.String);
                key.SetValue("Icon", QuoteArgument(Application.ExecutablePath), RegistryValueKind.String);

                using var commandKey = key.CreateSubKey("command");
                commandKey.SetValue("", GetCommandValue(), RegistryValueKind.String);
            }
        }

        public static void Unregister()
        {
            foreach (var subkey in ContextMenuSubkeys)
            {
                Registry.CurrentUser.DeleteSubKeyTree(subkey, throwOnMissingSubKey: false);
            }
        }

        private static string GetCommandValue()
        {
            return $"{QuoteArgument(Application.ExecutablePath)} \"%1\"";
        }
    }

    private static class LoginStartup
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool IsRegistered()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return string.Equals(key?.GetValue(StartupRunName) as string, GetCommandValue(), StringComparison.OrdinalIgnoreCase);
        }

        public static void Register()
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key.SetValue(StartupRunName, GetCommandValue(), RegistryValueKind.String);
        }

        public static void Unregister()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(StartupRunName, throwOnMissingValue: false);
        }

        private static string GetCommandValue()
        {
            return $"{QuoteArgument(Application.ExecutablePath)} --restore";
        }
    }

    private static string ReadActiveStateList()
    {
        if (!File.Exists(ActiveStatePath))
        {
            return "No applications are currently monitored by RestartableLaunch.";
        }

        try
        {
            var state = JsonSerializer.Deserialize<ActiveState>(File.ReadAllText(ActiveStatePath), JsonOptions);
            if (state is null || state.Apps.Length == 0)
            {
                return "No applications are currently monitored by RestartableLaunch.";
            }

            var liveApps = state.Apps.Where(static app => IsProcessAlive(app.ProcessId)).ToArray();
            if (liveApps.Length == 0)
            {
                TryDelete(ActiveStatePath);
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
        catch
        {
            return "No applications are currently monitored by RestartableLaunch.";
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

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetShellWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
    private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr hMem);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;

        public static WindowPlacement Create()
        {
            return new WindowPlacement
            {
                Length = Marshal.SizeOf<WindowPlacement>(),
            };
        }
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    private interface IVirtualDesktopManager
    {
        int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out bool onCurrentDesktop);

        int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

        int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    private static class VirtualDesktopPlacement
    {
        private static IVirtualDesktopManager CreateManager()
        {
            var type = Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A"), throwOnError: true);
            return (IVirtualDesktopManager)Activator.CreateInstance(type!)!;
        }

        public static Guid? TryGetDesktopId(IntPtr window)
        {
            if (window == IntPtr.Zero)
            {
                return null;
            }

            var accessorDesktopId = VirtualDesktopAccessor.TryGetWindowDesktopId(window);
            if (accessorDesktopId is not null)
            {
                return accessorDesktopId;
            }

            try
            {
                var manager = CreateManager();
                var result = manager.GetWindowDesktopId(window, out var desktopId);
                return result == 0 && desktopId != Guid.Empty ? desktopId : null;
            }
            catch
            {
                return null;
            }
        }

        public static bool TryMoveToDesktop(IntPtr window, Guid desktopId)
        {
            if (window == IntPtr.Zero || desktopId == Guid.Empty)
            {
                return false;
            }

            if (VirtualDesktopAccessor.TryMoveWindowToDesktop(window, desktopId))
            {
                return true;
            }

            try
            {
                var manager = CreateManager();
                var target = desktopId;
                return manager.MoveWindowToDesktop(window, ref target) == 0;
            }
            catch
            {
                return false;
            }
        }

        public static bool TrySwitchToDesktop(Guid desktopId)
        {
            if (desktopId == Guid.Empty)
            {
                return false;
            }

            return VirtualDesktopAccessor.TrySwitchToDesktop(desktopId);
        }
    }

    private static class VirtualDesktopAccessor
    {
        public static Guid? TryGetWindowDesktopId(IntPtr window)
        {
            try
            {
                var desktopId = GetWindowDesktopId(window);
                return desktopId == Guid.Empty ? null : desktopId;
            }
            catch
            {
                return null;
            }
        }

        public static bool TryMoveWindowToDesktop(IntPtr window, Guid desktopId)
        {
            try
            {
                var desktopNumber = GetDesktopNumberById(desktopId);
                return desktopNumber >= 0 && MoveWindowToDesktopNumber(window, desktopNumber) != -1;
            }
            catch
            {
                return false;
            }
        }

        public static bool TrySwitchToDesktop(Guid desktopId)
        {
            try
            {
                var desktopNumber = GetDesktopNumberById(desktopId);
                return desktopNumber >= 0 && GoToDesktopNumber(desktopNumber) != -1;
            }
            catch
            {
                return false;
            }
        }

        [DllImport("VirtualDesktopAccessor.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern Guid GetWindowDesktopId(IntPtr hwnd);

        [DllImport("VirtualDesktopAccessor.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GetDesktopNumberById(Guid desktopId);

        [DllImport("VirtualDesktopAccessor.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int MoveWindowToDesktopNumber(IntPtr hwnd, int desktopNumber);

        [DllImport("VirtualDesktopAccessor.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GoToDesktopNumber(int desktopNumber);
    }

    private sealed class ManagerContext : ApplicationContext
    {
        private readonly NotifyIcon notifyIcon;
        private readonly MainForm mainForm;
        private readonly List<MonitoredApp> apps = [];
        private readonly List<ProcessMonitorRule> monitorRules = [];
        private readonly List<SavedApp> pendingPlacementOnlyApps = [];
        private readonly SynchronizationContext uiContext;
        private bool sessionEnding;
        private bool sessionRestored;
        private bool restoringSession;
        private bool globalPlacementOnly;

        public ManagerContext()
        {
            uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            mainForm = new MainForm(this);

            notifyIcon = new NotifyIcon
            {
                Icon = CloneAppIcon(),
                Text = "RestartableLaunch",
                Visible = true,
                ContextMenuStrip = new ContextMenuStrip(),
            };
            notifyIcon.ContextMenuStrip.Items.Add("Open", null, (_, _) => ShowMainWindow());
            notifyIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => ExitThread());
            notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

            SystemEvents.SessionEnding += OnSessionEnding;
            _ = MonitorRulesAsync();
        }

        public IReadOnlyList<MonitoredApp> Apps => apps;

        public IReadOnlyList<ProcessMonitorRule> MonitorRules => monitorRules;

        public IReadOnlyList<SavedApp> PendingPlacementOnlyApps => pendingPlacementOnlyApps;

        public bool GlobalPlacementOnly => globalPlacementOnly;

        public bool IsExiting { get; private set; }

        public void Post(Action action)
        {
            if (IsExiting)
            {
                return;
            }

            uiContext.Post(_ =>
            {
                if (!IsExiting)
                {
                    action();
                }
            }, null);
        }

        public void Launch(LaunchRequest request)
        {
            _ = Launch(request, ruleId: null, applyRestorePlacement: false);
        }

        private MonitoredApp? Launch(LaunchRequest request, Guid? ruleId, bool applyRestorePlacement)
        {
            try
            {
                var existingWindows = GetTopLevelWindowHandles();
                var process = request.Kind == LaunchKind.DefaultOpen
                    ? StartDefaultOpen(request.Executable)
                    : StartExecutable(request);
                process.EnableRaisingEvents = true;
                var startedAt = TryGetProcessStartedAt(process) ?? DateTimeOffset.Now;

                var initialDesktopId = applyRestorePlacement ? request.DesktopId : null;
                var initialBounds = applyRestorePlacement ? request.WindowBounds : null;
                var app = new MonitoredApp(request, process, startedAt, initialDesktopId, initialBounds, ruleId);
                if (applyRestorePlacement)
                {
                    app.PendingRestoreDesktopId = request.DesktopId;
                    app.PendingRestoreWindowBounds = request.WindowBounds;
                    app.ProtectRestorePlacementUntil = DateTimeOffset.UtcNow.AddMilliseconds(RestorePlacementProtectionMilliseconds);
                }

                apps.Add(app);
                process.Exited += (_, _) => Post(() => RemoveExitedApp(app, process));

                SaveSession();
                mainForm.RefreshApps();
                _ = TrackWindowPlacementAsync(app, request.DesktopId, request.WindowBounds, existingWindows, applyRestorePlacement);
                return app;
            }
            catch (Exception ex)
            {
                MessageBox(IntPtr.Zero, ex.Message, "RestartableLaunch", 0x10);
                LogException("launch-failed", ex, FormatRequest(request));
                return null;
            }
        }

        public void AdoptWindow(WindowCandidate candidate, bool monitorAllInstances)
        {
            try
            {
                var existingApp = apps.FirstOrDefault(app => !app.Process.HasExited && app.Process.Id == candidate.ProcessId);
                if (existingApp is not null)
                {
                    if (monitorAllInstances)
                    {
                        MonitorAllInstances(existingApp);
                        return;
                    }

                    MessageBox(IntPtr.Zero, "That process is already monitored.", "RestartableLaunch", 0x40);
                    return;
                }

                var process = Process.GetProcessById(candidate.ProcessId);
                var request = TryCreateRequestFromProcess(process);
                if (request is null)
                {
                    process.Dispose();
                    MessageBox(IntPtr.Zero, "Could not read a restartable command line for that window.", "RestartableLaunch", 0x10);
                    return;
                }

                var desktopId = VirtualDesktopPlacement.TryGetDesktopId(candidate.Handle);
                var bounds = TryGetWindowBounds(candidate.Handle);
                request = request with { DesktopId = desktopId, WindowBounds = bounds };

                process.EnableRaisingEvents = true;
                var rule = monitorAllInstances ? EnsureMonitorRule(process, MonitorMode.Restart) : null;
                var app = new MonitoredApp(request, process, DateTimeOffset.Now, desktopId, bounds, rule?.Id);
                app.WindowHandle = candidate.Handle;
                app.WindowTitle = candidate.Title;
                app.WindowClass = candidate.WindowClass;
                app.HasResolvedWindow = true;
                app.WindowResolved.TrySetResult(true);
                apps.Add(app);
                process.Exited += (_, _) => Post(() => RemoveExitedApp(app, process));
                _ = TrackWindowPlacementAsync(app, desktopId, bounds, GetTopLevelWindowHandles(), applyRestorePlacement: false);

                if (rule is not null)
                {
                    ScanMonitorRules(save: false);
                }

                SaveSession();
                mainForm.RefreshApps();
            }
            catch (Exception ex)
            {
                MessageBox(IntPtr.Zero, ex.Message, "RestartableLaunch", 0x10);
            }
        }

        public void MonitorAllInstances(MonitoredApp app)
        {
            try
            {
                if (app.Process.HasExited)
                {
                    return;
                }

                var rule = EnsureMonitorRule(app.Process, MonitorMode.Restart);
                app.RuleId = rule.Id;
                app.Mode = MonitorMode.Restart;
                ScanMonitorRules(save: false);
                SaveSession();
                mainForm.RefreshApps();
            }
            catch (Exception ex)
            {
                MessageBox(IntPtr.Zero, ex.Message, "RestartableLaunch", 0x10);
            }
        }

        public void MonitorPlacementOnly(MonitoredApp app)
        {
            try
            {
                if (app.Process.HasExited)
                {
                    return;
                }

                var rule = EnsureMonitorRule(app.Process, MonitorMode.PlacementOnly);
                app.RuleId = rule.Id;
                app.Mode = MonitorMode.PlacementOnly;
                SaveSession();
                mainForm.RefreshApps();
            }
            catch (Exception ex)
            {
                MessageBox(IntPtr.Zero, ex.Message, "RestartableLaunch", 0x10);
            }
        }

        public void AdoptWindowPlacementOnly(WindowCandidate candidate)
        {
            try
            {
                var existingApp = apps.FirstOrDefault(app => app.WindowHandle == candidate.Handle);
                if (existingApp is not null)
                {
                    MonitorPlacementOnly(existingApp);
                    return;
                }

                var process = Process.GetProcessById(candidate.ProcessId);
                var rule = EnsureMonitorRule(process, MonitorMode.PlacementOnly);
                if (!TryAdoptPlacementWindow(process, candidate.Handle, rule.Id, pendingSnapshot: null))
                {
                    process.Dispose();
                    MessageBox(IntPtr.Zero, "Could not read a command line for that window.", "RestartableLaunch", 0x10);
                    return;
                }

                ScanMonitorRules(save: false);
                SaveSession();
                mainForm.RefreshApps();
            }
            catch (Exception ex)
            {
                MessageBox(IntPtr.Zero, ex.Message, "RestartableLaunch", 0x10);
            }
        }

        public void SetGlobalPlacementOnly(bool enabled)
        {
            if (globalPlacementOnly == enabled)
            {
                return;
            }

            globalPlacementOnly = enabled;
            if (enabled)
            {
                ScanMonitorRules(save: false);
            }

            SaveSession();
            mainForm.RefreshApps();
        }

        public void RemoveItems(IEnumerable<object> selectedItems)
        {
            var items = selectedItems.ToArray();
            var ruleIdsToRemove = items
                .OfType<ProcessMonitorRule>()
                .Select(static rule => rule.Id)
                .Concat(items.OfType<MonitoredApp>().Select(static app => app.RuleId).OfType<Guid>())
                .ToHashSet();

            var removed = false;
            foreach (var snapshot in items.OfType<SavedApp>().ToArray())
            {
                removed |= pendingPlacementOnlyApps.Remove(snapshot);
            }

            if (ruleIdsToRemove.Count > 0)
            {
                removed |= monitorRules.RemoveAll(rule => ruleIdsToRemove.Contains(rule.Id)) > 0;
                removed |= pendingPlacementOnlyApps.RemoveAll(snapshot => ruleIdsToRemove.Contains(snapshot.RuleId ?? Guid.Empty)) > 0;
            }

            var appsToRemove = apps
                .Where(app => ruleIdsToRemove.Contains(app.RuleId ?? Guid.Empty))
                .Concat(items.OfType<MonitoredApp>())
                .Distinct()
                .ToArray();

            foreach (var app in appsToRemove)
            {
                if (apps.Remove(app))
                {
                    app.Process.Dispose();
                    removed = true;
                }
            }

            if (removed)
            {
                SaveSession();
                mainForm.RefreshApps();
            }
        }

        public void RestoreSavedSession()
        {
            if (sessionRestored)
            {
                return;
            }

            sessionRestored = true;
            if (!File.Exists(SessionPath))
            {
                return;
            }

            Post(() => _ = RestoreSavedSessionAsync());
        }

        private async Task RestoreSavedSessionAsync()
        {
            var completed = false;
            try
            {
                var session = JsonSerializer.Deserialize<SavedSession>(File.ReadAllText(SessionPath), JsonOptions);
                if (session is null)
                {
                    return;
                }

                restoringSession = true;
                monitorRules.Clear();
                pendingPlacementOnlyApps.Clear();
                monitorRules.AddRange(session.Rules ?? []);
                globalPlacementOnly = session.GlobalPlacementOnly;
                LogMessage("restore-begin", $"Apps: {session.Apps.Length}; rules: {monitorRules.Count}; global placement-only: {globalPlacementOnly}");

                var restoredRuleBackedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var savedApp in session.Apps)
                {
                    if (IsExiting)
                    {
                        break;
                    }

                    if (savedApp.Mode == MonitorMode.PlacementOnly)
                    {
                        pendingPlacementOnlyApps.Add(savedApp);
                        continue;
                    }

                    if (TryAdoptSavedApp(savedApp))
                    {
                        if (savedApp.RuleId is not null)
                        {
                            restoredRuleBackedCommands.Add(GetSavedAppRestartKey(savedApp));
                        }

                        continue;
                    }

                    if (savedApp.RuleId is not null && !restoredRuleBackedCommands.Add(GetSavedAppRestartKey(savedApp)))
                    {
                        LogMessage("restore-deduplicate", $"Skipped duplicate saved app: {FormatRequest(savedApp.ToLaunchRequest())}");
                        continue;
                    }

                    if (savedApp.RuleId is not null && (savedApp.Arguments?.Length ?? 0) == 0)
                    {
                        LogMessage("restore-skip", $"Skipped rule-backed app without arguments because it could not be reattached: {savedApp.Executable}");
                        continue;
                    }

                    var request = savedApp.ToLaunchRequest();
                    LogMessage("restore-launch", $"{FormatRequest(request)}; desktop: {savedApp.DesktopId?.ToString() ?? "unknown"}; bounds: {FormatWindowBounds(savedApp.WindowBounds)}");
                    var app = Launch(request, savedApp.RuleId, applyRestorePlacement: true);
                    if (app is null)
                    {
                        continue;
                    }

                    await WaitForRestoreLaunchAsync(app);
                }

                ScanMonitorRules(save: false);
                completed = true;
                LogMessage("restore-complete", $"Monitored apps: {apps.Count}; rules: {monitorRules.Count}");
            }
            catch (Exception ex)
            {
                LogException("restore-failed", ex);
                MessageBox(IntPtr.Zero, ex.Message, "RestartableLaunch restore", 0x10);
            }
            finally
            {
                restoringSession = false;
                if (completed)
                {
                    SaveSession();
                }
                else
                {
                    SaveActiveState();
                }

                mainForm.RefreshApps();
            }
        }

        private static async Task WaitForRestoreLaunchAsync(MonitoredApp app)
        {
            var windowTask = app.WindowResolved.Task;
            var completedTask = await Task.WhenAny(windowTask, Task.Delay(RestoreLaunchWindowWaitMilliseconds));
            if (completedTask != windowTask)
            {
                LogMessage("restore-window-timeout", FormatRequest(app.Request));
            }

            await Task.Delay(RestoreLaunchSpacingMilliseconds);
        }

        public void ShowMainWindow()
        {
            mainForm.RefreshApps();
            mainForm.Show();
            mainForm.WindowState = FormWindowState.Normal;
            mainForm.Activate();
        }

        protected override void ExitThreadCore()
        {
            IsExiting = true;

            if (!sessionEnding)
            {
                RefreshWindowPlacements();
                SaveSession();
            }

            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            mainForm.Dispose();
            SystemEvents.SessionEnding -= OnSessionEnding;

            base.ExitThreadCore();
        }

        private static string GetWorkingDirectory(string executable)
        {
            var fullPath = Path.GetFullPath(executable);
            var directory = Path.GetDirectoryName(fullPath);
            return string.IsNullOrWhiteSpace(directory) ? Environment.CurrentDirectory : directory;
        }

        private static Process StartExecutable(LaunchRequest request)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = request.Executable,
                UseShellExecute = true,
                WorkingDirectory = GetWorkingDirectory(request.Executable),
            };

            foreach (var arg in request.Arguments)
            {
                startInfo.ArgumentList.Add(arg);
            }

            return Process.Start(startInfo) ?? throw new InvalidOperationException("The child process could not be started.");
        }

        private static Process StartDefaultOpen(string target)
        {
            if (Directory.Exists(target))
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = false,
                };
                startInfo.ArgumentList.Add(target);
                return Process.Start(startInfo) ?? throw new InvalidOperationException("Explorer could not be started.");
            }

            var shellStartInfo = new ProcessStartInfo
            {
                FileName = target,
                Verb = "open",
                UseShellExecute = true,
                WorkingDirectory = File.Exists(target)
                    ? Path.GetDirectoryName(Path.GetFullPath(target)) ?? Environment.CurrentDirectory
                    : Environment.CurrentDirectory,
            };

            return Process.Start(shellStartInfo) ?? throw new InvalidOperationException("The default shell action did not return a process to monitor.");
        }

        private void Remove(MonitoredApp app)
        {
            apps.Remove(app);
            app.Process.Dispose();
            SaveSession();
            mainForm.RefreshApps();
        }

        private void AddPendingPlacementSnapshot(SavedApp snapshot)
        {
            if (snapshot.Mode != MonitorMode.PlacementOnly)
            {
                snapshot = snapshot with { Mode = MonitorMode.PlacementOnly };
            }

            pendingPlacementOnlyApps.RemoveAll(existing => HaveSamePlacementIdentity(existing, snapshot));
            pendingPlacementOnlyApps.Add(snapshot);
        }

        private void RemoveExitedApp(MonitoredApp app, Process exitedProcess)
        {
            if (sessionEnding)
            {
                return;
            }

            if (!ReferenceEquals(app.Process, exitedProcess) || !app.HasResolvedWindow)
            {
                return;
            }

            if (app.Mode == MonitorMode.PlacementOnly)
            {
                AddPendingPlacementSnapshot(SavedApp.FromMonitoredApp(app));
            }

            Remove(app);
        }

        private bool TryAdoptSavedApp(SavedApp savedApp)
        {
            if (savedApp.ProcessId <= 0)
            {
                return false;
            }

            try
            {
                var process = Process.GetProcessById(savedApp.ProcessId);
                if (process.HasExited)
                {
                    process.Dispose();
                    return false;
                }

                var request = savedApp.ToLaunchRequest();
                if (!MatchesSavedProcess(process, savedApp))
                {
                    if (!HasSameSavedStartTime(process, savedApp))
                    {
                        process.Dispose();
                        return false;
                    }

                    var repairedRequest = TryCreateRequestFromProcess(process, requireCommandLine: true);
                    if (repairedRequest is null || !MatchesSavedProcessName(process, savedApp))
                    {
                        process.Dispose();
                        return false;
                    }

                    request = repairedRequest with
                    {
                        DesktopId = savedApp.DesktopId,
                        WindowBounds = savedApp.WindowBounds,
                    };
                    LogMessage("restore-repair", $"Repaired saved command line for PID {process.Id}: {FormatRequest(request)}");
                }

                if (apps.Any(app => !app.Process.HasExited && app.Process.Id == process.Id))
                {
                    process.Dispose();
                    return true;
                }

                process.EnableRaisingEvents = true;
                var startedAt = TryGetProcessStartedAt(process)
                    ?? (savedApp.StartedAt == default ? DateTimeOffset.Now : savedApp.StartedAt);
                var app = new MonitoredApp(
                    request,
                    process,
                    startedAt,
                    savedApp.DesktopId,
                    savedApp.WindowBounds,
                    savedApp.RuleId,
                    savedApp.Mode)
                {
                    WindowTitle = savedApp.WindowTitle ?? string.Empty,
                    WindowClass = savedApp.WindowClass ?? string.Empty,
                };

                apps.Add(app);
                process.Exited += (_, _) => Post(() => RemoveExitedApp(app, process));

                var window = FindTopLevelWindow(process.Id);
                if (window != IntPtr.Zero)
                {
                    app.WindowHandle = window;
                    app.HasResolvedWindow = true;
                    app.WindowResolved.TrySetResult(true);
                    UpdateWindowPlacement(app, window, savedApp.DesktopId, savedApp.WindowBounds, save: false);
                }

                _ = TrackWindowPlacementAsync(app, savedApp.DesktopId, savedApp.WindowBounds, GetTopLevelWindowHandles(), applyRestorePlacement: false);

                SaveSession();
                mainForm.RefreshApps();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetSavedAppRestartKey(SavedApp savedApp)
        {
            return GetLaunchRequestRestartKey(savedApp.ToLaunchRequest(), savedApp.RuleId);
        }

        private static string GetLaunchRequestRestartKey(LaunchRequest request, Guid? ruleId)
        {
            var builder = new StringBuilder()
                .Append(ruleId?.ToString("D") ?? string.Empty)
                .Append('|')
                .Append(request.Kind)
                .Append('|')
                .Append(NormalizeRestartKeyPart(request.Executable));

            foreach (var argument in request.Arguments ?? [])
            {
                builder.Append('|').Append(NormalizeRestartKeyPart(argument));
            }

            return builder.ToString();
        }

        private static string NormalizeRestartKeyPart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            try
            {
                if (Path.IsPathFullyQualified(value))
                {
                    return Path.GetFullPath(value).Trim().ToLowerInvariant();
                }
            }
            catch
            {
                // Use the trimmed value below.
            }

            return value.Trim().ToLowerInvariant();
        }

        private static DateTimeOffset? TryGetProcessStartedAt(Process process)
        {
            try
            {
                return new DateTimeOffset(process.StartTime);
            }
            catch
            {
                return null;
            }
        }

        private static bool HasSameSavedCommandLine(Process process, SavedApp savedApp)
        {
            var processExecutable = TryGetProcessExecutablePath(process);
            if (!string.IsNullOrWhiteSpace(processExecutable)
                && !string.Equals(
                    NormalizeRestartKeyPart(processExecutable),
                    NormalizeRestartKeyPart(savedApp.Executable),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var commandLine = TryGetProcessCommandLine(process.Id);
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                return !string.IsNullOrWhiteSpace(processExecutable)
                    && (savedApp.Arguments?.Length ?? 0) == 0;
            }

            var arguments = CommandLineToArguments(commandLine);
            if (arguments.Length == 0)
            {
                return false;
            }

            var processRequest = new LaunchRequest(arguments[0], arguments.Skip(1).ToArray(), null, savedApp.Kind, null);
            return string.Equals(
                GetLaunchRequestRestartKey(processRequest, savedApp.RuleId),
                GetSavedAppRestartKey(savedApp),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesSavedProcess(Process process, SavedApp savedApp)
        {
            if (!MatchesSavedProcessName(process, savedApp))
            {
                return false;
            }

            if (savedApp.StartedAt != default)
            {
                try
                {
                    var delta = (process.StartTime.ToUniversalTime() - savedApp.StartedAt.UtcDateTime).Duration();
                    if (delta > TimeSpan.FromSeconds(2))
                    {
                        return HasSameSavedCommandLine(process, savedApp);
                    }
                }
                catch
                {
                    return HasSameSavedCommandLine(process, savedApp);
                }
            }

            return true;
        }

        private static bool HasSameSavedStartTime(Process process, SavedApp savedApp)
        {
            if (savedApp.StartedAt == default)
            {
                return false;
            }

            try
            {
                var delta = (process.StartTime.ToUniversalTime() - savedApp.StartedAt.UtcDateTime).Duration();
                return delta <= TimeSpan.FromSeconds(2);
            }
            catch
            {
                return false;
            }
        }

        private static bool MatchesSavedProcessName(Process process, SavedApp savedApp)
        {
            var expectedProcessName = !string.IsNullOrWhiteSpace(savedApp.ProcessName)
                ? savedApp.ProcessName
                : Path.GetFileNameWithoutExtension(savedApp.Executable);
            return string.IsNullOrWhiteSpace(expectedProcessName)
                || string.Equals(process.ProcessName, expectedProcessName, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsAlreadyMonitoredProcess(int processId)
        {
            return apps.Any(app => !app.Process.HasExited && app.Process.Id == processId);
        }

        private ProcessMonitorRule EnsureMonitorRule(Process process, MonitorMode mode)
        {
            var executablePath = TryGetProcessExecutablePath(process);
            var processName = process.ProcessName;
            var existingRule = monitorRules.FirstOrDefault(rule => rule.Mode == mode && MatchesRuleIdentity(rule, executablePath, processName));
            if (existingRule is not null)
            {
                return existingRule;
            }

            var rule = new ProcessMonitorRule(Guid.NewGuid(), executablePath, processName, mode);
            monitorRules.Add(rule);
            return rule;
        }

        private async Task MonitorRulesAsync()
        {
            while (!IsExiting)
            {
                await Task.Delay(2000);
                if (IsExiting)
                {
                    break;
                }

                Post(() =>
                {
                    RefreshIconCache();
                    ScanMonitorRules(save: true);
                });
            }
        }

        private void RefreshIconCache()
        {
            foreach (var app in apps.ToArray())
            {
                try
                {
                    if (!app.Process.HasExited)
                    {
                        using var _ = TryGetProcessIcon(app.Process);
                    }
                }
                catch
                {
                    // Cache refresh must never disturb monitoring.
                }
            }

            foreach (var rule in monitorRules.ToArray())
            {
                try
                {
                    using var _ = TryGetRuleIcon(rule);
                }
                catch
                {
                    // Cache refresh must never disturb monitoring.
                }
            }
        }

        private void ScanMonitorRules(bool save)
        {
            if (monitorRules.Count == 0 && !globalPlacementOnly && pendingPlacementOnlyApps.Count == 0)
            {
                return;
            }

            var added = false;
            foreach (var rule in monitorRules.ToArray())
            {
                if (rule.Mode != MonitorMode.Restart)
                {
                    continue;
                }

                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        if (process.HasExited || IsAlreadyMonitoredProcess(process.Id) || !MatchesMonitorRule(process, rule))
                        {
                            process.Dispose();
                            continue;
                        }

                        if (TryAdoptProcess(process, rule.Id))
                        {
                            added = true;
                        }
                        else
                        {
                            process.Dispose();
                        }
                    }
                    catch
                    {
                        process.Dispose();
                    }
                }
            }

            added |= ScanPlacementOnlyWindows();

            if (added && save)
            {
                SaveSession();
                mainForm.RefreshApps();
            }
        }

        private bool ScanPlacementOnlyWindows()
        {
            var placementRules = monitorRules.Where(static rule => rule.Mode == MonitorMode.PlacementOnly).ToArray();
            if (placementRules.Length == 0 && !globalPlacementOnly && pendingPlacementOnlyApps.Count == 0)
            {
                return false;
            }

            var added = false;
            var shellWindow = GetShellWindow();
            EnumWindows((window, _) =>
            {
                if (window == shellWindow
                    || !IsWindowVisible(window)
                    || apps.Any(app => app.WindowHandle == window))
                {
                    return true;
                }

                var title = GetWindowTitle(window);
                if (string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                GetWindowThreadProcessId(window, out var processId);
                if (processId <= 0 || processId == Environment.ProcessId)
                {
                    return true;
                }

                Process? process = null;
                try
                {
                    process = Process.GetProcessById(processId);
                    if (process.HasExited)
                    {
                        return true;
                    }

                    var rule = placementRules.FirstOrDefault(rule => MatchesMonitorRule(process, rule));
                    var pendingSnapshot = TryTakePendingPlacementSnapshot(process, window, title);
                    if (rule is null && pendingSnapshot is null && !globalPlacementOnly)
                    {
                        return true;
                    }

                    if (TryAdoptPlacementWindow(process, window, rule?.Id ?? pendingSnapshot?.RuleId, pendingSnapshot))
                    {
                        process = null;
                        added = true;
                    }
                }
                catch (Exception ex)
                {
                    LogException("placement-scan-window", ex);
                }
                finally
                {
                    process?.Dispose();
                }

                return true;
            }, IntPtr.Zero);

            return added;
        }

        private bool TryAdoptProcess(Process process, Guid? ruleId)
        {
            if (apps.Any(app => !app.Process.HasExited && app.Process.Id == process.Id))
            {
                return false;
            }

            var window = FindTopLevelWindow(process.Id);
            if (ruleId is not null && window == IntPtr.Zero)
            {
                return false;
            }

            var request = TryCreateRequestFromProcess(process, requireCommandLine: ruleId is not null);
            if (request is null)
            {
                return false;
            }

            var desktopId = VirtualDesktopPlacement.TryGetDesktopId(window);
            var bounds = TryGetWindowBounds(window);
            request = request with { DesktopId = desktopId, WindowBounds = bounds };

            process.EnableRaisingEvents = true;
            var app = new MonitoredApp(request, process, TryGetProcessStartedAt(process) ?? DateTimeOffset.Now, desktopId, bounds, ruleId);
            if (window != IntPtr.Zero)
            {
                app.WindowHandle = window;
                app.WindowTitle = GetWindowTitle(window);
                app.WindowClass = GetWindowClass(window);
                app.HasResolvedWindow = true;
                app.WindowResolved.TrySetResult(true);
            }

            apps.Add(app);
            process.Exited += (_, _) => Post(() => RemoveExitedApp(app, process));
            _ = TrackWindowPlacementAsync(app, desktopId, bounds, GetTopLevelWindowHandles(), applyRestorePlacement: false);
            return true;
        }

        private bool TryAdoptPlacementWindow(Process process, IntPtr window, Guid? ruleId, SavedApp? pendingSnapshot)
        {
            if (apps.Any(app => app.WindowHandle == window))
            {
                return false;
            }

            var request = TryCreateRequestFromProcess(process);
            if (request is null)
            {
                return false;
            }

            var desktopId = VirtualDesktopPlacement.TryGetDesktopId(window);
            var bounds = TryGetWindowBounds(window);
            var title = GetWindowTitle(window);
            var windowClass = GetWindowClass(window);
            request = request with { DesktopId = desktopId, WindowBounds = bounds };

            process.EnableRaisingEvents = true;
            var app = new MonitoredApp(
                request,
                process,
                TryGetProcessStartedAt(process) ?? DateTimeOffset.Now,
                pendingSnapshot?.DesktopId ?? desktopId,
                pendingSnapshot?.WindowBounds ?? bounds,
                ruleId,
                MonitorMode.PlacementOnly)
            {
                WindowHandle = window,
                WindowTitle = title,
                WindowClass = windowClass,
                HasResolvedWindow = true,
            };
            app.WindowResolved.TrySetResult(true);

            if (pendingSnapshot is not null)
            {
                app.PendingRestoreDesktopId = pendingSnapshot.DesktopId;
                app.PendingRestoreWindowBounds = pendingSnapshot.WindowBounds;
                app.ProtectRestorePlacementUntil = DateTimeOffset.UtcNow.AddMilliseconds(RestorePlacementProtectionMilliseconds);
            }

            apps.Add(app);
            if (pendingSnapshot is not null)
            {
                pendingPlacementOnlyApps.Remove(pendingSnapshot);
            }

            process.Exited += (_, _) => Post(() => RemoveExitedApp(app, process));
            _ = TrackWindowPlacementAsync(
                app,
                pendingSnapshot?.DesktopId ?? desktopId,
                pendingSnapshot?.WindowBounds ?? bounds,
                GetTopLevelWindowHandles(),
                applyRestorePlacement: pendingSnapshot is not null);
            return true;
        }

        private SavedApp? TryTakePendingPlacementSnapshot(Process process, IntPtr window, string title)
        {
            if (pendingPlacementOnlyApps.Count == 0)
            {
                return null;
            }

            var windowClass = GetWindowClass(window);
            var candidates = pendingPlacementOnlyApps
                .Where(snapshot => MatchesPlacementProcess(process, snapshot)
                    && MatchesPlacementWindowClass(snapshot, windowClass))
                .ToArray();
            if (candidates.Length == 0)
            {
                return null;
            }

            var titleMatches = candidates
                .Where(snapshot => MatchesPlacementWindowTitle(snapshot, title))
                .ToArray();
            if (titleMatches.Length == 1)
            {
                if (CountOpenPlacementCandidates(process, windowClass, title) == 1)
                {
                    return titleMatches[0];
                }

                LogMessage("placement-match-ambiguous", $"Multiple open title matches for {process.ProcessName}: {title}");
                return null;
            }

            if (titleMatches.Length > 1)
            {
                LogMessage("placement-match-ambiguous", $"Multiple title matches for {process.ProcessName}: {title}");
                return null;
            }

            if (candidates.Length == 1 && CountOpenPlacementCandidates(process, windowClass, title: null) == 1)
            {
                return candidates[0];
            }

            LogMessage("placement-match-ambiguous", $"Ambiguous placement candidates for {process.ProcessName}: {title}; candidates: {candidates.Length}");
            return null;
        }

        private static bool MatchesPlacementProcess(Process process, SavedApp snapshot)
        {
            var expectedProcessName = !string.IsNullOrWhiteSpace(snapshot.ProcessName)
                ? snapshot.ProcessName
                : Path.GetFileNameWithoutExtension(snapshot.Executable);
            if (!string.IsNullOrWhiteSpace(expectedProcessName)
                && !string.Equals(process.ProcessName, expectedProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var expectedPath = snapshot.Executable;
            var actualPath = TryGetProcessExecutablePath(process);
            if (string.IsNullOrWhiteSpace(expectedPath)
                || string.IsNullOrWhiteSpace(actualPath)
                || !Path.IsPathFullyQualified(expectedPath))
            {
                return true;
            }

            return string.Equals(
                NormalizeRestartKeyPart(expectedPath),
                NormalizeRestartKeyPart(actualPath),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesPlacementWindowClass(SavedApp snapshot, string windowClass)
        {
            return string.IsNullOrWhiteSpace(snapshot.WindowClass)
                || string.IsNullOrWhiteSpace(windowClass)
                || string.Equals(snapshot.WindowClass, windowClass, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesPlacementWindowTitle(SavedApp snapshot, string title)
        {
            return !string.IsNullOrWhiteSpace(snapshot.WindowTitle)
                && string.Equals(snapshot.WindowTitle.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool HaveSamePlacementIdentity(SavedApp left, SavedApp right)
        {
            return string.Equals(NormalizeRestartKeyPart(left.Executable), NormalizeRestartKeyPart(right.Executable), StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.ProcessName ?? string.Empty, right.ProcessName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.WindowClass ?? string.Empty, right.WindowClass ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.WindowTitle ?? string.Empty, right.WindowTitle ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && left.RuleId == right.RuleId;
        }

        private int CountOpenPlacementCandidates(Process referenceProcess, string windowClass, string? title)
        {
            var count = 0;
            var shellWindow = GetShellWindow();
            EnumWindows((window, _) =>
            {
                if (window == shellWindow || !IsWindowVisible(window) || apps.Any(app => app.WindowHandle == window))
                {
                    return true;
                }

                if (!string.Equals(GetWindowClass(window), windowClass, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (title is not null && !string.Equals(GetWindowTitle(window), title, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                GetWindowThreadProcessId(window, out var processId);
                if (processId <= 0)
                {
                    return true;
                }

                try
                {
                    using var process = Process.GetProcessById(processId);
                    if (MatchesSameProcessIdentity(referenceProcess, process))
                    {
                        count++;
                    }
                }
                catch
                {
                    // Ignore racing windows.
                }

                return count <= 1;
            }, IntPtr.Zero);

            return count;
        }

        private static bool MatchesSameProcessIdentity(Process left, Process right)
        {
            if (!string.Equals(left.ProcessName, right.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var leftPath = TryGetProcessExecutablePath(left);
            var rightPath = TryGetProcessExecutablePath(right);
            return string.IsNullOrWhiteSpace(leftPath)
                || string.IsNullOrWhiteSpace(rightPath)
                || string.Equals(leftPath, rightPath, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesMonitorRule(Process process, ProcessMonitorRule rule)
        {
            if (!string.Equals(process.ProcessName, rule.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var processPath = TryGetProcessExecutablePath(process);
            return string.IsNullOrWhiteSpace(rule.ExecutablePath)
                || string.IsNullOrWhiteSpace(processPath)
                || string.Equals(processPath, rule.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesRuleIdentity(ProcessMonitorRule rule, string executablePath, string processName)
        {
            if (!string.Equals(rule.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(rule.ExecutablePath)
                || string.IsNullOrWhiteSpace(executablePath)
                || string.Equals(rule.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase);
        }

        private void SaveSession()
        {
            if (IsExiting && !sessionEnding)
            {
                return;
            }

            if (restoringSession)
            {
                SaveActiveState();
                return;
            }

            var savedApps = apps
                .Select(static app => SavedApp.FromMonitoredApp(app))
                .Concat(pendingPlacementOnlyApps)
                .ToArray();
            var session = new SavedSession(savedApps, monitorRules.ToArray(), globalPlacementOnly);
            File.WriteAllText(SessionPath, JsonSerializer.Serialize(session, JsonOptions));
            SaveActiveState();
        }

        private void SaveActiveState()
        {
            var state = new ActiveState(apps.Select(static app => new ActiveAppState(
                app.Process.Id,
                app.StartedAt,
                app.Request with { DesktopId = app.DesktopId, WindowBounds = app.WindowBounds },
                app.DesktopId,
                app.WindowBounds)).ToArray());

            File.WriteAllText(ActiveStatePath, JsonSerializer.Serialize(state, JsonOptions));
        }

        private void RefreshWindowPlacements()
        {
            foreach (var app in apps)
            {
                if (app.WindowHandle != IntPtr.Zero)
                {
                    UpdateWindowPlacement(app, app.WindowHandle, app.DesktopId, app.WindowBounds, save: false);
                }
            }
        }

        private void LogShutdownSnapshot(SessionEndReasons reason)
        {
            var builder = new StringBuilder()
                .AppendLine($"Reason: {reason}")
                .AppendLine($"Monitored apps: {apps.Count}")
                .AppendLine($"All-instance rules: {monitorRules.Count}");

            foreach (var app in apps.ToArray())
            {
                builder.AppendLine()
                    .AppendLine($"App PID: {SafeGetProcessId(app.Process)}")
                    .AppendLine($"Process: {SafeGetProcessName(app.Process)}")
                    .AppendLine($"Has exited: {SafeHasExited(app.Process)}")
                    .AppendLine($"Started: {app.StartedAt:O}")
                    .AppendLine($"Mode: {app.Mode}")
                    .AppendLine($"RuleId: {app.RuleId?.ToString("D") ?? "(none)"}")
                    .AppendLine($"WindowHandle: 0x{app.WindowHandle.ToInt64():X}")
                    .AppendLine($"WindowTitle: {app.WindowTitle}")
                    .AppendLine($"WindowClass: {app.WindowClass}")
                    .AppendLine($"HasResolvedWindow: {app.HasResolvedWindow}")
                    .AppendLine($"DesktopId: {app.DesktopId?.ToString() ?? "unknown"}")
                    .AppendLine($"WindowBounds: {FormatWindowBounds(app.WindowBounds)}")
                    .AppendLine($"Command: {FormatRequest(app.Request)}");
            }

            if (monitorRules.Count > 0)
            {
                builder.AppendLine().AppendLine("Rules:");
                foreach (var rule in monitorRules.ToArray())
                {
                    builder.AppendLine($"- {rule.ProcessName}: {rule.ExecutablePath} ({rule.Mode}, {rule.Id:D})");
                }
            }

            LogMessage("shutdown-snapshot", builder.ToString());
        }

        private static int SafeGetProcessId(Process process)
        {
            try
            {
                return process.Id;
            }
            catch
            {
                return 0;
            }
        }

        private static string SafeGetProcessName(Process process)
        {
            try
            {
                return process.ProcessName;
            }
            catch
            {
                return "(unavailable)";
            }
        }

        private static bool? SafeHasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch
            {
                return null;
            }
        }

        private static string FormatWindowBounds(WindowBounds? bounds)
        {
            return bounds is null
                ? "unknown"
                : $"{bounds.Left},{bounds.Top} {bounds.Width}x{bounds.Height} {bounds.State}";
        }

        private void UpdateWindowPlacement(MonitoredApp app, IntPtr window, Guid? fallbackDesktopId, WindowBounds? fallbackBounds, bool save)
        {
            var currentDesktopId = VirtualDesktopPlacement.TryGetDesktopId(window) ?? fallbackDesktopId;
            var currentBounds = TryGetWindowBounds(window) ?? fallbackBounds;
            app.WindowTitle = GetWindowTitle(window);
            app.WindowClass = GetWindowClass(window);
            if (ShouldProtectRestorePlacement(app, currentDesktopId, currentBounds))
            {
                currentDesktopId = app.PendingRestoreDesktopId ?? currentDesktopId;
                currentBounds = app.PendingRestoreWindowBounds ?? currentBounds;
            }

            if (app.DesktopId == currentDesktopId && Equals(app.WindowBounds, currentBounds))
            {
                return;
            }

            app.DesktopId = currentDesktopId;
            app.WindowBounds = currentBounds;
            if (save)
            {
                SaveSession();
                mainForm.RefreshApps();
            }
        }

        private static bool ShouldProtectRestorePlacement(MonitoredApp app, Guid? currentDesktopId, WindowBounds? currentBounds)
        {
            if (app.PendingRestoreDesktopId is null && app.PendingRestoreWindowBounds is null)
            {
                return false;
            }

            var desktopMatches = app.PendingRestoreDesktopId is null
                || currentDesktopId == app.PendingRestoreDesktopId;
            var boundsMatch = app.PendingRestoreWindowBounds is null
                || IsWindowPlacementClose(currentBounds, app.PendingRestoreWindowBounds);
            if (desktopMatches && boundsMatch)
            {
                app.ClearPendingRestorePlacement();
                return false;
            }

            if (DateTimeOffset.UtcNow < app.ProtectRestorePlacementUntil)
            {
                return true;
            }

            LogMessage(
                "restore-placement-mismatch",
                $"Keeping observed placement after retry window. Expected: {FormatWindowBounds(app.PendingRestoreWindowBounds)}; observed: {FormatWindowBounds(currentBounds)}; command: {FormatRequest(app.Request)}");
            app.ClearPendingRestorePlacement();
            return false;
        }

        private static bool IsWindowPlacementClose(WindowBounds? actual, WindowBounds expected)
        {
            if (actual is null || actual.State != expected.State)
            {
                return false;
            }

            return Math.Abs(actual.Left - expected.Left) <= WindowPlacementTolerancePixels
                && Math.Abs(actual.Top - expected.Top) <= WindowPlacementTolerancePixels
                && Math.Abs(actual.Width - expected.Width) <= WindowPlacementTolerancePixels
                && Math.Abs(actual.Height - expected.Height) <= WindowPlacementTolerancePixels;
        }

        private async Task TrackWindowPlacementAsync(
            MonitoredApp app,
            Guid? restoreDesktopId,
            WindowBounds? restoreBounds,
            HashSet<IntPtr> existingWindows,
            bool applyRestorePlacement)
        {
            try
            {
                using var cancellation = new CancellationTokenSource(WindowWaitTimeoutMilliseconds + 5000);
                var window = app.WindowHandle != IntPtr.Zero
                    ? app.WindowHandle
                    : await WaitForLaunchWindowAsync(app, existingWindows, IsAlreadyMonitoredProcess, cancellation.Token);
                if (window == IntPtr.Zero)
                {
                    app.WindowResolved.TrySetResult(false);
                    Post(() =>
                    {
                        if (!app.HasResolvedWindow && app.Process.HasExited)
                        {
                            Remove(app);
                        }
                    });
                    return;
                }

                GetWindowThreadProcessId(window, out var windowProcessId);
                if (windowProcessId > 0 && windowProcessId != app.Process.Id)
                {
                    try
                    {
                        var previousProcess = app.Process;
                        var resolvedProcess = Process.GetProcessById(windowProcessId);
                        resolvedProcess.EnableRaisingEvents = true;
                        resolvedProcess.Exited += (_, _) => Post(() => RemoveExitedApp(app, resolvedProcess));
                        app.Process = resolvedProcess;
                        app.StartedAt = TryGetProcessStartedAt(resolvedProcess) ?? app.StartedAt;
                        previousProcess.Dispose();
                    }
                    catch
                    {
                        // Keep tracking the original process if the replacement process disappears.
                    }
                }

                app.HasResolvedWindow = true;
                app.WindowHandle = window;
                app.WindowResolved.TrySetResult(true);

                if (applyRestorePlacement)
                {
                    await ApplyRestorePlacementAsync(window, restoreDesktopId, restoreBounds, cancellation.Token);
                }

                Post(() =>
                {
                    if (!IsExiting)
                    {
                        UpdateWindowPlacement(app, window, restoreDesktopId, restoreBounds, save: true);
                    }
                });

                while (!IsExiting && !app.Process.HasExited)
                {
                    await Task.Delay(1000);
                    Post(() =>
                    {
                        if (apps.Contains(app) && !app.Process.HasExited)
                        {
                            if (app.Mode == MonitorMode.PlacementOnly && (window == IntPtr.Zero || !IsWindow(window) || !IsWindowVisible(window)))
                            {
                                AddPendingPlacementSnapshot(SavedApp.FromMonitoredApp(app));
                                Remove(app);
                                return;
                            }

                            UpdateWindowPlacement(app, window, app.DesktopId, app.WindowBounds, save: true);
                        }
                    });
                }
            }
            catch
            {
                app.WindowResolved.TrySetResult(false);
                // Virtual desktop placement uses Windows shell APIs that can fail across builds.
            }
        }

        private static async Task ApplyRestorePlacementAsync(
            IntPtr window,
            Guid? restoreDesktopId,
            WindowBounds? restoreBounds,
            CancellationToken cancellationToken)
        {
            foreach (var delay in RestorePlacementRetryDelaysMilliseconds)
            {
                if (delay > 0)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                if (restoreDesktopId is { } targetDesktopId)
                {
                    VirtualDesktopPlacement.TryMoveToDesktop(window, targetDesktopId);
                }

                TryMoveWindow(window, restoreBounds);
            }
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            sessionEnding = true;
            IsExiting = true;
            try
            {
                LogShutdownSnapshot(e.Reason);
                SaveSession();
            }
            catch (Exception ex)
            {
                LogException("session-ending-save", ex);
            }

            ExitThread();
        }
    }

    private sealed class MainForm : Form
    {
        private readonly ManagerContext context;
        private readonly ListView listView = new();
        private readonly ListView rulesView = new();
        private ImageList appIcons = CreateSmallIconList();
        private ImageList ruleIcons = CreateSmallIconList();
        private readonly Button addWindowButton = new();
        private readonly Button removeButton = new();
        private readonly CheckBox startupCheckBox = new();
        private readonly CheckBox explorerMenuCheckBox = new();
        private readonly CheckBox globalPlacementCheckBox = new();
        private readonly StatusStrip statusStrip = new();
        private readonly ToolStripStatusLabel statusLabel = new();
        private bool updatingStartupCheckBox;
        private bool updatingExplorerMenuCheckBox;
        private bool updatingGlobalPlacementCheckBox;

        public MainForm(ManagerContext context)
        {
            this.context = context;
            Text = "RestartableLaunch";
            Icon = CloneAppIcon();
            Width = 1040;
            Height = 560;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 420);
            BackColor = Color.FromArgb(248, 250, 252);

            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 112,
                Padding = new Padding(16, 12, 16, 10),
                BackColor = Color.White,
            };

            startupCheckBox.AutoSize = true;
            startupCheckBox.Text = "Start at user logon and restore monitored apps";
            startupCheckBox.Location = new Point(16, 12);
            startupCheckBox.CheckedChanged += (_, _) => ToggleStartup();
            topPanel.Controls.Add(startupCheckBox);

            explorerMenuCheckBox.AutoSize = true;
            explorerMenuCheckBox.Text = "RestartableLaunch in Explorer context menu";
            explorerMenuCheckBox.Location = new Point(16, 40);
            explorerMenuCheckBox.CheckedChanged += (_, _) => ToggleExplorerContextMenu();
            topPanel.Controls.Add(explorerMenuCheckBox);

            globalPlacementCheckBox.AutoSize = true;
            globalPlacementCheckBox.Text = "Keep placement for self-restoring windows";
            globalPlacementCheckBox.Location = new Point(16, 68);
            globalPlacementCheckBox.CheckedChanged += (_, _) => ToggleGlobalPlacementOnly();
            topPanel.Controls.Add(globalPlacementCheckBox);

            addWindowButton.Text = "+ Add Window";
            addWindowButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            addWindowButton.Size = new Size(124, 34);
            addWindowButton.FlatStyle = FlatStyle.System;
            addWindowButton.Location = new Point(topPanel.Width - addWindowButton.Width - 16, 39);
            addWindowButton.Click += (_, _) => AddOpenWindow();
            topPanel.Controls.Add(addWindowButton);

            removeButton.Text = "Remove";
            removeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            removeButton.Size = new Size(96, 34);
            removeButton.FlatStyle = FlatStyle.System;
            removeButton.Enabled = false;
            removeButton.Click += (_, _) => RemoveSelectedApps();
            topPanel.Controls.Add(removeButton);
            topPanel.Resize += (_, _) =>
            {
                addWindowButton.Location = new Point(topPanel.ClientSize.Width - addWindowButton.Width - 16, 39);
                removeButton.Location = new Point(addWindowButton.Left - removeButton.Width - 10, 39);
            };
            removeButton.Location = new Point(addWindowButton.Left - removeButton.Width - 10, 39);

            listView.Dock = DockStyle.Fill;
            listView.BorderStyle = BorderStyle.None;
            listView.View = View.Details;
            listView.FullRowSelect = true;
            listView.GridLines = false;
            listView.HideSelection = false;
            listView.MultiSelect = true;
            listView.SmallImageList = appIcons;
            listView.Columns.Add(string.Empty, 34);
            listView.Columns.Add("Process", 150);
            listView.Columns.Add("PID", 80);
            listView.Columns.Add("Mode", 110);
            listView.Columns.Add("Started", 150);
            listView.Columns.Add("Command", 486);
            listView.Columns.Add("Desktop", 240);
            listView.ColumnClick += (_, e) => SortListByColumn(e.Column);
            listView.SelectedIndexChanged += (_, _) => UpdateRemoveButtonState();
            listView.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Delete)
                {
                    RemoveSelectedApps();
                    e.Handled = true;
                }
            };
            listView.ContextMenuStrip = BuildListContextMenu();

            var rulesPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 132,
                Padding = new Padding(0, 8, 0, 0),
                BackColor = Color.FromArgb(248, 250, 252),
            };

            var rulesLabel = new Label
            {
                Text = "All-instance monitors",
                Dock = DockStyle.Top,
                Height = 24,
                Padding = new Padding(10, 4, 0, 0),
            };

            rulesView.Dock = DockStyle.Fill;
            rulesView.BorderStyle = BorderStyle.None;
            rulesView.View = View.Details;
            rulesView.FullRowSelect = true;
            rulesView.HideSelection = false;
            rulesView.SmallImageList = ruleIcons;
            rulesView.Columns.Add(string.Empty, 34);
            rulesView.Columns.Add("Process", 160);
            rulesView.Columns.Add("Mode", 120);
            rulesView.Columns.Add("Executable", 626);
            rulesView.SelectedIndexChanged += (_, _) => UpdateRemoveButtonState();
            rulesView.ColumnClick += (_, e) => SortListByColumn(rulesView, e.Column);
            rulesView.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Delete)
                {
                    RemoveSelectedApps();
                    e.Handled = true;
                }
            };
            rulesView.ContextMenuStrip = BuildRulesContextMenu();
            rulesPanel.Controls.Add(rulesView);
            rulesPanel.Controls.Add(rulesLabel);

            statusStrip.SizingGrip = false;
            statusStrip.BackColor = Color.White;
            statusStrip.Items.Add(statusLabel);

            Controls.Add(listView);
            Controls.Add(rulesPanel);
            Controls.Add(statusStrip);
            Controls.Add(topPanel);

            FormClosing += (_, e) =>
            {
                e.Cancel = true;
                Hide();
            };

            RefreshStartupState();
            RefreshExplorerContextMenuState();
            RefreshGlobalPlacementState();
        }

        public void RefreshApps()
        {
            RefreshGlobalPlacementState();
            var nextAppIcons = CreateSmallIconList();
            var appItems = new List<ListViewItem>();

            foreach (var app in context.Apps)
            {
                var imageKey = $"app-{app.Process.Id}";
                var imageIndex = nextAppIcons.Images.Count;
                using (var icon = TryGetProcessIcon(app.Process))
                {
                    AddIconImage(nextAppIcons, imageKey, icon);
                }

                var item = new ListViewItem(string.Empty)
                {
                    ImageIndex = imageIndex,
                };
                item.SubItems.Add(app.Process.ProcessName);
                item.SubItems.Add(app.Process.Id.ToString());
                item.SubItems.Add(FormatMonitorMode(app));
                item.SubItems.Add(app.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"));
                item.SubItems.Add(FormatRequest(app.Request));
                item.SubItems.Add(app.DesktopId?.ToString() ?? "unknown");
                item.Tag = app;
                appItems.Add(item);
            }

            foreach (var snapshot in context.PendingPlacementOnlyApps)
            {
                var processName = !string.IsNullOrWhiteSpace(snapshot.ProcessName)
                    ? snapshot.ProcessName
                    : Path.GetFileNameWithoutExtension(snapshot.Executable);
                var imageKey = $"pending-{snapshot.GetHashCode()}";
                var imageIndex = nextAppIcons.Images.Count;
                using (var icon = TryGetRuleIcon(new ProcessMonitorRule(Guid.Empty, snapshot.Executable, processName, MonitorMode.PlacementOnly)))
                {
                    AddIconImage(nextAppIcons, imageKey, icon);
                }

                var item = new ListViewItem(string.Empty)
                {
                    ImageIndex = imageIndex,
                };
                item.SubItems.Add(processName);
                item.SubItems.Add("pending");
                item.SubItems.Add("Placement pending");
                item.SubItems.Add(snapshot.StartedAt == default ? string.Empty : snapshot.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"));
                item.SubItems.Add(FormatRequest(snapshot.ToLaunchRequest()));
                item.SubItems.Add(snapshot.DesktopId?.ToString() ?? "unknown");
                item.Tag = snapshot;
                appItems.Add(item);
            }

            var nextRuleIcons = CreateSmallIconList();
            var ruleItems = new List<ListViewItem>();
            foreach (var rule in context.MonitorRules)
            {
                var imageKey = $"rule-{rule.Id}";
                var imageIndex = nextRuleIcons.Images.Count;
                using (var icon = TryGetRuleIcon(rule))
                {
                    AddIconImage(nextRuleIcons, imageKey, icon);
                }

                var item = new ListViewItem(string.Empty)
                {
                    ImageIndex = imageIndex,
                };
                item.SubItems.Add(rule.ProcessName);
                item.SubItems.Add(rule.Mode == MonitorMode.PlacementOnly ? "Placement only" : "Restart");
                item.SubItems.Add(string.IsNullOrWhiteSpace(rule.ExecutablePath) ? "(process name only)" : rule.ExecutablePath);
                item.Tag = rule;
                ruleItems.Add(item);
            }

            var oldAppIcons = appIcons;
            var oldRuleIcons = ruleIcons;
            appIcons = nextAppIcons;
            ruleIcons = nextRuleIcons;

            listView.BeginUpdate();
            rulesView.BeginUpdate();
            try
            {
                listView.SmallImageList = null;
                rulesView.SmallImageList = null;
                listView.Items.Clear();
                rulesView.Items.Clear();
                listView.SmallImageList = appIcons;
                rulesView.SmallImageList = ruleIcons;
                listView.Items.AddRange(appItems.ToArray());
                rulesView.Items.AddRange(ruleItems.ToArray());
            }
            finally
            {
                listView.EndUpdate();
                rulesView.EndUpdate();
                oldAppIcons.Dispose();
                oldRuleIcons.Dispose();
            }

            UpdateRemoveButtonState();
            var totalApps = context.Apps.Count + context.PendingPlacementOnlyApps.Count;
            statusLabel.Text = totalApps == 1
                ? $"1 monitored app, {context.MonitorRules.Count} watch rules"
                : $"{totalApps} monitored apps, {context.MonitorRules.Count} watch rules";
        }

        private static string FormatMonitorMode(MonitoredApp app)
        {
            return app.Mode == MonitorMode.PlacementOnly
                ? "Placement only"
                : app.RuleId is null ? "Restart" : "All instances";
        }

        private void SortListByColumn(int column)
        {
            SortListByColumn(listView, column);
        }

        private static void SortListByColumn(ListView target, int column)
        {
            var sorter = target.ListViewItemSorter as ListViewColumnSorter;
            if (sorter is null)
            {
                sorter = new ListViewColumnSorter();
                target.ListViewItemSorter = sorter;
            }

            if (sorter.Column == column)
            {
                sorter.Descending = !sorter.Descending;
            }
            else
            {
                sorter.Column = column;
                sorter.Descending = false;
            }

            target.Sort();
        }

        private ContextMenuStrip BuildListContextMenu()
        {
            var menu = new ContextMenuStrip();
            var bringToFrontItem = new ToolStripMenuItem("Bring to front");
            var monitorAllItem = new ToolStripMenuItem();
            var placementOnlyItem = new ToolStripMenuItem();
            var separator = new ToolStripSeparator();
            menu.Opening += (_, e) =>
            {
                e.Cancel = listView.SelectedItems.Count == 0;
                if (e.Cancel)
                {
                    return;
                }

                var selectedApp = GetSelectedMonitoredApp();
                bringToFrontItem.Visible = selectedApp is not null;
                monitorAllItem.Visible = selectedApp is not null && selectedApp.RuleId is null;
                placementOnlyItem.Visible = selectedApp is not null && selectedApp.Mode != MonitorMode.PlacementOnly;
                separator.Visible = selectedApp is not null;
                monitorAllItem.Text = selectedApp is null
                    ? "Monitor all instances"
                    : $"Monitor all instances of {selectedApp.Process.ProcessName}.exe";
                placementOnlyItem.Text = selectedApp is null
                    ? "Keep placement only"
                    : $"Keep placement only for all instances of {selectedApp.Process.ProcessName}.exe";
            };
            bringToFrontItem.Click += (_, _) => BringSelectedMonitoredAppToFront();
            monitorAllItem.Click += (_, _) =>
            {
                if (GetSelectedMonitoredApp() is { } app)
                {
                    context.MonitorAllInstances(app);
                }
            };
            placementOnlyItem.Click += (_, _) =>
            {
                if (GetSelectedMonitoredApp() is { } app)
                {
                    context.MonitorPlacementOnly(app);
                }
            };
            menu.Items.Add(bringToFrontItem);
            menu.Items.Add(monitorAllItem);
            menu.Items.Add(placementOnlyItem);
            menu.Items.Add(separator);
            menu.Items.Add("Remove", null, (_, _) => RemoveSelectedApps());
            return menu;
        }

        private ContextMenuStrip BuildRulesContextMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Opening += (_, e) =>
            {
                e.Cancel = rulesView.SelectedItems.Count == 0;
            };
            menu.Items.Add("Remove", null, (_, _) => RemoveSelectedApps());
            return menu;
        }

        private void UpdateRemoveButtonState()
        {
            removeButton.Enabled = listView.SelectedItems.Count > 0 || rulesView.SelectedItems.Count > 0;
        }

        private void RemoveSelectedApps()
        {
            var selectedItems = listView.SelectedItems
                .Cast<ListViewItem>()
                .Select(static item => item.Tag)
                .Concat(rulesView.SelectedItems.Cast<ListViewItem>().Select(static item => item.Tag))
                .OfType<object>()
                .ToArray();

            if (selectedItems.Length == 0)
            {
                return;
            }

            context.RemoveItems(selectedItems);
        }

        private MonitoredApp? GetSelectedMonitoredApp()
        {
            return listView.SelectedItems
                .Cast<ListViewItem>()
                .Select(static item => item.Tag)
                .OfType<MonitoredApp>()
                .FirstOrDefault();
        }

        private void BringSelectedMonitoredAppToFront()
        {
            if (GetSelectedMonitoredApp() is not { } app)
            {
                return;
            }

            try
            {
                if (app.Process.HasExited)
                {
                    return;
                }

                var window = app.WindowHandle != IntPtr.Zero
                    ? app.WindowHandle
                    : FindTopLevelWindow(app.Process.Id);
                if (window != IntPtr.Zero)
                {
                    app.WindowHandle = window;
                    app.HasResolvedWindow = true;
                    TryBringWindowToFront(window);
                }
            }
            catch
            {
                // Best-effort UI helper only.
            }
        }

        private void RefreshExplorerContextMenuState()
        {
            updatingExplorerMenuCheckBox = true;
            try
            {
                explorerMenuCheckBox.Checked = ExplorerContextMenu.IsRegistered();
            }
            finally
            {
                updatingExplorerMenuCheckBox = false;
            }
        }

        private void RefreshGlobalPlacementState()
        {
            updatingGlobalPlacementCheckBox = true;
            try
            {
                globalPlacementCheckBox.Checked = context.GlobalPlacementOnly;
            }
            finally
            {
                updatingGlobalPlacementCheckBox = false;
            }
        }

        private void AddOpenWindow()
        {
            using var dialog = new WindowPickerForm();
            if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedWindow is { } selectedWindow)
            {
                if (dialog.PlacementOnly)
                {
                    context.AdoptWindowPlacementOnly(selectedWindow);
                }
                else
                {
                    context.AdoptWindow(selectedWindow, dialog.MonitorAllInstances);
                }
            }
        }

        private void RefreshStartupState()
        {
            updatingStartupCheckBox = true;
            try
            {
                startupCheckBox.Checked = LoginStartup.IsRegistered();
            }
            finally
            {
                updatingStartupCheckBox = false;
            }
        }

        private void ToggleStartup()
        {
            if (updatingStartupCheckBox)
            {
                return;
            }

            try
            {
                if (startupCheckBox.Checked)
                {
                    LoginStartup.Register();
                }
                else
                {
                    LoginStartup.Unregister();
                }
            }
            catch (Exception ex)
            {
                MessageBox(IntPtr.Zero, ex.Message, "RestartableLaunch", 0x10);
            }
            finally
            {
                RefreshStartupState();
            }
        }

        private void ToggleExplorerContextMenu()
        {
            if (updatingExplorerMenuCheckBox)
            {
                return;
            }

            try
            {
                if (explorerMenuCheckBox.Checked)
                {
                    ExplorerContextMenu.Register();
                }
                else
                {
                    ExplorerContextMenu.Unregister();
                }
            }
            catch (Exception ex)
            {
                MessageBox(IntPtr.Zero, ex.Message, "RestartableLaunch", 0x10);
            }
            finally
            {
                RefreshExplorerContextMenuState();
            }
        }

        private void ToggleGlobalPlacementOnly()
        {
            if (updatingGlobalPlacementCheckBox)
            {
                return;
            }

            context.SetGlobalPlacementOnly(globalPlacementCheckBox.Checked);
        }
    }

    private sealed class WindowPickerForm : Form
    {
        private readonly ListView listView = new();
        private readonly ImageList windowIcons = new();
        private readonly CheckBox monitorAllInstancesCheckBox = new();
        private readonly Button okButton = new();
        private readonly Button cancelButton = new();
        private readonly List<WindowCandidate> candidates;

        public WindowPickerForm()
        {
            Text = "Add Open Window";
            Icon = CloneAppIcon();
            Width = 820;
            Height = 420;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;

            candidates = GetOpenWindowCandidates();
            windowIcons.ColorDepth = ColorDepth.Depth32Bit;
            windowIcons.ImageSize = new Size(16, 16);

            listView.Dock = DockStyle.Fill;
            listView.View = View.Details;
            listView.FullRowSelect = true;
            listView.GridLines = false;
            listView.SmallImageList = windowIcons;
            listView.Columns.Add(string.Empty, 34);
            listView.Columns.Add("Process", 160);
            listView.Columns.Add("PID", 80);
            listView.Columns.Add("Window title", 450);
            listView.DoubleClick += (_, _) => AcceptSelection();
            listView.ContextMenuStrip = BuildContextMenu();

            foreach (var candidate in candidates)
            {
                var imageKey = $"window-{candidate.Handle.ToInt64()}";
                using (var icon = TryGetWindowIcon(candidate.Handle, candidate.ProcessId))
                {
                    AddIconImage(windowIcons, imageKey, icon);
                }

                var item = new ListViewItem(string.Empty)
                {
                    ImageKey = imageKey,
                };

                item.SubItems.Add(candidate.ProcessName);
                item.SubItems.Add(candidate.ProcessId.ToString());
                item.SubItems.Add(candidate.Title);
                item.Tag = candidate;
                listView.Items.Add(item);
            }

            var buttonPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                ColumnCount = 4,
                RowCount = 1,
                Padding = new Padding(12, 8, 12, 8),
            };
            buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
            buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            buttonPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            monitorAllInstancesCheckBox.Text = "Monitor all instances of this app";
            monitorAllInstancesCheckBox.AutoSize = true;
            monitorAllInstancesCheckBox.Anchor = AnchorStyles.Left;
            monitorAllInstancesCheckBox.Margin = new Padding(0);

            okButton.Text = "Add";
            okButton.Dock = DockStyle.Fill;
            okButton.Margin = new Padding(0);
            okButton.Click += (_, _) => AcceptSelection();
            cancelButton.Text = "Cancel";
            cancelButton.Dock = DockStyle.Fill;
            cancelButton.Margin = new Padding(0);
            cancelButton.Click += (_, _) => DialogResult = DialogResult.Cancel;

            buttonPanel.Controls.Add(monitorAllInstancesCheckBox, 0, 0);
            buttonPanel.Controls.Add(cancelButton, 1, 0);
            buttonPanel.Controls.Add(okButton, 3, 0);

            Controls.Add(listView);
            Controls.Add(buttonPanel);

            if (listView.Items.Count > 0)
            {
                listView.Items[0].Selected = true;
            }
        }

        public WindowCandidate? SelectedWindow { get; private set; }

        public bool MonitorAllInstances => monitorAllInstancesCheckBox.Checked;

        public bool PlacementOnly { get; private set; }

        private ContextMenuStrip BuildContextMenu()
        {
            var menu = new ContextMenuStrip();
            var monitorAllItem = new ToolStripMenuItem();
            var placementOnlyItem = new ToolStripMenuItem();
            menu.Opening += (_, e) =>
            {
                e.Cancel = listView.SelectedItems.Count == 0;
                if (e.Cancel)
                {
                    return;
                }

                var candidate = GetSelectedCandidate();
                monitorAllItem.Text = candidate is null
                    ? "Monitor all instances"
                    : $"Monitor all instances of {candidate.ProcessName}.exe";
                placementOnlyItem.Text = candidate is null
                    ? "Keep placement only"
                    : $"Keep placement only for all instances of {candidate.ProcessName}.exe";
            };

            monitorAllItem.Click += (_, _) =>
            {
                if (GetSelectedCandidate() is null)
                {
                    return;
                }

                monitorAllInstancesCheckBox.Checked = true;
                AcceptSelection();
            };

            placementOnlyItem.Click += (_, _) =>
            {
                if (GetSelectedCandidate() is null)
                {
                    return;
                }

                PlacementOnly = true;
                AcceptSelection();
            };

            menu.Items.Add(monitorAllItem);
            menu.Items.Add(placementOnlyItem);
            return menu;
        }

        private void AcceptSelection()
        {
            if (listView.SelectedItems.Count == 0)
            {
                return;
            }

            if (listView.SelectedItems[0].Tag is not WindowCandidate candidate)
            {
                return;
            }

            SelectedWindow = candidate;
            DialogResult = DialogResult.OK;
        }

        private WindowCandidate? GetSelectedCandidate()
        {
            return listView.SelectedItems.Count > 0 && listView.SelectedItems[0].Tag is WindowCandidate candidate
                ? candidate
                : null;
        }
    }

    private sealed class ListViewColumnSorter : System.Collections.IComparer
    {
        public int Column { get; set; }

        public bool Descending { get; set; }

        public int Compare(object? x, object? y)
        {
            if (x is not ListViewItem left || y is not ListViewItem right)
            {
                return 0;
            }

            var leftText = Column < left.SubItems.Count ? left.SubItems[Column].Text : string.Empty;
            var rightText = Column < right.SubItems.Count ? right.SubItems[Column].Text : string.Empty;
            var result = CompareText(leftText, rightText);
            return Descending ? -result : result;
        }

        private static int CompareText(string left, string right)
        {
            if (int.TryParse(left, out var leftNumber) && int.TryParse(right, out var rightNumber))
            {
                return leftNumber.CompareTo(rightNumber);
            }

            return string.Compare(left, right, StringComparison.CurrentCultureIgnoreCase);
        }
    }

    private sealed record AppCommand(LaunchRequest? Launch, bool Restore, CommandMode Mode)
    {
        public static AppCommand Parse(string[] args)
        {
            if (args.Length == 0)
            {
                return new AppCommand(null, false, CommandMode.ShowGui);
            }

            if (Is(args[0], "--restore"))
            {
                return new AppCommand(null, true, CommandMode.Launch);
            }

            if (Is(args[0], "--gui"))
            {
                return new AppCommand(null, false, CommandMode.ShowGui);
            }

            var executableIndex = 0;
            var launchKind = LaunchKind.DefaultOpen;
            while (executableIndex < args.Length)
            {
                if (args[executableIndex] == "--")
                {
                    executableIndex++;
                    break;
                }

                if (Is(args[executableIndex], "--run") || Is(args[executableIndex], "-r"))
                {
                    launchKind = LaunchKind.Executable;
                    executableIndex++;
                    continue;
                }

                if (!args[executableIndex].StartsWith('-'))
                {
                    break;
                }

                executableIndex++;
            }

            if (executableIndex >= args.Length)
            {
                return new AppCommand(null, false, CommandMode.ShowGui);
            }

            var request = launchKind == LaunchKind.Executable
                ? new LaunchRequest(args[executableIndex], args.Skip(executableIndex + 1).ToArray(), null, LaunchKind.Executable, null)
                : new LaunchRequest(args[executableIndex], [], null, LaunchKind.DefaultOpen, null);

            return new AppCommand(request, false, CommandMode.Launch);
        }

        private static bool Is(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

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

    private sealed record SavedSession(SavedApp[] Apps, ProcessMonitorRule[]? Rules = null, bool GlobalPlacementOnly = false);

    private sealed record SavedApp(
        int ProcessId,
        DateTimeOffset StartedAt,
        string Executable,
        string[] Arguments,
        Guid? DesktopId,
        LaunchKind Kind,
        WindowBounds? WindowBounds,
        Guid? RuleId = null,
        MonitorMode Mode = MonitorMode.Restart,
        string? ProcessName = null,
        string? WindowTitle = null,
        string? WindowClass = null)
    {
        public static SavedApp FromMonitoredApp(MonitoredApp app)
        {
            return new SavedApp(
                app.Process.Id,
                app.StartedAt,
                app.Request.Executable,
                app.Request.Arguments,
                app.DesktopId,
                app.Request.Kind,
                app.WindowBounds,
                app.RuleId,
                app.Mode,
                TryGetProcessName(app.Process),
                app.WindowTitle,
                app.WindowClass);
        }

        public LaunchRequest ToLaunchRequest()
        {
            return new LaunchRequest(Executable, Arguments ?? [], DesktopId, Kind, WindowBounds);
        }
    }

    private sealed record ActiveState(ActiveAppState[] Apps);

    private sealed record ActiveAppState(int ProcessId, DateTimeOffset StartedAt, LaunchRequest Request, Guid? DesktopId, WindowBounds? WindowBounds);

    private enum MonitorMode
    {
        Restart,
        PlacementOnly,
    }

    private sealed record ProcessMonitorRule(Guid Id, string ExecutablePath, string ProcessName, MonitorMode Mode = MonitorMode.Restart);

    private sealed class MonitoredApp(LaunchRequest request, Process process, DateTimeOffset startedAt, Guid? desktopId, WindowBounds? windowBounds, Guid? ruleId, MonitorMode mode = MonitorMode.Restart)
    {
        public LaunchRequest Request { get; set; } = request;

        public Process Process { get; set; } = process;

        public DateTimeOffset StartedAt { get; set; } = startedAt;

        public Guid? DesktopId { get; set; } = desktopId;

        public WindowBounds? WindowBounds { get; set; } = windowBounds;

        public Guid? RuleId { get; set; } = ruleId;

        public MonitorMode Mode { get; set; } = mode;

        public IntPtr WindowHandle { get; set; }

        public string WindowTitle { get; set; } = string.Empty;

        public string WindowClass { get; set; } = string.Empty;

        public bool HasResolvedWindow { get; set; }

        public TaskCompletionSource<bool> WindowResolved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid? PendingRestoreDesktopId { get; set; }

        public WindowBounds? PendingRestoreWindowBounds { get; set; }

        public DateTimeOffset ProtectRestorePlacementUntil { get; set; }

        public void ClearPendingRestorePlacement()
        {
            PendingRestoreDesktopId = null;
            PendingRestoreWindowBounds = null;
            ProtectRestorePlacementUntil = default;
        }
    }

    private enum WindowShowState
    {
        Normal,
        Minimized,
        Maximized,
    }

    private sealed record WindowBounds(int Left, int Top, int Width, int Height, WindowShowState State = WindowShowState.Normal);

    private sealed record WindowCandidate(IntPtr Handle, int ProcessId, string ProcessName, string Title, string WindowClass);
}
