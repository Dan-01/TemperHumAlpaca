using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;

internal static class WindowsServiceSupport
{
    public const string ServiceName = "TemperHumAlpaca";
    public const string DisplayName = "TemperHumAlpaca ASCOM Alpaca Bridge";
    private const string Description = "ASCOM Alpaca ObservingConditions bridge for PCsensor TEMPerHUM/TEMPerX USB sensors.";

    public static int Run(Func<CancellationToken, Task<int>> server)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Windows service mode is only available on Windows.");
            return 1;
        }

        ServiceBase.Run(new TemperHumWindowsService(server));
        return 0;
    }

    public static async Task<int> InstallAsync()
    {
        if (!EnsureWindowsAndAdministrator())
        {
            return 1;
        }

        var sourceExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(sourceExe) || !File.Exists(sourceExe))
        {
            Console.Error.WriteLine("Could not determine the current executable path.");
            return 1;
        }

        var installDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "TemperHumAlpaca");
        Directory.CreateDirectory(installDirectory);

        var targetExe = Path.Combine(installDirectory, "TemperHumAlpaca.exe");
        var targetConfig = Path.Combine(installDirectory, "temperhum.json");
        var sourceConfig = Path.Combine(AppContext.BaseDirectory, "temperhum.json");

        var exists = ServiceExists();
        if (exists)
        {
            await StopServiceIfRunningAsync();
        }

        if (!Path.GetFullPath(sourceExe).Equals(Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourceExe, targetExe, overwrite: true);
        }

        // Preserve the installed configuration (including calibration and UniqueID)
        // during upgrades. Seed it only on first installation.
        if (!File.Exists(targetConfig) && File.Exists(sourceConfig))
        {
            File.Copy(sourceConfig, targetConfig);
        }

        var serviceCommand = $"\"{targetExe}\" --service";
        if (exists)
        {
            RunSc("config", ServiceName, "binPath=", serviceCommand, "start=", "auto", "DisplayName=", DisplayName);
        }
        else
        {
            RunSc("create", ServiceName, "binPath=", serviceCommand, "start=", "auto", "DisplayName=", DisplayName);
        }

        RunSc("description", ServiceName, Description);
        RunSc("failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000");
        RunSc("failureflag", ServiceName, "1");
        RunSc("start", ServiceName);

        Console.WriteLine($"Installed and started Windows service '{DisplayName}'.");
        Console.WriteLine($"Files: {installDirectory}");
        Console.WriteLine("The service is configured to start automatically with Windows.");
        Console.WriteLine("Configuration is preserved in C:\\ProgramData\\TemperHumAlpaca\\temperhum.json during upgrades.");
        return 0;
    }

    public static async Task<int> UninstallAsync()
    {
        if (!EnsureWindowsAndAdministrator())
        {
            return 1;
        }

        if (!ServiceExists())
        {
            Console.WriteLine($"Windows service '{ServiceName}' is not installed.");
            return 0;
        }

        await StopServiceIfRunningAsync();
        RunSc("delete", ServiceName);
        Console.WriteLine($"Removed Windows service '{ServiceName}'.");
        Console.WriteLine("C:\\ProgramData\\TemperHumAlpaca is intentionally retained so calibration and the Alpaca UniqueID are not lost.");
        return 0;
    }

    public static int PrintStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Windows service status is only available on Windows.");
            return 1;
        }

        if (!ServiceExists())
        {
            Console.WriteLine($"{ServiceName}: not installed");
            return 0;
        }

        using var controller = new ServiceController(ServiceName);
        Console.WriteLine($"{ServiceName}: {controller.Status}");
        Console.WriteLine($"Startup files: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TemperHumAlpaca")}");
        return 0;
    }

    private static bool EnsureWindowsAndAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This command is only available on Windows.");
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.Error.WriteLine("This command must be run from PowerShell or Command Prompt opened as Administrator.");
            return false;
        }

        return true;
    }

    private static bool ServiceExists()
    {
        return ServiceController.GetServices()
            .Any(service => service.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task StopServiceIfRunningAsync()
    {
        using var controller = new ServiceController(ServiceName);
        controller.Refresh();
        if (controller.Status == ServiceControllerStatus.Stopped)
        {
            return;
        }

        if (controller.Status != ServiceControllerStatus.StopPending)
        {
            controller.Stop();
        }

        await Task.Run(() => controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)));
    }

    private static void RunSc(params string[] args)
    {
        var startInfo = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start sc.exe.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"sc.exe {string.Join(' ', args)} failed ({process.ExitCode}): {detail.Trim()}");
        }
    }

    private sealed class TemperHumWindowsService : ServiceBase
    {
        private readonly Func<CancellationToken, Task<int>> _server;
        private CancellationTokenSource? _cts;
        private Task<int>? _serverTask;

        public TemperHumWindowsService(Func<CancellationToken, Task<int>> server)
        {
            _server = server;
            ServiceName = WindowsServiceSupport.ServiceName;
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            _cts = new CancellationTokenSource();
            _serverTask = Task.Run(() => _server(_cts.Token));
        }

        protected override void OnStop() => StopServer();

        protected override void OnShutdown() => StopServer();

        private void StopServer()
        {
            _cts?.Cancel();
            if (_serverTask is not null)
            {
                try
                {
                    _serverTask.Wait(TimeSpan.FromSeconds(20));
                }
                catch (AggregateException aggregate) when (aggregate.InnerExceptions.All(e => e is OperationCanceledException or TaskCanceledException))
                {
                }
            }

            _cts?.Dispose();
            _cts = null;
            _serverTask = null;
        }
    }
}
