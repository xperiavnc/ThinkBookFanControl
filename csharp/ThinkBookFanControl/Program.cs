using System;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Windows;

namespace ThinkBookFanControl;

public static class Program
{
    private const string LenovoLegionToolkitDir = @"C:\Program Files\LenovoLegionToolkit";

    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveLenovoLegionToolkitAssembly;
            AppDomain.CurrentDomain.UnhandledException += (_, args) => LogException(args.ExceptionObject as Exception);

            var app = new Application
            {
                ShutdownMode = ShutdownMode.OnMainWindowClose
            };
            app.DispatcherUnhandledException += (_, args) =>
            {
                LogException(args.Exception);
                MessageBox.Show(args.Exception.ToString(), "ThinkBook Fan Control error", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
            var startToTrayRequested = args.Any(arg => string.Equals(arg, "--startup-tray", StringComparison.OrdinalIgnoreCase));
            app.Run(new MainWindow(startToTrayRequested));
        }
        catch (Exception ex)
        {
            LogException(ex);
            MessageBox.Show(ex.ToString(), "ThinkBook Fan Control startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static Assembly? ResolveLenovoLegionToolkitAssembly(object? sender, ResolveEventArgs args)
    {
        var assemblyName = new AssemblyName(args.Name).Name;
        if (string.IsNullOrWhiteSpace(assemblyName))
            return null;

        var localPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        if (File.Exists(localPath))
            return Assembly.LoadFrom(localPath);

        var lenovoPath = Path.Combine(LenovoLegionToolkitDir, assemblyName + ".dll");
        return File.Exists(lenovoPath) ? Assembly.LoadFrom(lenovoPath) : null;
    }

    private static void LogException(Exception? exception)
    {
        if (exception is null)
            return;

        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".thinkbook_fan_control");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "csharp-crash.log");
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}]\r\n{exception}\r\n\r\n");
        }
        catch
        {
            // Last-ditch logging must not create a second crash.
        }
    }
}
