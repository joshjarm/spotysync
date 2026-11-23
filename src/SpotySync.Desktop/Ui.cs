using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SpotySyncDesktop;

/// <summary>Colors of the Linux window, matching the dark SpotySync look on Windows.</summary>
internal static class Pal
{
    public static readonly Color AccentColor = Color.Parse("#1DB954");
    public static readonly SolidColorBrush Background = new(Color.Parse("#121212"));
    public static readonly SolidColorBrush Surface = new(Color.Parse("#181818"));
    public static readonly SolidColorBrush Card = new(Color.Parse("#202020"));
    public static readonly SolidColorBrush CardHover = new(Color.Parse("#2A2A2A"));
    public static readonly SolidColorBrush Line = new(Color.Parse("#2E2E2E"));
    public static readonly SolidColorBrush Ink = new(Color.Parse("#EDEDED"));
    public static readonly SolidColorBrush Muted = new(Color.Parse("#A7A7A7"));
    public static readonly SolidColorBrush Dim = new(Color.Parse("#6F6F6F"));
    public static readonly SolidColorBrush Accent = new(AccentColor);
    public static readonly SolidColorBrush AccentInk = new(Color.Parse("#0B0B0B"));
    public static readonly SolidColorBrush Ok = new(Color.Parse("#4ADE80"));
    public static readonly SolidColorBrush Warn = new(Color.Parse("#E8B339"));
    public static readonly SolidColorBrush Err = new(Color.Parse("#F06A63"));
    public static readonly SolidColorBrush SpotifyAdd = new(Color.Parse("#1DB954"));
    public static readonly SolidColorBrush LocalAdd = new(Color.Parse("#5AA9FF"));
    public static readonly FontFamily Mono = new("Cascadia Mono, JetBrains Mono, DejaVu Sans Mono, Noto Sans Mono, monospace");
}

/// <summary>Small builders so the pages read like a layout, not like property soup.</summary>
internal static class Ui
{
    public static TextBlock Title(string text) => new()
    {
        Text = text, FontSize = 26, FontWeight = FontWeight.Bold, Foreground = Pal.Ink, Margin = new Thickness(0, 0, 0, 4)
    };

    public static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Pal.Ink, Margin = new Thickness(0, 0, 0, 6)
    };

    public static TextBlock Text(string text, double size = 13.5) => new()
    {
        Text = text, FontSize = size, Foreground = Pal.Ink, TextWrapping = TextWrapping.Wrap
    };

    public static TextBlock Muted(string text, double size = 13) => new()
    {
        Text = text, FontSize = size, Foreground = Pal.Muted, TextWrapping = TextWrapping.Wrap
    };

    public static Border Card(Control child, double padding = 18) => new()
    {
        Background = Pal.Card, CornerRadius = new CornerRadius(12), Padding = new Thickness(padding), Child = child,
        BorderBrush = Pal.Line, BorderThickness = new Thickness(1)
    };

    public static Button Button(string text, Action onClick, bool primary = false)
    {
        var button = new Button
        {
            Content = text, Padding = new Thickness(16, 8), CornerRadius = new CornerRadius(18), FontWeight = FontWeight.SemiBold,
            Background = primary ? Pal.Accent : Pal.CardHover, Foreground = primary ? Pal.AccentInk : Pal.Ink,
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    public static StackPanel Column(double spacing, params Control[] children)
    {
        var column = new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing };
        foreach (var child in children) column.Children.Add(child);
        return column;
    }

    /// <summary>A label above an input, as on the Settings page.</summary>
    public static Control Field(string label, Control input, string? hint = null)
    {
        input.HorizontalAlignment = HorizontalAlignment.Left;
        var column = Column(4, new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Pal.Ink }, input);
        if (hint is not null) column.Children.Add(Muted(hint, 12));
        return column;
    }

    /// <summary>Opens a link or folder with the desktop's default app (xdg-open on Linux).</summary>
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch
        {
            try { if (!OperatingSystem.IsWindows()) Process.Start("xdg-open", target); } catch { }
        }
    }
}
