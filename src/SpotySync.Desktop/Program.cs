using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using SpotySync;

namespace SpotySyncDesktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // a new version folder takes over settings, login and caches from the previous one (as on Windows)
        try { VersionCarryOver.RunIfFresh(AppContext.BaseDirectory); } catch { }
        if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal)) return CommandLine.Run(args).GetAwaiter().GetResult();
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }
}

public sealed class App : Application
{
    public override void Initialize()
    {
        var fluent = new FluentTheme();
        fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources
        {
            Accent = Pal.AccentColor,
            RegionColor = Pal.Background.Color,
            AltHigh = Pal.Background.Color,
            ChromeMediumLow = Pal.Surface.Color,
            ChromeMedium = Pal.Card.Color,
            ChromeLow = Pal.Surface.Color
        };
        Styles.Add(fluent);
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
