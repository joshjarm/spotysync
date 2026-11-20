using System.Globalization;

namespace SpotySyncApp;

/// <summary>Color helpers shared by the theme engine and every custom-drawn control.</summary>
internal static class ColorX
{
    public static Color Hex(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        return Color.FromArgb(int.Parse(hex[..2], NumberStyles.HexNumber), int.Parse(hex[2..4], NumberStyles.HexNumber), int.Parse(hex[4..6], NumberStyles.HexNumber));
    }

    public static bool TryHex(string? value, out Color color)
    {
        color = Color.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim().TrimStart('#');
        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) return false;
        color = Hex(text);
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    /// <summary>Blend a towards b (t = 0 → a, t = 1 → b).</summary>
    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    public static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
    public static Color Alpha(Color c, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), c.R, c.G, c.B);
}

/// <summary>A theme as designed: the handful of colors a designer picks. Everything else is derived.</summary>
internal sealed record ThemeDef(string Id, string Name, string Group,
    string Bg, string Panel, string Panel2, string Raised, string Well, string Line, string Line2,
    string Ink, string Muted, string Dim, string Accent, string Glow, string? Accent2 = null,
    /// <summary>Optional backdrop effect drawn once into the cached page-header backdrop: stars, grid, scan, rays, waves, embers.</summary>
    string? Fx = null);

/// <summary>The resolved colors every control paints with.</summary>
internal sealed class Palette
{
    public string Id = "";
    public string Name = "";
    public Color Bg, Glow, Panel, Panel2, Field, Well, Line, Border, Ink, Muted, Dim;
    public Color Accent, AccentHi, AccentLo, AccentInk, Sel, Hover;
    /// <summary>Second accent: equal to Accent for single-color themes, a contrasting hue for duotones.</summary>
    public Color Accent2, Glow2;
    public bool Duotone;
    public string Fx = "";
    public Color Ok, Warn, Err, Busy, Info;
    // Semantic sync colors (kept recognisable across every theme)
    public Color SpotifyAdd, LocalAdd, Protected, Removal;

    public static Palette From(ThemeDef t, Color? accentOverride = null)
    {
        var accent = accentOverride ?? ColorX.Hex(t.Accent);
        var bg = ColorX.Hex(t.Bg);
        var raised = ColorX.Hex(t.Raised);
        var panel = ColorX.Hex(t.Panel);
        var p = new Palette
        {
            Id = t.Id, Name = t.Name,
            Bg = bg, Glow = accentOverride is null ? ColorX.Hex(t.Glow) : ColorX.Mix(bg, accent, 0.17),
            Panel = panel, Panel2 = ColorX.Hex(t.Panel2), Field = raised, Well = ColorX.Hex(t.Well),
            Line = ColorX.Hex(t.Line), Border = ColorX.Hex(t.Line2),
            Ink = ColorX.Hex(t.Ink), Muted = ColorX.Hex(t.Muted), Dim = ColorX.Hex(t.Dim),
            Accent = accent,
            AccentHi = ColorX.Mix(accent, Color.White, 0.22),
            AccentLo = ColorX.Mix(accent, Color.Black, 0.30),
            AccentInk = ColorX.Luma(accent) > 0.5 ? ColorX.Hex("#0b0b0b") : Color.White,
            Sel = ColorX.Mix(panel, accent, 0.30),
            Hover = ColorX.Mix(raised, accent, 0.16),
            Ok = ColorX.Hex("#4ade80"), Warn = ColorX.Hex("#fbaa4b"), Err = ColorX.Hex("#ff5d61"),
            Busy = ColorX.Hex("#ffd60a"), Info = ColorX.Hex("#7aa7ff"),
        };
        p.Accent2 = t.Accent2 is null || accentOverride is not null ? accent : ColorX.Hex(t.Accent2);
        p.Duotone = p.Accent2 != accent;
        p.Fx = t.Fx ?? "";
        p.Glow2 = p.Duotone ? ColorX.Mix(bg, p.Accent2, 0.16) : p.Glow;
        p.SpotifyAdd = ColorX.Hex("#53da89");
        p.LocalAdd = ColorX.Hex("#74aeff");
        p.Protected = ColorX.Hex("#c39bff");
        p.Removal = p.Err;
        return p;
    }
}

/// <summary>Every built-in theme, grouped for the gallery.</summary>
internal static class ThemeCatalog
{
    public const string DefaultId = "spotysync";
    public static readonly string[] Groups = ["SpotySync", "Duotone", "Game worlds", "Studio classics", "QD-OLED glow"];

