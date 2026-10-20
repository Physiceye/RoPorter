using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace Rbx2Source
{
    public sealed class App : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = new MainWindow();
            base.OnFrameworkInitializationCompleted();
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Run by RoPorter itself to set a Workshop item's description and page image (see Export.SteamPreview).
            if (args.Length > 0 && args[0] == Export.Workshop.DetailsArgument)
                return Export.SteamPreview.Run(args);
            // Run by RoPorter itself to publish a Source Filmmaker export to SFM's Workshop (see Export.SfmWorkshop).
            if (args.Length > 0 && args[0] == Export.SfmWorkshop.PublishArgument)
                return Export.SfmWorkshop.Run(args);
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
    }
}
