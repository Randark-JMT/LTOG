using Microsoft.UI.Xaml;

namespace LTOG.Gui;

public partial class App : Application
{
    public static bool AutoRemount { get; private set; }
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        AutoRemount = Environment.GetCommandLineArgs().Contains("--remount");
        UnhandledException += (_, e) =>
        {
            LogCrash($"UnhandledException: {e.Exception}");
            e.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash($"AppDomain: {e.ExceptionObject}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            LogCrash($"OnLaunched: {ex}");
            throw;
        }
    }

    private static void LogCrash(string text)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LTOG");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:O}] {text}\n");
        }
        catch { }
    }
}