    public static readonly IReadOnlyList<ThemeDef> All = Build();
    public static readonly IReadOnlyList<ThemeDef> RandomPool = BuildPool();

    public static ThemeDef Find(string? id) =>
        All.FirstOrDefault(x => x.Id.Equals(id ?? "", StringComparison.OrdinalIgnoreCase))
        ?? RandomPool.FirstOrDefault(x => x.Id.Equals(id ?? "", StringComparison.OrdinalIgnoreCase))
        ?? All[0];

    /// <summary>Builds a whole theme from a background, a surface tint, one accent and the text color.</summary>
    private static ThemeDef Make(string id, string name, string group, string bg, string tint, string accent, string ink, double strength = 1.0, string? accent2 = null, string? fx = null)
    {
        var b = ColorX.Hex(bg); var tt = ColorX.Hex(tint); var i = ColorX.Hex(ink); var a = ColorX.Hex(accent);
        string K(double v) => ColorX.ToHex(ColorX.Mix(b, tt, Math.Min(0.85, v * strength)));
        return new ThemeDef(id, name, group, bg, K(0.05), K(0.08), K(0.13), ColorX.ToHex(ColorX.Mix(b, Color.Black, 0.6)),
            K(0.14), K(0.27), ink, ColorX.ToHex(ColorX.Mix(i, b, 0.38)), ColorX.ToHex(ColorX.Mix(i, b, 0.64)), accent,
            ColorX.ToHex(ColorX.Mix(b, a, 0.17)), accent2, fx);
    }

