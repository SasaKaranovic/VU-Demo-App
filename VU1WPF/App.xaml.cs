using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;

namespace VU1WPF
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private const string SingleInstanceMutexName = "Local\\KaranovicResearch.VU1-Demo-App";
        private Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppPaths.EnsureDataDirs();
            ConfigureFonts();
            ConfigureLogging();
            RegisterGlobalExceptionHandlers();

            // Only one instance may drive the dials, otherwise two apps fight over the same dial values.
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                Log.Warning("Another instance is already running. Exiting.");
                MessageBox.Show("VU1 Demo App is already running.\nCheck the system tray.",
                                "VU1 Demo App", MessageBoxButton.OK, MessageBoxImage.Information);
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                Shutdown(0);
                return;
            }

            Log.Information("VU1 Demo App starting. Build {Build}, runtime {Runtime}",
                            VU1WPF.Properties.Resources.BuildDate.Trim(), Environment.Version);

            try
            {
                var window = new MainWindow();
                MainWindow = window;
                window.Show();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Failed to create main window");
                MessageBox.Show("VU1 Demo App failed to start:\n\n" + ex.Message + "\n\nSee log file:\n" + AppPaths.LogFile,
                                "VU1 Demo App", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Information("VU1 Demo App exiting with code {Code}", e.ApplicationExitCode);
            Log.CloseAndFlush();

            if (_singleInstanceMutex != null)
            {
                try { _singleInstanceMutex.ReleaseMutex(); } catch { /* not owned */ }
                _singleInstanceMutex.Dispose();
            }

            base.OnExit(e);
        }

        // Font stacks: bundled fonts first (compiled in from /Fonts), then Windows fonts as fallback.
        // Change these to restyle the whole app.
        private const string UiFontStack = "./Fonts/#Inter, Segoe UI Variable Text, Segoe UI";
        private const string DisplayFontStack = "./Fonts/#JetBrains Mono, Cascadia Mono, Consolas";
        private const string MonoFontStack = "./Fonts/#JetBrains Mono, Cascadia Mono, Consolas";

        /// <summary>Replaces the theme's fallback fonts with the bundled ones (used via DynamicResource).</summary>
        private void ConfigureFonts()
        {
            try
            {
                var baseUri = new Uri("pack://application:,,,/");
                Resources["UiFont"] = new FontFamily(baseUri, UiFontStack);
                Resources["DisplayFont"] = new FontFamily(baseUri, DisplayFontStack);
                Resources["MonoFont"] = new FontFamily(baseUri, MonoFontStack);
            }
            catch (Exception ex)
            {
                // Keep the theme's system-font fallbacks
                Serilog.Log.Warning(ex, "Could not load bundled fonts");
            }
        }

        private static void ConfigureLogging()
        {
            // NOTE: the logger must live for the whole app lifetime.
            // (Previously it was created with `using var`, which disposed it as soon as the
            // MainWindow constructor finished, silently dropping all later log entries.)
            Log.Logger = new LoggerConfiguration()
#if DEBUG
                .MinimumLevel.Debug()
#else
                .MinimumLevel.Information()
#endif
                .WriteTo.Console()
                .WriteTo.File(
                    AppPaths.LogFile,
                    rollOnFileSizeLimit: true,
                    fileSizeLimitBytes: 1048576,
                    retainedFileCountLimit: 10,
                    shared: true)
                .CreateLogger();
        }

        private void RegisterGlobalExceptionHandlers()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", args.IsTerminating);
                Log.CloseAndFlush();
            };

            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                Log.Error(args.Exception, "Unobserved task exception");
                args.SetObserved();
            };
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // Keep the app alive (it usually runs unattended in the tray) and log the problem.
            Log.Error(e.Exception, "Unhandled UI exception");
            e.Handled = true;

            try
            {
                MessageBox.Show("An unexpected error occurred:\n\n" + e.Exception.Message +
                                "\n\nThe app will keep running. Details were written to:\n" + AppPaths.LogFile,
                                "VU1 Demo App", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch
            {
                // Never throw from the global handler
            }
        }
    }
}
