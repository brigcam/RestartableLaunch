using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace RestartableLaunch;

internal static partial class Program
{
    private const int RestartNoCrash = 1;
    private const int RestartNoHang = 2;
    private const string MutexName = @"Local\RestartableLaunch.Manager";
    private const string PipeName = "RestartableLaunch.Manager";
    private const int SwHide = 0;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var command = AppCommand.Parse(args);
        using var mutex = new Mutex(false, MutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            var response = SendToExistingInstance(command);
            if (command.Mode == CommandMode.List)
            {
                Console.WriteLine(response.Length == 0 ? "RestartableLaunch is running, but no response was returned." : response);
            }

            return 0;
        }

        if (command.Mode == CommandMode.List)
        {
            Console.WriteLine("No applications are currently monitored by RestartableLaunch.");
            return 0;
        }

        HideConsoleWindow();

        var context = new ManagerContext();
        RegisterApplicationRestart("--restore", RestartNoCrash | RestartNoHang);
        _ = StartPipeServer(context);

        if (command.Restore)
        {
            context.RestoreSavedSession();
        }
        else if (command.Launch is not null)
        {
            context.Launch(command.Launch);
        }

        if (command.Mode == CommandMode.ShowGui || command.Launch is null)
        {
            context.ShowMainWindow();
        }

        Application.Run(context);
        UnregisterApplicationRestart();
        return 0;
    }

    private static async Task StartPipeServer(ManagerContext context)
    {
        while (!context.IsExiting)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync();
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var json = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(json))
                {
                    continue;
                }

                var command = JsonSerializer.Deserialize<AppCommand>(json, JsonOptions);
                if (command is null)
                {
                    continue;
                }

                if (command.Mode == CommandMode.List)
                {
                    using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
                    writer.Write(FormatApps(context.Apps));
                    continue;
                }

                context.Post(() =>
                {
                    if (command.Launch is not null)
                    {
                        context.Launch(command.Launch);
                    }

                    if (command.Mode == CommandMode.ShowGui || command.Launch is null)
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
            using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(command, JsonOptions));
            pipe.WaitForPipeDrain();

            if (command.Mode != CommandMode.List)
            {
                return string.Empty;
            }

            using var reader = new StreamReader(pipe, Encoding.UTF8);
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
            builder.AppendLine($"Command: {FormatCommand(app.Request.Executable, app.Request.Arguments)}");
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

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterApplicationRestart(string commandLineArgs, int flags);

    [LibraryImport("kernel32.dll")]
    private static partial int UnregisterApplicationRestart();

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private sealed class ManagerContext : ApplicationContext
    {
        private readonly NotifyIcon notifyIcon;
        private readonly MainForm mainForm;
        private readonly List<MonitoredApp> apps = [];
        private readonly SynchronizationContext uiContext;
        private bool sessionEnding;

        public ManagerContext()
        {
            uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            mainForm = new MainForm(this);

            notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "RestartableLaunch",
                Visible = true,
                ContextMenuStrip = new ContextMenuStrip(),
            };
            notifyIcon.ContextMenuStrip.Items.Add("Open", null, (_, _) => ShowMainWindow());
            notifyIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => ExitThread());
            notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

            SystemEvents.SessionEnding += OnSessionEnding;
        }

        public IReadOnlyList<MonitoredApp> Apps => apps;

        public bool IsExiting { get; private set; }

        public void Post(Action action)
        {
            uiContext.Post(_ => action(), null);
        }

        public void Launch(LaunchRequest request)
        {
            try
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

                var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The child process could not be started.");
                process.EnableRaisingEvents = true;

                var app = new MonitoredApp(request, process, DateTimeOffset.Now);
                apps.Add(app);
                process.Exited += (_, _) => Post(() => Remove(app));

                SaveSession();
                mainForm.RefreshApps();
            }
            catch (Exception ex)
            {
                MessageBox(IntPtr.Zero, ex.Message, "RestartableLaunch", 0x10);
            }
        }

        public void RestoreSavedSession()
        {
            if (!File.Exists(SessionPath))
            {
                return;
            }

            try
            {
                var session = JsonSerializer.Deserialize<SavedSession>(File.ReadAllText(SessionPath), JsonOptions);
                if (session is null)
                {
                    return;
                }

                foreach (var request in session.Apps)
                {
                    Launch(request);
                }
            }
            catch (Exception ex)
            {
                MessageBox(IntPtr.Zero, ex.Message, "RestartableLaunch restore", 0x10);
            }
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
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            mainForm.Dispose();
            SystemEvents.SessionEnding -= OnSessionEnding;

            if (!sessionEnding)
            {
                TryDelete(SessionPath);
            }

            base.ExitThreadCore();
        }

        private static string GetWorkingDirectory(string executable)
        {
            var fullPath = Path.GetFullPath(executable);
            var directory = Path.GetDirectoryName(fullPath);
            return string.IsNullOrWhiteSpace(directory) ? Environment.CurrentDirectory : directory;
        }

        private void Remove(MonitoredApp app)
        {
            apps.Remove(app);
            app.Process.Dispose();
            SaveSession();
            mainForm.RefreshApps();
        }

        private void SaveSession()
        {
            var session = new SavedSession(apps.Select(static app => app.Request).ToArray());
            File.WriteAllText(SessionPath, JsonSerializer.Serialize(session, JsonOptions));
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            sessionEnding = true;
            SaveSession();
        }
    }

    private sealed class MainForm : Form
    {
        private readonly ManagerContext context;
        private readonly ListView listView = new();

        public MainForm(ManagerContext context)
        {
            this.context = context;
            Text = "RestartableLaunch";
            Width = 900;
            Height = 420;
            StartPosition = FormStartPosition.CenterScreen;

            listView.Dock = DockStyle.Fill;
            listView.View = View.Details;
            listView.FullRowSelect = true;
            listView.GridLines = true;
            listView.Columns.Add("PID", 80);
            listView.Columns.Add("Started", 150);
            listView.Columns.Add("Command line", 620);
            Controls.Add(listView);

            FormClosing += (_, e) =>
            {
                e.Cancel = true;
                Hide();
            };
        }

        public void RefreshApps()
        {
            listView.BeginUpdate();
            listView.Items.Clear();

            foreach (var app in context.Apps)
            {
                var item = new ListViewItem(app.Process.Id.ToString());
                item.SubItems.Add(app.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"));
                item.SubItems.Add(FormatCommand(app.Request.Executable, app.Request.Arguments));
                listView.Items.Add(item);
            }

            listView.EndUpdate();
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

            if (Is(args[0], "--list") || Is(args[0], "-l"))
            {
                return new AppCommand(null, false, CommandMode.List);
            }

            if (Is(args[0], "--gui"))
            {
                return new AppCommand(null, false, CommandMode.ShowGui);
            }

            var executableIndex = 0;
            while (executableIndex < args.Length)
            {
                if (args[executableIndex] == "--")
                {
                    executableIndex++;
                    break;
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

            var request = new LaunchRequest(args[executableIndex], args.Skip(executableIndex + 1).ToArray());
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

    private sealed record LaunchRequest(string Executable, string[] Arguments);

    private sealed record SavedSession(LaunchRequest[] Apps);

    private sealed record MonitoredApp(LaunchRequest Request, Process Process, DateTimeOffset StartedAt);
}
