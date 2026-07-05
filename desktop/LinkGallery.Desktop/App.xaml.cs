using System.IO;

namespace LinkGallery.Desktop;

public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += (_, args) =>
            WriteE2eFailure("dispatcher", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteE2eFailure("app-domain", args.ExceptionObject as Exception);
    }

    private static void WriteE2eFailure(string source, Exception? exception)
    {
        if (Environment.GetEnvironmentVariable("LINKGALLERY_E2E_DATA_DIRECTORY")
                is not { Length: > 0 } directory)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "desktop-unhandled.log"),
                $"{DateTimeOffset.UtcNow:O} [{source}]{Environment.NewLine}" +
                $"{exception}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never replace the original failure.
        }
    }
}