    private static List<ThemeDef> Build()
    {
        const string S = "SpotySync", D = "Duotone", G = "Game worlds", C = "Studio classics", Q = "QD-OLED glow";
        var list = new List<ThemeDef>
        {
            new("spotysync", "SpotySync", S, "#121212", "#1b1c1e", "#202124", "#27282b", "#0d0d0e", "#2a2c30", "#3a3d43", "#f2f2f2", "#aaaab0", "#6e6f76", "#2ecc71", "#15291d"),
            Make("musicbee_night", "Bee Night", S, "#0b0b07", "#f5c518", "#ffcf33", "#fff8dc", 1.2),
            // Duotone: two contrasting accents (gradients on buttons, logo, progress, glow and graphs)
            Make("duo_crimson_cobalt", "Crimson & Cobalt", D, "#0a0710", "#5a1a3a", "#ff3355", "#f4e9f0", 1.5, "#4f7dff"),
            Make("duo_ruby_sapphire", "Ruby & Sapphire", D, "#07060f", "#2a1f5c", "#e0115f", "#eef1ff", 1.5, "#3fa9ff"),
            // Effect themes: a pattern baked once into the header backdrop (no ongoing cost)
            Make("fx_starfield", "Starfield", D, "#03040b", "#1a1f4a", "#8fa8ff", "#eef1ff", 1.5, "#ffd27a", "stars"),
            Make("fx_retro_grid", "Retro Grid", D, "#0b0614", "#2d1450", "#ff3fb4", "#fbefff", 1.5, "#32e6ff", "grid"),
            Make("fx_phosphor", "CRT Phosphor", D, "#020703", "#0f2a17", "#39ff7a", "#e6ffec", 1.5, "#ffb000", "scan"),
            Make("fx_solar_rays", "Solar Rays", D, "#0c0603", "#4a2410", "#ffb020", "#fff4e4", 1.5, "#ff4f5a", "rays"),
            Make("fx_tide_lines", "Tide Lines", D, "#020a10", "#0e3550", "#3ad1ff", "#e6f8ff", 1.5, "#9effc8", "waves"),
            Make("fx_ember_drift", "Ember Drift", D, "#0a0504", "#3a160c", "#ff6a2a", "#fff0e8", 1.5, "#ffd166", "embers"),
            Make("duo_blood_steel", "Blood & Steel", D, "#0b0b0e", "#2b3340", "#e5383b", "#eef1f5", 1.6, "#8fb0d8"),
            Make("duo_synth_sunset", "Synth Sunset", D, "#0f0716", "#4a1d5c", "#ff4fa3", "#fff0f7", 1.4, "#ffa53d"),
            Make("duo_vaporwave", "Vaporwave", D, "#0d0a1a", "#2e2457", "#ff71ce", "#f5f0ff", 1.5, "#01cdfe"),
            Make("duo_aurora", "Aurora", D, "#050d10", "#12404a", "#2de0b8", "#e8fbf6", 1.4, "#9b6bff", "waves"),
            Make("duo_ember_ice", "Ember & Ice", D, "#0a0a0f", "#26303f", "#ff7a2f", "#f2f6fa", 1.5, "#3fd9ff"),
            Make("duo_royal_gold", "Royal Gold", D, "#0a0614", "#2e1a55", "#a36bff", "#f3ecff", 1.5, "#ffc94a"),
            Make("duo_coral_reef", "Coral Reef", D, "#04101a", "#0f3a52", "#ff6f61", "#eaf8fb", 1.5, "#2ec4d6"),
            Make("duo_midnight_mango", "Midnight Mango", D, "#050611", "#1d2457", "#ffb347", "#f1f2ff", 1.5, "#6c7bff"),
            Make("duo_lava_lagoon", "Lava Lagoon", D, "#0b0605", "#4a1a10", "#ff5a1f", "#fff1ea", 1.5, "#23d5c4"),
            Make("duo_matcha_berry", "Matcha & Berry", D, "#070906", "#2d3d1f", "#a8d672", "#f4fbea", 1.5, "#ff5c8a"),
            Make("duo_ice_cherry", "Ice Cherry", D, "#06080d", "#283a52", "#ff3d6e", "#eef6ff", 1.5, "#b8e6ff"),
            Make("duo_ultramarine_saffron", "Ultramarine & Saffron", D, "#04050f", "#1d2a7a", "#4a72ff", "#eef1ff", 1.5, "#ffb627"),
            Make("duo_mint_magma", "Mint & Magma", D, "#050a09", "#164a3d", "#5effc8", "#eafff8", 1.5, "#ff4d2e"),
            Make("duo_lilac_lemon", "Lilac & Lemon", D, "#0a0810", "#3a2b5c", "#c3a6ff", "#f6f1ff", 1.5, "#f8ef6c"),
            Make("duo_neon_noir", "Neon Noir", D, "#000000", "#3a1024", "#ff1f6b", "#ffeef4", 1.6, "#1fffb0"),
            Make("duo_sakura_storm", "Sakura Storm", D, "#0a0710", "#3d2440", "#ffa6c9", "#fff1f7", 1.5, "#6c8cff"),
            Make("duo_copper_teal", "Copper & Teal", D, "#070807", "#3b2a1f", "#e0864a", "#fbf2ea", 1.5, "#2bb3a6"),
            Make("duo_desert_night", "Desert Night", D, "#0c0805", "#4a3018", "#ffc46b", "#fff6e8", 1.5, "#7a5cff"),
            // Inspired by the games on this PC (palettes only — names hint at the game)
            Make("game_alchemist", "Alchemist's Fire", G, "#07060a", "#2a1a3d", "#ff7b1a", "#fbf1ff", 1.5, "#7f5cff", "embers"),
            Make("game_gungeon", "Bullet Hell Brass", G, "#0c0806", "#3a2512", "#f2a330", "#fff4e6", 1.5, "#3fc6ff"),
            Make("game_petrichor", "Petrichor", G, "#06080d", "#1f2b45", "#e1c46a", "#eef2fb", 1.5, "#6fd4ff", "rain"),
            Make("game_abyss", "Arctic Abyss", G, "#020a10", "#0e3a4f", "#3ae6d6", "#e6fbfb", 1.5, "#ff8fc8", "embers"),
            Make("game_rules", "Rule Is Words", G, "#080808", "#2e1f26", "#e6476a", "#fbeff3", 1.5, "#e4d24a"),
            Make("game_dragonborn", "Dragonborn Frost", G, "#07090b", "#2a3138", "#c9d6e3", "#f1f5f9", 1.5, "#d4a24a"),
            Make("game_glade", "Meadow Glade", G, "#080b0a", "#23402f", "#8fd6a0", "#eefaf2", 1.5, "#ffb48a"),
            Make("game_astro", "Astro Orange", G, "#050812", "#1a2a50", "#ff8a3d", "#eef3ff", 1.5, "#3ed8c3"),
            Make("game_element115", "Element 115", G, "#03050a", "#122a3a", "#46f0ff", "#e8fdff", 1.5, "#b45cff", "stars"), // glowing 115 cyan + Pack-a-Punch violet
            // The color schemes from the T320 Pool Planner
            new("graphite", "Rack Graphite", C, "#0a0e13", "#10161d", "#141b24", "#19212d", "#0b1016", "#202a37", "#2c394a", "#e8eef5", "#95a3b5", "#66758b", "#f4ab3e", "#132339"),
            new("midnight", "Midnight Blue", C, "#070b16", "#0b1222", "#0f172b", "#152038", "#070c18", "#1a2642", "#263559", "#e6edff", "#9aa9cc", "#62719a", "#5ea2ff", "#10224a"),
            new("tokyo", "Tokyo Night", C, "#16161e", "#1a1b26", "#1f2030", "#24283b", "#13131a", "#292e42", "#3b4261", "#c0caf5", "#a9b1d6", "#565f89", "#7aa2f7", "#24283b"),
            new("nord", "Nord", C, "#1f232b", "#242933", "#2a303c", "#323946", "#1c2028", "#363d4a", "#434c5e", "#eceff4", "#aab4c5", "#7b8496", "#88c0d0", "#2b3a4f"),
            new("dracula", "Dracula", C, "#1e1f29", "#242530", "#282a36", "#313343", "#1b1c25", "#343746", "#44475a", "#f8f8f2", "#b7b9cc", "#7c7f99", "#bd93f9", "#2f2a4a"),
            new("catppuccin", "Catppuccin Mocha", C, "#11111b", "#181825", "#1e1e2e", "#252536", "#0f0f17", "#2a2a3c", "#45475a", "#cdd6f4", "#bac2de", "#6c7086", "#f5c2e7", "#1e1e2e"),
            new("onedark", "One Dark", C, "#1b1f27", "#21252b", "#282c34", "#2f343f", "#191c22", "#323842", "#3e4451", "#dcdfe4", "#abb2bf", "#6b7280", "#61afef", "#2a3140"),
            new("gruvbox", "Gruvbox", C, "#1d2021", "#242526", "#282828", "#32302f", "#1a1c1d", "#3c3836", "#504945", "#ebdbb2", "#bdae93", "#7c6f64", "#fe8019", "#32302f"),
            new("solarized", "Solarized Dark", C, "#00212b", "#002833", "#073642", "#0b404e", "#001e27", "#0e4552", "#1c5866", "#eee8d5", "#93a1a1", "#657b83", "#b58900", "#063845"),
            new("monokai", "Monokai", C, "#1e1f1c", "#232420", "#272822", "#30312a", "#1a1b18", "#33342d", "#49483e", "#f8f8f2", "#cfcfc2", "#75715e", "#a6e22e", "#2d2e27"),
            new("ocean", "Deep Ocean", C, "#07141a", "#0a1b23", "#0d212b", "#122b37", "#06121a", "#16323f", "#204556", "#e2f4f8", "#9cc3cc", "#5f8892", "#2dd4bf", "#0b2f3d"),
            new("crimson", "Crimson", C, "#120a0c", "#170d10", "#1c1014", "#26161b", "#0f080a", "#2f1b21", "#45262e", "#f5e6e9", "#c9a3ab", "#86636b", "#ff5d73", "#2a1016"),
            new("matrix", "Terminal Green", C, "#030805", "#06100a", "#08150d", "#0c1f13", "#020604", "#123321", "#1b4a2f", "#c8f7d2", "#7fcf93", "#4a8a5c", "#39ff7a", "#0b2414"),
            new("amber", "Amber CRT", C, "#0d0904", "#130d06", "#181008", "#22170b", "#0a0703", "#2e200e", "#46311a", "#ffe2b0", "#d6ab6a", "#8f6c3f", "#ffb000", "#2a1a06"),
            new("slate", "Slate", C, "#0f1115", "#14171c", "#191d23", "#21262e", "#0d0f13", "#252a33", "#333a45", "#e5e7eb", "#9ca3af", "#6b7280", "#a3a3ff", "#1d222b"),
            new("oled", "OLED Black", C, "#000000", "#050505", "#0a0a0a", "#141414", "#000000", "#1c1c1c", "#2a2a2a", "#f2f2f2", "#a0a0a0", "#666666", "#f4ab3e", "#0a0a0a"),
            // QD-OLED: true black backgrounds so the pixels switch off, accents that glow
            Make("qd_hyper_pink", "Hyper Pink", Q, "#000000", "#ff2e93", "#ff2e93", "#ffe6f1"),
            Make("qd_ion_cyan", "Ion Cyan", Q, "#000000", "#00d9ff", "#00e5ff", "#e3fbff"),
            Make("qd_blood_moon", "Blood Moon", Q, "#000000", "#ff1f2d", "#ff2a36", "#ffe5e6"),
            Make("qd_ultraviolet", "Ultraviolet", Q, "#000000", "#8a3dff", "#a066ff", "#f1e9ff"),
            Make("qd_cobalt_blaze", "Cobalt Blaze", Q, "#000000", "#2f6bff", "#3d7bff", "#e6eeff"),
            Make("qd_ember", "Ember", Q, "#000000", "#ff6a00", "#ff7b1c", "#fff0e2"),
            Make("qd_rose_gold", "Rose Gold", Q, "#000000", "#e8a598", "#f3b9aa", "#fff0eb"),
            Make("qd_gold_leaf", "Gold Leaf", Q, "#000000", "#d4af37", "#e8bf45", "#fff7de"),
            Make("qd_paper_ghost", "Paper Ghost", Q, "#000000", "#ffffff", "#f2f2f2", "#f5f5f5"),
            Make("qd_deep_space", "Deep Space", Q, "#02020b", "#5b5bff", "#6e6eff", "#ececff"),
        };
        return list;
    }

    private static List<ThemeDef> BuildPool()
    {
        const string R = "Surprise";
        return
        [
            Make("rnd_cotton_candy", "Cotton Candy Void", R, "#070205", "#ff8ccb", "#ffb3dc", "#fff0f8"),
            Make("rnd_arctic_ice", "Arctic Ice", R, "#00050a", "#5ad7ee", "#a5f3ff", "#effcff"),
            Make("rnd_jade_noir", "Jade Noir", R, "#010604", "#0f8f5a", "#1fcf86", "#e3fff1", 1.2),
            Make("rnd_ruby_rose", "Ruby Rose", R, "#080105", "#e11d48", "#f43f5e", "#ffe9ee"),
            Make("rnd_butter_static", "Butter Static", R, "#060600", "#fff08a", "#ffee8a", "#fffbe6"),
            Make("rnd_teal_tangerine", "Teal & Tangerine", R, "#00080a", "#14808f", "#ff8a3d", "#e6fbff", 1.9),
            Make("rnd_plum_mint", "Plum & Mint", R, "#070009", "#7a2a8f", "#6ee7b7", "#f4eaff", 1.9),
            Make("rnd_navy_gold", "Navy & Gold", R, "#00020c", "#1d3b8f", "#f5c542", "#eaf0ff", 1.9),
            Make("rnd_wine_cyan", "Wine & Cyan", R, "#080003", "#8f1d3f", "#22e4ff", "#ffe9ef", 1.9),
            Make("rnd_pine_pink", "Pine & Pink", R, "#000804", "#1d8f55", "#ff7ac6", "#e9fff2", 1.9),
            Make("rnd_violet_lime", "Violet & Lime", R, "#05000d", "#5a2dc9", "#c6ff3d", "#f0eaff", 1.9),
            Make("rnd_cocoa_cream", "Cocoa & Cream", R, "#080402", "#8a5a35", "#ffd9a8", "#fff4e6", 1.9),
            Make("rnd_slate_coral", "Slate & Coral", R, "#04060a", "#3d5a80", "#ff6b6b", "#eef3fa", 1.9),
            Make("rnd_obsidian_peach", "Obsidian & Peach", R, "#05030a", "#4b3a7a", "#ffb08a", "#f5f0ff", 1.9),
            Make("rnd_cobalt_citron", "Cobalt & Citron", R, "#00030f", "#1f4fd6", "#e6ff4d", "#eaf1ff", 1.9),
        ];
    }
}

/// <summary>The live theme. Controls read <see cref="P"/> when they paint and listen to <see cref="Changed"/>.</summary>
internal static class Theme
{
    public static Palette P { get; private set; } = Palette.From(ThemeCatalog.All[0]);
    public static bool Rounded { get; private set; } = true;
    public static bool Glow { get; private set; } = true;
    public static bool Animations { get; private set; } = true;
    public static string FontChoice { get; private set; } = "system";
    public static event Action? Changed;

    public static int R(int radius) => Rounded ? radius : Math.Min(radius, 3);

    public static void Apply(string themeId, string? accentHex, string font, bool rounded, bool glow, bool animations)
    {
        var def = ThemeCatalog.Find(themeId);
        P = Palette.From(def, ColorX.TryHex(accentHex, out var accent) ? accent : null);
        FontChoice = font is "display" or "mono" ? font : "system";
        Rounded = rounded; Glow = glow; Animations = animations;
        Fonts.Reset();
        Changed?.Invoke();
    }
}
