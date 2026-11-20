using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SpotySyncApp;

/// <summary>Segoe Fluent Icons / MDL2 code points used across the app.</summary>
internal static class Glyph
{
    public const string Home = "", Sync = "", Settings = "", Console = "", Download = "",
        Heart = "", Play = "", Search = "", Close = "", Check = "", Warning = "",
        Error = "", Copy = "", Delete = "", Folder = "", Palette = "", Menu = "",
        Up = "", Down = "", Refresh = "", Link = "", Music = "", Filter = "",
        Save = "", Eye = "", Shuffle = "", Library = "", Plug = "", Shield = "",
        Open = "", Clear = "", More = "", Lightning = "", Info = "", Account = "",
        Tools = "", List = "", Layout = "", Undo = "", Stop = "", Clock = "",
        Grid = "", Pin = "", Dice = "", Text = "", Database = "", Back = "",
        ChevronRight = "", ChevronLeft = "", Collapse = "", Expand = "", Add = "",
        Remove = "", CircleRing = "", Lock = "", Spotify = "",
        Pause = "", Minimize = "", Restore = "", Split = "";
}

/// <summary>Font factory that follows the user's font choice and falls back gracefully.</summary>
internal static class Fonts
{
    private static readonly Dictionary<string, Font> Cache = new();
    private static HashSet<string>? installed;

    private static bool Has(string family)
    {
        installed ??= new HashSet<string>(new InstalledFontCollection().Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        return installed.Contains(family);
    }

    private static string First(params string[] families) => families.FirstOrDefault(Has) ?? "Segoe UI";

    public static void Reset() => Cache.Clear();

    private static Font Get(string key, Func<Font> make)
    {
        if (!Cache.TryGetValue(key, out var font)) Cache[key] = font = make();
        return font;
    }

    public static string BodyFamily => Theme.FontChoice switch
    {
        "mono" => First("Cascadia Mono", "Consolas"),
        "display" => First("Bahnschrift", "Segoe UI"),
        _ => First("Segoe UI Variable Text", "Segoe UI")
    };

    public static Font Body(float size = 9.75f, FontStyle style = FontStyle.Regular) =>
        Get($"b{size}{style}{Theme.FontChoice}", () => new Font(BodyFamily, size, style));

    public static Font Semibold(float size = 9.75f) =>
        Get($"s{size}{Theme.FontChoice}", () => Theme.FontChoice == "system" && Has("Segoe UI Semibold")
            ? new Font("Segoe UI Semibold", size)
            : new Font(BodyFamily, size, FontStyle.Bold));

    public static Font Display(float size) =>
        Get($"d{size}{Theme.FontChoice}", () => Theme.FontChoice == "mono"
            ? new Font(First("Cascadia Mono SemiBold", "Cascadia Mono", "Consolas"), size, Has("Cascadia Mono SemiBold") ? FontStyle.Regular : FontStyle.Bold)
            : Has("Bahnschrift SemiBold SemiConden") ? new Font("Bahnschrift SemiBold SemiConden", size)
            : Has("Bahnschrift") ? new Font("Bahnschrift", size, FontStyle.Bold)
            : new Font("Segoe UI Semibold", size));

    public static Font Mono(float size = 9.25f, FontStyle style = FontStyle.Regular) =>
        Get($"m{size}{style}", () => new Font(First("Cascadia Mono", "Consolas"), size, style));

    public static Font Icon(float size = 11f) =>
        Get($"i{size}", () => new Font(First("Segoe Fluent Icons", "Segoe MDL2 Assets", "Segoe UI Symbol"), size));
}

/// <summary>Theme roles for plain WinForms controls and the walker that re-colors them.</summary>
internal static class Ui
{
    private sealed class RoleBox { public string Role = ""; }
    private static readonly ConditionalWeakTable<Control, RoleBox> Roles = new();

    public static T Role<T>(T control, string role) where T : Control
    {
        Roles.AddOrUpdate(control, new RoleBox { Role = role });
        Paint(control);
        return control;
    }

    public static string RoleOf(Control control) => Roles.TryGetValue(control, out var box) ? box.Role : "";

    private static readonly ConditionalWeakTable<Control, Func<Font>> FontMakers = new();

    public static Label Label(string text, string role = "ink", Font? font = null, bool autoSize = true)
    {
        var label = new Label { Text = text, AutoSize = autoSize, BackColor = Color.Transparent, UseMnemonic = false, Margin = new Padding(0) };
        if (font is not null)
        {
            label.Font = font;
            var maker = FontMaker(font);
            if (maker is not null) FontMakers.AddOrUpdate(label, maker);
        }
        return Role(label, role);
    }

    /// <summary>Works out which Fonts factory produced a font so it can be re-made when the font style changes.</summary>
    private static Func<Font>? FontMaker(Font font)
    {
        var size = font.Size; var style = font.Style;
        if (ReferenceEquals(font, Fonts.Display(size))) return () => Fonts.Display(size);
        if (ReferenceEquals(font, Fonts.Mono(size, style))) return () => Fonts.Mono(size, style);
        if (ReferenceEquals(font, Fonts.Semibold(size))) return () => Fonts.Semibold(size);
        if (ReferenceEquals(font, Fonts.Body(size, style))) return () => Fonts.Body(size, style);
        return null;
    }

    private static readonly ConditionalWeakTable<Control, object> Inheriting = new();
    private static readonly System.Reflection.PropertyInfo? DoubleBufferedProperty =
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>The nearest solid background color behind a control.</summary>
    public static Color SolidBack(Control? c)
    {
        for (var x = c; x is not null; x = x.Parent)
            if (x.BackColor.A == 255 && x is not ISelfBackground) return x.BackColor;
        return Theme.P.Bg;
    }

    /// <summary>Paints what the parent shows behind <paramref name="c"/> — a solid color or a parent backdrop (glow).</summary>
    public static void PaintBack(Graphics g, Control c)
    {
        Control? parent = c.Parent;
        var offset = c.Location;
        while (parent is ISelfBackground && parent is not IBackdrop && parent.Parent is not null) { offset.Offset(parent.Location); parent = parent.Parent; }
        if (parent is IBackdrop backdrop) backdrop.PaintBackdrop(g, new Rectangle(offset, c.Size));
        else using (var b = new SolidBrush(SolidBack(parent))) g.FillRectangle(b, 0, 0, c.Width, c.Height);
    }

    /// <summary>Re-color a control tree from the current palette (top-down, so children inherit solid colors).</summary>
    public static void Restyle(Control root)
    {
        root.SuspendLayout();
        Paint(root);
        foreach (Control child in root.Controls) Restyle(child);
        root.ResumeLayout(false);
        root.Invalidate();
    }

    private static void Paint(Control c)
    {
        var p = Theme.P;
        var role = RoleOf(c);
        // Opaque, double-buffered containers: no transparent repaint chains (the main cause of lag).
        if (c is not ISelfBackground and not TextBoxBase and not DataGridView and not ComboBox and not ListBox and not Form)
        {
            if (c.BackColor.A < 255 || Inheriting.TryGetValue(c, out _))
            {
                Inheriting.AddOrUpdate(c, true);
                c.BackColor = SolidBack(c.Parent);
            }
        }
        if (c is ScrollableControl or TableLayoutPanel or FlowLayoutPanel or System.Windows.Forms.Label) DoubleBufferedProperty?.SetValue(c, true);
        switch (c)
        {
            case Card card:
                card.BackColor = card.Raised ? p.Panel2 : p.Panel;
                break;
            case Chip or StatusPill:
                c.Font = Fonts.Body(9f);
                break;
            case ThemedControl:
                break;
            case RoundedButton button:
                button.Font = button.Kind == ButtonKind.Primary ? Fonts.Semibold(9.5f) : Fonts.Body(button.Font.Size);
                if (button.AutoSize) button.PerformLayout();
                break;
            case ToggleSwitch toggle:
                toggle.Font = Fonts.Body(9.5f);
                break;
            case Label label:
                if (FontMakers.TryGetValue(label, out var make)) label.Font = make();
                label.ForeColor = role switch
                {
                    "muted" => p.Muted, "dim" => p.Dim, "accent" or "eyebrow" => p.Accent, "danger" => p.Err,
                    "warn" => p.Warn, "ok" => p.Ok, _ => p.Ink
                };
                if (role == "title") label.Font = Fonts.Display(label.Font.Size);
                else if (role == "eyebrow") label.Font = Fonts.Mono(label.Font.Size, FontStyle.Bold);
                break;
            case TextBox box when box.Parent is FieldBox && !box.Font.Name.Contains("Mono") && !box.Font.Name.Contains("Consolas"):
                box.Font = Fonts.Body(9.75f);
                break;
            case TextBox or RichTextBox or ListBox:
                c.BackColor = role == "field" ? p.Field : p.Well;
                c.ForeColor = p.Ink;
                break;
            case DarkComboBox:
                c.BackColor = p.Well; c.ForeColor = p.Ink;
                break;
        }
    }

    public static void DarkScrollbars(Control control)
    {
        void Apply() { try { SetWindowTheme(control.Handle, "DarkMode_Explorer", null); } catch { } }
        if (control.IsHandleCreated) Apply(); else control.HandleCreated += (_, _) => Apply();
    }

    public static void StyleWindow(Form form)
    {
        void Apply()
        {
            if (!form.IsHandleCreated) return;
            try
            {
                var on = 1; var round = 2;
                DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int));
                DwmSetWindowAttribute(form.Handle, 33, ref round, sizeof(int));
                var caption = ColorRef(Theme.P.Bg); var text = ColorRef(Theme.P.Ink); var border = ColorRef(Theme.P.Line);
                DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int));
                DwmSetWindowAttribute(form.Handle, 36, ref text, sizeof(int));
                DwmSetWindowAttribute(form.Handle, 34, ref border, sizeof(int));
            }
            catch { }
        }
        form.HandleCreated += (_, _) => Apply();
        Apply();
    }

    private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    public static GraphicsPath Rounded(RectangleF b, float radius)
    {
        var path = new GraphicsPath();
        if (b.Width <= 0 || b.Height <= 0) return path;
        var d = Math.Min(Math.Min(radius * 2, b.Width), b.Height);
        if (d <= 1) { path.AddRectangle(b); return path; }
        path.AddArc(b.X, b.Y, d, d, 180, 90);
        path.AddArc(b.Right - d, b.Y, d, d, 270, 90);
        path.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
        path.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Graphics g, Color fill, RectangleF b, float radius, Color? border = null, float borderWidth = 1f)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(b, radius);
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        if (border is { A: > 0 } line)
        {
            using var pen = new Pen(line, borderWidth);
            g.DrawPath(pen, path);
        }
    }

    public static void FillGradient(Graphics g, Color from, Color to, RectangleF b, float radius, bool vertical = false)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(b, radius);
        var rect = new RectangleF(b.X - 1, b.Y - 1, b.Width + 2, b.Height + 2);
        using var brush = new LinearGradientBrush(rect, from, to, vertical ? LinearGradientMode.Vertical : LinearGradientMode.Horizontal);
        g.FillPath(brush, path);
    }

    public static void DrawGlyph(Graphics g, string glyph, float size, Color color, Rectangle bounds) =>
        TextRenderer.DrawText(g, glyph, Fonts.Icon(size), bounds, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

    public static void Text(Graphics g, string text, Font font, Rectangle bounds, Color color, TextFormatFlags extra = TextFormatFlags.Left)
    {
        TextRenderer.DrawText(g, text, font, bounds, color,
            extra | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    public static ToolTip ToolTip()
    {
        var tip = new ToolTip { OwnerDraw = true, InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 12000 };
        tip.Draw += (_, e) =>
        {
            var p = Theme.P;
            using var bg = new SolidBrush(p.Panel2);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using var pen = new Pen(p.Border);
            e.Graphics.DrawRectangle(pen, new Rectangle(0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1));
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, Fonts.Body(9f), Rectangle.Inflate(e.Bounds, -6, -3), p.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        };
        tip.Popup += (_, e) =>
        {
            var size = TextRenderer.MeasureText(tip.GetToolTip(e.AssociatedControl!), Fonts.Body(9f), new Size(380, 0), TextFormatFlags.WordBreak);
            e.ToolTipSize = new Size(size.Width + 14, size.Height + 8);
        };
        return tip;
    }

    public static ContextMenuStrip Menu()
    {
        var menu = new ContextMenuStrip { Renderer = new ThemedMenuRenderer(), ShowImageMargin = false, Font = Fonts.Body(9.5f) };
        menu.Opening += (_, _) => { menu.BackColor = Theme.P.Panel2; menu.ForeColor = Theme.P.Ink; menu.Renderer = new ThemedMenuRenderer(); };
        return menu;
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    public static extern int SetWindowTheme(IntPtr handle, string? subAppName, string? subIdList);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

internal sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
{
    public ThemedMenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.P.Ink : Theme.P.Dim;
        base.OnRenderItemText(e);
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        private static Palette P => Theme.P;
        public override Color ToolStripDropDownBackground => P.Panel2;
        public override Color MenuBorder => P.Border;
        public override Color MenuItemBorder => P.Accent;
        public override Color MenuItemSelected => P.Sel;
        public override Color MenuItemSelectedGradientBegin => P.Sel;
        public override Color MenuItemSelectedGradientEnd => P.Sel;
        public override Color ImageMarginGradientBegin => P.Panel2;
        public override Color ImageMarginGradientMiddle => P.Panel2;
        public override Color ImageMarginGradientEnd => P.Panel2;
        public override Color SeparatorDark => P.Line;
        public override Color SeparatorLight => P.Line;
    }
}

/// <summary>Controls that paint their own background (they ask their parent what is behind them).</summary>
internal interface ISelfBackground { }

/// <summary>Parents that paint something richer than a solid color behind their children (e.g. the header glow).</summary>
internal interface IBackdrop
{
    /// <summary>Paint the area <paramref name="childBounds"/> (in this control's coordinates) at the graphics origin.</summary>
    void PaintBackdrop(Graphics g, Rectangle childBounds);
}

/// <summary>Marker base for controls that paint themselves from <see cref="Theme.P"/>.</summary>
internal abstract class ThemedControl : Control, ISelfBackground
{
    protected bool Hovered, Pressed;
    private Tween? hoverTween;
    /// <summary>Eased 0..1 hover amount (animates in and out).</summary>
    protected float HoverT => hoverTween?.Eased ?? (Hovered ? 1f : 0f);

    protected ThemedControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        DoubleBuffered = true;
    }

    protected override void OnPaintBackground(PaintEventArgs e) => Ui.PaintBack(e.Graphics, this);

    private Tween HoverTween() { if (hoverTween is null) { hoverTween = new Tween(this, 0.12); hoverTween.Changed += Invalidate; } return hoverTween; }
    protected override void OnMouseEnter(EventArgs e) { Hovered = true; HoverTween().To(1); Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hovered = Pressed = false; HoverTween().To(0); Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
}

/// <summary>Controls whose height depends on their width (rows with wrapping text, card grids...).</summary>
internal interface IHeightForWidth
{
    int HeightFor(int width);
}

public enum ButtonKind { Primary, Secondary, Ghost, Danger }

/// <summary>Rounded, theme-aware push button with an optional icon glyph.</summary>
public sealed class RoundedButton : Button, ISelfBackground
{
    private bool hovered, pressed;
    public ButtonKind Kind { get; set; } = ButtonKind.Secondary;
    public string Glyph { get; set; } = "";
    public int CornerRadius { get; set; } = 9;

    public RoundedButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        UseMnemonic = false;
        Cursor = Cursors.Hand;
        Margin = new Padding(4);
        Font = Fonts.Body(9.5f);
        MinimumSize = new Size(0, 34);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text.Length == 0 ? " " : Text, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        var glyphWidth = Glyph.Length > 0 ? (Text.Length > 0 ? 24 : 16) : 0;
        var width = (Text.Length == 0 ? 18 : 30) + text.Width + glyphWidth;
        return new Size(Math.Max(MinimumSize.Width, width), Math.Max(MinimumSize.Height, text.Height + 14));
    }

    private Tween? hoverTween;
    private Tween HoverTween() { if (hoverTween is null) { hoverTween = new Tween(this, 0.12); hoverTween.Changed += Invalidate; } return hoverTween; }
    private float H => hoverTween?.Eased ?? (hovered ? 1f : 0f);
    protected override void OnMouseEnter(EventArgs e) { hovered = true; HoverTween().To(1); Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; HoverTween().To(0); Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaintBackground(PaintEventArgs pevent) => Ui.PaintBack(pevent.Graphics, this);

    protected override void OnPaint(PaintEventArgs e)
    {
        // Buttons don't get a reliable background pass, so always paint what's behind the rounded corners
        // first — otherwise stale pixels from the shared paint buffer show through (ghost-button artifacts).
        Ui.PaintBack(e.Graphics, this);
        var p = Theme.P;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Color fill, ink, border;
        switch (Kind)
        {
            case ButtonKind.Primary:
                fill = !Enabled ? ColorX.Mix(p.Field, p.Accent, 0.18) : pressed ? p.AccentLo : ColorX.Mix(p.Accent, p.AccentHi, H);
                ink = Enabled ? p.AccentInk : ColorX.Mix(p.Muted, p.Accent, 0.25);
                border = Color.Transparent;
                break;
            case ButtonKind.Danger:
                fill = !Enabled ? p.Field : pressed ? ColorX.Mix(p.Err, Color.Black, 0.3) : ColorX.Mix(ColorX.Mix(p.Field, p.Err, 0.22), ColorX.Mix(p.Field, p.Err, 0.45), H);
                ink = Enabled ? ColorX.Mix(p.Err, Color.White, 0.55) : p.Dim;
                border = ColorX.Mix(p.Border, p.Err, 0.5);
                break;
            case ButtonKind.Ghost:
                fill = !Enabled ? Color.Transparent : pressed ? p.Sel : ColorX.Alpha(p.Hover, (int)(255 * H));
                ink = Enabled ? ColorX.Mix(p.Muted, p.Ink, H) : p.Dim;
                border = Enabled ? ColorX.Alpha(p.Border, (int)(255 * H)) : Color.Transparent;
                break;
            default:
                fill = !Enabled ? ColorX.Mix(p.Panel, p.Field, 0.5) : pressed ? p.Sel : ColorX.Mix(p.Field, p.Hover, H);
                ink = Enabled ? p.Ink : p.Dim;
                border = Enabled ? ColorX.Mix(p.Border, ColorX.Mix(p.Border, p.Accent, 0.55), H) : p.Border;
                break;
        }
        if (fill.A > 0 || border.A > 0) Ui.FillRound(g, fill, bounds, Theme.R(CornerRadius), border);
        var content = ClientRectangle;
        if (Glyph.Length > 0 && Text.Length == 0)
        {
            Ui.DrawGlyph(g, Glyph, 10.5f, ink, content);
        }
        else if (Glyph.Length > 0)
        {
            var textSize = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            var total = 22 + textSize.Width;
            var x = Math.Max(10, (Width - total) / 2);
            Ui.DrawGlyph(g, Glyph, 10.5f, Kind == ButtonKind.Primary ? ink : (Enabled ? p.Accent : p.Dim), new Rectangle(x, 0, 16, Height));
            Ui.Text(g, Text, Font, new Rectangle(x + 22, 0, Width - x - 26, Height), ink);
        }
        else
        {
            Ui.Text(g, Text, Font, new Rectangle(8, 0, Width - 16, Height), ink, TextFormatFlags.HorizontalCenter);
        }
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(ColorX.Alpha(p.Accent, 200), 1.5f);
            using var path = Ui.Rounded(RectangleF.Inflate(bounds, -2.5f, -2.5f), Theme.R(CornerRadius - 2));
            g.DrawPath(pen, path);
        }
    }

    public static RoundedButton Make(string text, ButtonKind kind = ButtonKind.Secondary, string glyph = "", EventHandler? click = null)
    {
        var button = new RoundedButton { Text = text, Kind = kind, Glyph = glyph, AutoSize = true };
        if (kind == ButtonKind.Primary) button.Font = Fonts.Semibold(9.5f);
        if (click is not null) button.Click += click;
        return button;
    }
}

/// <summary>A rounded surface with an optional painted header (eyebrow, icon, title, menu).</summary>
internal class Card : Panel
{
    private string title = "", eyebrow = "", glyph = "";
    private bool menuHover;
    private int actionHover = -1;
    private bool minimized, lifted;
    private static readonly ToolTip ActionTips = Ui.ToolTip();
    public int Radius { get; set; } = 14;
    public bool Raised { get; set; }
    public Color? AccentEdge { get; set; }
    public ContextMenuStrip? HeaderMenu { get; set; }
    /// <summary>Shows a resize grip in the bottom-right corner (dashboard modules).</summary>
    public bool Resizable { get; set; }

    /// <summary>A small icon button in the header, left of the "…" menu (minimize, full screen…).</summary>
    public sealed class HeaderAction
    {
        public string Glyph = "";
        public string Tip = "";
        public Action? Click;
    }

    /// <summary>Header buttons, right to left.</summary>
    public List<HeaderAction> HeaderActions { get; } = new();

    /// <summary>Only the header is shown (dashboard modules); the body is hidden until restored.</summary>
    public bool Minimized
    {
        get => minimized;
        set
        {
            if (minimized == value) return;
            minimized = value;
            foreach (Control child in Controls) child.Visible = !value;
            Invalidate();
        }
    }

    public int MinimizedHeight => HeaderHeight + 2;

    /// <summary>Being dragged: drawn with a strong accent border.</summary>
    public bool Lifted { get => lifted; set { if (lifted != value) { lifted = value; Invalidate(); } } }

    // ---- hover highlight (dashboard modules): the border eases to the accent while the mouse is anywhere over the card
    private bool hoverHighlight;
    private Tween? hoverTween;
    public bool HoverHighlight
    {
        get => hoverHighlight;
        set
        {
            hoverHighlight = value;
            if (value && hoverTween is null)
            {
                hoverTween = new Tween(this, 0.16);
                hoverTween.Changed += () => Invalidate(false);
                HookHover(this);
            }
        }
    }

    private void HookHover(Control control)
    {
        control.MouseEnter += (_, _) => hoverTween?.To(1);
        control.MouseLeave += (_, _) => { if (!IsDisposed && IsHandleCreated) BeginInvoke(CheckHoverLeft); };
        control.ControlAdded += (_, e) => { if (e.Control is not null) HookHover(e.Control); };
        foreach (Control child in control.Controls) HookHover(child);
    }

    private void CheckHoverLeft()
    {
        // leaving a child into another child isn't leaving the card
        if (!RectangleToScreen(ClientRectangle).Contains(Cursor.Position)) hoverTween?.To(0);
    }
    public Rectangle GripBounds => minimized ? Rectangle.Empty : new(Width - 22, Height - 22, 20, 20);
    public event MouseEventHandler? HeaderMouseDown;
    /// <summary>Double-click on the header (outside its buttons).</summary>
    public event Action? HeaderDoubleClick;

    public string Title { get => title; set { title = value; UpdatePadding(); Invalidate(); } }
    public string Eyebrow { get => eyebrow; set { eyebrow = value; UpdatePadding(); Invalidate(); } }
    public string Glyph { get => glyph; set { glyph = value; Invalidate(); } }
    public int HeaderHeight => title.Length == 0 ? 0 : eyebrow.Length > 0 ? 58 : 44;
    private Padding inner = new(16, 12, 16, 14);
    public Padding Inner { get => inner; set { inner = value; UpdatePadding(); } }

    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        DoubleBuffered = true;
        UpdatePadding();
    }

    public void UpdatePadding() => Padding = new Padding(Inner.Left, HeaderHeight + Inner.Top, Inner.Right, Inner.Bottom);

    private Rectangle MenuBounds => HeaderMenu is null ? Rectangle.Empty : new(Width - 40, 10, 28, 28);

    private Rectangle ActionBounds(int index) => new(Width - 40 - (HeaderMenu is null ? 0 : 30) - index * 30, 10, 28, 28);

    /// <summary>Left edge of the header buttons (the title and the drag area end here).</summary>
    public int HeaderButtonsLeft => HeaderActions.Count > 0 ? ActionBounds(HeaderActions.Count - 1).Left : HeaderMenu is not null ? MenuBounds.Left : Width - 8;

    private int ActionAt(Point point)
    {
        for (var i = 0; i < HeaderActions.Count; i++) if (ActionBounds(i).Contains(point)) return i;
        return -1;
    }

    /// <summary>True when <paramref name="point"/> is on the header but not on one of its buttons.</summary>
    public bool IsDragArea(Point point) => point.Y < HeaderHeight && point.X < HeaderButtonsLeft - 2;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Ui.PaintBack(e.Graphics, this); // corners show whatever is behind the card
        var p = Theme.P;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        var fill = Raised ? p.Panel2 : p.Panel;
        if (BackColor != fill) BackColor = fill;
        using (var path = Ui.Rounded(bounds, Theme.R(Radius)))
        {
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            var h = lifted ? 1f : hoverTween?.Eased ?? 0f;
            using var pen = new Pen(h > 0.01f ? ColorX.Mix(p.Line, p.Accent, lifted ? 1 : 0.75 * h) : p.Line, lifted ? 2.2f : 1f + h * 0.6f);
            g.DrawPath(pen, path);
        }
        if (AccentEdge is { } edge)
        {
            using var brush = new SolidBrush(edge);
            g.FillRectangle(brush, 1, Theme.R(Radius) + 2, 3, Math.Max(0, Height - 2 * Theme.R(Radius) - 4));
        }
        if (title.Length == 0) return;
        var x = Inner.Left;
        var titleTop = eyebrow.Length > 0 ? 28 : 12;
        var textRight = HeaderButtonsLeft - 6;
        if (eyebrow.Length > 0)
            Ui.Text(g, eyebrow.ToUpperInvariant(), Fonts.Mono(7.75f, FontStyle.Bold), new Rectangle(x, 10, Math.Max(10, textRight - x), 18), p.Accent2);
        if (glyph.Length > 0)
        {
            Ui.DrawGlyph(g, glyph, 12f, p.Accent, new Rectangle(x - 2, titleTop, 22, 24));
            x += 26;
        }
        Ui.Text(g, title, Fonts.Display(13.5f), new Rectangle(x, titleTop - 1, Math.Max(10, textRight - x), 26), p.Ink);
        for (var i = 0; i < HeaderActions.Count; i++)
        {
            var r = ActionBounds(i);
            if (actionHover == i) Ui.FillRound(g, p.Hover, r, Theme.R(7));
            Ui.DrawGlyph(g, HeaderActions[i].Glyph, 9.5f, actionHover == i ? p.Ink : p.Dim, r);
        }
        if (HeaderMenu is not null)
        {
            if (menuHover) Ui.FillRound(g, p.Hover, MenuBounds, Theme.R(7));
            Ui.DrawGlyph(g, Glyph_More, 10f, menuHover ? p.Ink : p.Dim, MenuBounds);
        }
        if (minimized) return;
        using var line = new Pen(ColorX.Mix(p.Line, fill, 0.35));
        g.DrawLine(line, Inner.Left, HeaderHeight - 1, Width - Inner.Right, HeaderHeight - 1);
        if (Resizable)
        {
            using var dot = new SolidBrush(ColorX.Mix(p.Dim, p.Accent, 0.3));
            for (var i = 0; i < 3; i++)
                for (var j = 0; j <= i; j++)
                    g.FillEllipse(dot, Width - 8 - j * 5, Height - 18 + i * 5, 2.6f, 2.6f);
        }
    }

    private const string Glyph_More = SpotySyncApp.Glyph.More;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hover = HeaderMenu is not null && MenuBounds.Contains(e.Location);
        if (hover != menuHover) { menuHover = hover; Invalidate(MenuBounds); }
        var action = ActionAt(e.Location);
        if (action != actionHover)
        {
            if (actionHover >= 0) Invalidate(ActionBounds(actionHover));
            actionHover = action;
            if (action >= 0)
            {
                var r = ActionBounds(action);
                Invalidate(r);
                ActionTips.Show(HeaderActions[action].Tip, this, r.Left, r.Bottom + 4, 2500);
            }
            else ActionTips.Hide(this);
        }
        Cursor = hover || action >= 0 ? Cursors.Hand : Resizable && GripBounds.Contains(e.Location) ? Cursors.SizeNWSE
            : IsDragArea(e.Location) && HeaderMouseDown is not null ? Cursors.SizeAll : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (menuHover) { menuHover = false; Invalidate(MenuBounds); }
        if (actionHover >= 0) { Invalidate(ActionBounds(actionHover)); actionHover = -1; ActionTips.Hide(this); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (HeaderMenu is not null && MenuBounds.Contains(e.Location)) { HeaderMenu.Show(this, new Point(MenuBounds.Right - 4, MenuBounds.Bottom)); return; }
        var action = ActionAt(e.Location);
        if (action >= 0)
        {
            if (e.Button == MouseButtons.Left) { ActionTips.Hide(this); HeaderActions[action].Click?.Invoke(); }
            return;
        }
        if (e.Y < HeaderHeight) HeaderMouseDown?.Invoke(this, e);
        base.OnMouseDown(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && IsDragArea(e.Location)) HeaderDoubleClick?.Invoke();
    }
}

/// <summary>Vertical stack with an optional fixed gap; hidden children take no space. Width-aware heights.</summary>
internal class VStack : Panel, IHeightForWidth
{
    private SmoothScroller? smooth;
    public int Gap { get; set; } = 10;
    public event Action? Scrolled;
    public SmoothScroller Smooth => smooth ??= CreateSmooth();

    private SmoothScroller CreateSmooth() { var s = new SmoothScroller(this); s.Moved += () => Scrolled?.Invoke(); return s; }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (AutoScroll && VerticalScroll.Visible)
        {
            Smooth.Wheel(e.Delta);
            if (e is HandledMouseEventArgs h) h.Handled = true;
            return;
        }
        base.OnMouseWheel(e);
    }

    protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Scrolled?.Invoke(); }
    public bool AutoHeight { get; set; }

    public VStack()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.Transparent;
        DoubleBuffered = true;
    }

    public int HeightFor(int width)
    {
        var inner = width - Padding.Horizontal;
        var y = Padding.Top; var any = false;
        foreach (Control child in Controls)
        {
            if (!IsShown(child)) continue;
            // measure exactly as OnLayout lays out (minus the child's margins), or wrapping content
            // like the theme gallery ends up taller than measured and the group gets cut off
            y += (any ? Gap : 0) + ChildHeight(child, inner - child.Margin.Horizontal); any = true;
        }
        return y + Padding.Bottom;
    }

    private static bool IsShown(Control c) => c.Tag as string != "hidden" && (c is not IHideable h || !h.IsHidden);

    private static int ChildHeight(Control child, int width) =>
        child is IHeightForWidth hw ? hw.HeightFor(width) : child.Height;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        var width = ClientSize.Width - Padding.Horizontal;
        if (width <= 0) return;
        var y = Padding.Top + (AutoScroll ? AutoScrollPosition.Y : 0);
        var top = y; var any = false;
        foreach (Control child in Controls)
        {
            var shown = IsShown(child);
            if (child.Visible != shown) child.Visible = shown;
            if (!shown) continue;
            if (any) y += Gap;
            var h = ChildHeight(child, width - child.Margin.Horizontal);
            child.SetBounds(Padding.Left + child.Margin.Left, y, width - child.Margin.Horizontal, h);
            y += h; any = true;
        }
        var total = y - top + Padding.Vertical;
        if (AutoScroll && AutoScrollMinSize.Height != total) AutoScrollMinSize = new Size(0, total);
        if (AutoHeight && Height != total) Height = total;
        if (AutoScroll) base.OnLayout(levent); // ScrollableControl.OnLayout is what shows/sizes the scrollbars
    }
}

internal interface IHideable { bool IsHidden { get; } }

/// <summary>
/// Responsive dashboard grid: 1–3 columns depending on width. Every card has a column (<see cref="Cols"/>) and a
/// vertical order (<see cref="Orders"/>, its centre in layout pixels); cards are stacked in each column in that
/// order, so "put this one under that one" is simply a column + order. Dragging moves the real card under the
/// cursor while the other cards slide out of the way live; an outline shows where it will land.
/// </summary>
internal sealed class CardGrid : Panel
{
    /// <summary>Set before a user-driven layout change: modules then glide to their new places.</summary>
    public bool AnimateNextLayout { get; set; }
    private bool inGlideStep;            // true only while a glide frame moves the cards
    private Action<double>? glideFrame;  // the running glide, if any
    private Dictionary<Control, Rectangle> glideTargets = new();

    public int Gap { get; set; } = 14;
    public int MinColumnWidth { get; set; } = 330;
    public int MaxColumns { get; set; } = 3;
    public int MinCardHeight { get; set; } = 180;
    public int MaxCardHeight { get; set; } = 900;
    public int HeightSnap { get; set; } = 20;
    public Dictionary<Control, int> Spans { get; } = new();
    public Dictionary<Control, int> Heights { get; } = new();
    /// <summary>Column of each card's left edge.</summary>
    public Dictionary<Control, int> Cols { get; } = new();
    /// <summary>Vertical order of each card (its centre, in layout pixels).</summary>
    public Dictionary<Control, double> Orders { get; } = new();

    // gesture state
    private Control? lifted;
    private bool resizeMode;
    private Point grab;                       // move: cursor offset in the card; resize: offset from the bottom-right corner
    private Dictionary<Control, double> frozenOrders = new();
    private (int Col, double Order, int Span, int Height) liftedTarget;
    private Rectangle placeholder = Rectangle.Empty;

    public CardGrid()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Color.Transparent;
        AutoScroll = true;
        DoubleBuffered = true;
    }

    public bool Gesturing => lifted is not null;

    private SmoothScroller? smooth;

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (VerticalScroll.Visible)
        {
            (smooth ??= new SmoothScroller(this)).Wheel(e.Delta);
            if (e is HandledMouseEventArgs h) h.Handled = true;
            return;
        }
        base.OnMouseWheel(e);
    }

    public int Columns => Math.Clamp((ClientSize.Width - Padding.Horizontal + Gap) / (MinColumnWidth + Gap), 1, MaxColumns);

    private double ColWidth(int columns) => (ClientSize.Width - Padding.Horizontal - Gap * (columns - 1)) / (double)columns;

    private int Origin => Padding.Top + AutoScrollPosition.Y;

    private int CardHeight(Control c) => c is Card { Minimized: true } card ? card.MinimizedHeight : Heights.TryGetValue(c, out var h) ? h : c.Height;

    private int SpanOf(Control c, int columns) => Math.Clamp(Spans.TryGetValue(c, out var s) ? s : 1, 1, columns);

    // ---------------------------------------------------------------- glide
    private void StopGlide()
    {
        if (glideFrame is not null) { FrameClock.Unsubscribe(glideFrame); glideFrame = null; }
    }

    /// <summary>Moves cards from where they are now to their targets (a new target mid-glide simply re-targets).</summary>
    private void Glide(Dictionary<Control, Rectangle> targets)
    {
        StopGlide();
        glideTargets = targets;
        var moves = targets.Select(t => (Child: t.Key, From: t.Key.Visible && t.Key.Width > 0 ? t.Key.Bounds : t.Value, To: t.Value)).ToList();
        var elapsed = 0.0;
        void Frame(double dt)
        {
            elapsed += dt;
            var t = Math.Clamp(elapsed / 0.2, 0, 1);
            var e = 1 - Math.Pow(1 - t, 3);
            inGlideStep = true;
            try
            {
                foreach (var (child, from, to) in moves)
                {
                    if (child == lifted) continue;
                    int L(int a, int b) => (int)Math.Round(a + (b - a) * e);
                    var r = new Rectangle(L(from.X, to.X), L(from.Y, to.Y), L(from.Width, to.Width), L(from.Height, to.Height));
                    if (child.Bounds != r) child.SetBounds(r.X, r.Y, r.Width, r.Height);
                }
            }
            finally { inGlideStep = false; }
            if (t >= 1) StopGlide();
        }
        glideFrame = Frame;
        FrameClock.Subscribe(this, Frame);
    }

    // ---------------------------------------------------------------- arrangement
    /// <summary>Target rectangle of every visible card; <paramref name="extra"/> places the lifted card at a candidate spot.</summary>
    private Dictionary<Control, Rectangle> Arrange(IReadOnlyDictionary<Control, double> orders, (Control Card, int Col, double Order, int Span, int Height)? extra, out int bottom)
    {
        var columns = Columns;
        var colWidth = ColWidth(columns);
        var items = new List<(Control C, int Col, int Span, int H, double Key, int Index)>();
        var index = 0;
        foreach (Control child in Controls)
        {
            index++;
            if (!child.Visible) continue;
            if (extra is { } x && x.Card == child)
            {
                var span = Math.Clamp(x.Span, 1, columns);
                items.Add((child, Math.Clamp(x.Col, 0, columns - span), span, x.Height, x.Order, index));
                continue;
            }
            var s = SpanOf(child, columns);
            var col = Math.Clamp(Cols.TryGetValue(child, out var c) ? c : 0, 0, columns - s);
            items.Add((child, col, s, CardHeight(child), orders.TryGetValue(child, out var o) ? o : 1e9 + index, index));
        }
        var colBottom = new int[columns];
        var result = new Dictionary<Control, Rectangle>();
        foreach (var item in items.OrderBy(i => i.Key).ThenBy(i => i.Col).ThenBy(i => i.Index))
        {
            var top = 0;
            for (var k = item.Col; k < item.Col + item.Span; k++) top = Math.Max(top, colBottom[k]);
            var w = (int)Math.Round(item.Span * colWidth + (item.Span - 1) * Gap);
            var x = Padding.Left + (int)Math.Round(item.Col * (colWidth + Gap));
            result[item.C] = new Rectangle(x, Origin + top, w, item.H);
            for (var k = item.Col; k < item.Col + item.Span; k++) colBottom[k] = top + item.H + Gap;
        }
        bottom = colBottom.Length == 0 ? 0 : colBottom.Max();
        return result;
    }

    /// <summary>Cards without a saved position (older layouts) are packed into the highest free spots, in order.</summary>
    private void EnsurePositions()
    {
        var missing = Controls.Cast<Control>().Where(c => c.Visible && (!Cols.ContainsKey(c) || !Orders.ContainsKey(c))).ToList();
        if (missing.Count == 0) return;
        var columns = Columns;
        var placed = Controls.Cast<Control>().Where(c => c.Visible && !missing.Contains(c))
            .Select(c => (Col: Math.Clamp(Cols[c], 0, columns - SpanOf(c, columns)), Span: SpanOf(c, columns), Top: (int)(Orders[c] - CardHeight(c) / 2.0), Bottom: (int)(Orders[c] + CardHeight(c) / 2.0)))
            .ToList();
        foreach (var child in missing)
        {
            var span = SpanOf(child, columns);
            var h = CardHeight(child);
            int bestCol = 0, bestTop = 0;
            var found = false;
            foreach (var y in placed.Select(p => p.Bottom + Gap).Prepend(0).Distinct().OrderBy(v => v))
            {
                for (var c = 0; c + span <= columns && !found; c++)
                {
                    if (placed.All(p => p.Col + p.Span <= c || c + span <= p.Col || p.Bottom + Gap <= y || y + h + Gap <= p.Top)) { bestCol = c; bestTop = y; found = true; }
                }
                if (found) break;
            }
            placed.Add((bestCol, span, bestTop, bestTop + h));
            Cols[child] = bestCol;
            Orders[child] = bestTop + h / 2.0;
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (inGlideStep || ClientSize.Width - Padding.Horizontal <= 0) return;
        if (lifted is not null) { RearrangeAround(false); base.OnLayout(levent); return; }
        EnsurePositions();
        var targets = Arrange(Orders, null, out var bottom);
        // keep the order keys in step with where the cards really are
        foreach (var (child, r) in targets) Orders[child] = r.Y - Origin + r.Height / 2.0;
        var animate = AnimateNextLayout && Theme.Animations && IsHandleCreated && Visible;
        AnimateNextLayout = false;
        if (animate) Glide(targets);
        else
        {
            StopGlide();
            foreach (var (child, to) in targets) if (child.Bounds != to) child.SetBounds(to.X, to.Y, to.Width, to.Height);
        }
        var total = bottom + Padding.Vertical;
        if (AutoScrollMinSize.Height != total) AutoScrollMinSize = new Size(0, total);
        base.OnLayout(levent); // shows/sizes the scrollbars
    }

    // ---------------------------------------------------------------- gestures
    public void BeginMove(Control card, Point grabInCard) => Begin(card, false, grabInCard);

    public void BeginResize(Control card, Point cursorInCard) => Begin(card, true, new Point(card.Width - cursorInCard.X, card.Height - cursorInCard.Y));

    private void Begin(Control card, bool resize, Point offset)
    {
        StopGlide();
        if (glideTargets.Count > 0) foreach (var (c, r) in glideTargets) if (c.Bounds != r) c.Bounds = r; // finish a running glide instantly
        lifted = card; resizeMode = resize; grab = offset;
        frozenOrders = new Dictionary<Control, double>(Orders);
        var columns = Columns;
        liftedTarget = (Math.Clamp(Cols.TryGetValue(card, out var c0) ? c0 : 0, 0, columns - SpanOf(card, columns)), Orders.TryGetValue(card, out var o) ? o : 0, SpanOf(card, columns), CardHeight(card));
        card.BringToFront();
        if (card is Card lift) lift.Lifted = true;
    }

    /// <summary>Follows the cursor (grid client coordinates); call every frame while the button is held.</summary>
    public void DragTo(Point cursor)
    {
        var card = lifted;
        if (card is null) return;
        // scroll when the cursor is near the top or bottom edge
        var edge = 36;
        var scroll = cursor.Y < edge ? -Math.Min(24, (edge - cursor.Y) / 2 + 4) : cursor.Y > ClientSize.Height - edge ? Math.Min(24, (cursor.Y - ClientSize.Height + edge) / 2 + 4) : 0;
        if (scroll != 0 && VerticalScroll.Visible)
        {
            var max = Math.Max(0, DisplayRectangle.Height - ClientSize.Height);
            var next = Math.Clamp(-AutoScrollPosition.Y + scroll, 0, max);
            if (next != -AutoScrollPosition.Y) { inGlideStep = true; try { AutoScrollPosition = new Point(0, next); } finally { inGlideStep = false; } }
        }
        var columns = Columns;
        var colWidth = ColWidth(columns);
        if (!resizeMode)
        {
            var x = Math.Clamp(cursor.X - grab.X, 0, Math.Max(0, ClientSize.Width - card.Width));
            var y = Math.Max(Origin - 20, cursor.Y - grab.Y);
            inGlideStep = true;
            try { card.Location = new Point(x, y); } finally { inGlideStep = false; }
            var span = SpanOf(card, columns);
            var col = Math.Clamp((int)Math.Round((x - Padding.Left) / (colWidth + Gap)), 0, columns - span);
            // the dragged card goes above another card once its top edge is above that card's middle
            liftedTarget = (col, y - Origin + 1, span, liftedTarget.Height);
        }
        else
        {
            var right = cursor.X + grab.X; var bottomEdge = cursor.Y + grab.Y;
            var w = Math.Clamp(right - card.Left, (int)(colWidth * 0.6), ClientSize.Width - Padding.Right - card.Left);
            var h = Math.Clamp(bottomEdge - card.Top, MinCardHeight - 30, MaxCardHeight + 30);
            inGlideStep = true;
            try { card.Size = new Size(w, h); } finally { inGlideStep = false; }
            var col = liftedTarget.Col;
            var span = Math.Clamp((int)Math.Round((w + Gap) / (colWidth + Gap)), 1, columns - col);
            var snapped = Math.Clamp((int)Math.Round(h / (double)HeightSnap) * HeightSnap, MinCardHeight, MaxCardHeight);
            liftedTarget = (col, liftedTarget.Order, span, snapped);
        }
        RearrangeAround(true);
    }

    private void RearrangeAround(bool animate)
    {
        var card = lifted;
        if (card is null) return;
        var targets = Arrange(frozenOrders, (card, liftedTarget.Col, liftedTarget.Order, liftedTarget.Span, liftedTarget.Height), out var bottom);
        if (targets.TryGetValue(card, out var spot) && spot != placeholder)
        {
            var old = placeholder;
            placeholder = spot;
            Invalidate(Rectangle.Inflate(old, 4, 4)); Invalidate(Rectangle.Inflate(spot, 4, 4));
        }
        var others = targets.Where(t => t.Key != card).ToDictionary(t => t.Key, t => t.Value);
        var changed = others.Any(t => !glideTargets.TryGetValue(t.Key, out var r) || r != t.Value) || others.Count != glideTargets.Count(t => t.Key != card);
        if (changed)
        {
            if (animate && Theme.Animations) Glide(others);
            else foreach (var (c, r) in others) { inGlideStep = true; try { c.Bounds = r; } finally { inGlideStep = false; } }
            glideTargets = others;
        }
        var total = Math.Max(bottom, card.Bottom - Origin + Gap) + Padding.Vertical;
        if (AutoScrollMinSize.Height < total) AutoScrollMinSize = new Size(0, total);
    }

    /// <summary>Drops the lifted card on its spot. Returns its new column / span / height (null when nothing was lifted).</summary>
    public (int Col, int Span, int Height)? EndGesture()
    {
        var card = lifted;
        if (card is null) return null;
        var target = liftedTarget;
        Cols[card] = target.Col;
        if (resizeMode)
        {
            Spans[card] = target.Span >= Columns && Spans.TryGetValue(card, out var oldSpan) && oldSpan > Columns ? oldSpan : target.Span; // keep "full width" on a narrow window
            Heights[card] = target.Height;
        }
        Orders.Clear();
        foreach (var (c, o) in frozenOrders) Orders[c] = o;
        Orders[card] = target.Order;
        lifted = null;
        if (card is Card lift) lift.Lifted = false;
        var old = placeholder;
        placeholder = Rectangle.Empty;
        Invalidate(Rectangle.Inflate(old, 4, 4));
        glideTargets = new();
        AnimateNextLayout = true;
        PerformLayout();
        return (target.Col, target.Span, target.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (placeholder.IsEmpty) return;
        var p = Theme.P;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(placeholder.X + 1.5f, placeholder.Y + 1.5f, placeholder.Width - 3, placeholder.Height - 3);
        using var path = Ui.Rounded(r, Theme.R(14));
        using (var fill = new SolidBrush(ColorX.Alpha(p.Accent, 26))) g.FillPath(fill, path);
        using var pen = new Pen(ColorX.Alpha(p.Accent, 200), 2f) { DashStyle = DashStyle.Dash };
        g.DrawPath(pen, path);
    }
}

/// <summary>Sidebar navigation entry: glyph, label, selection bar and an optional badge.</summary>
internal sealed class NavItem : ThemedControl
{
    private bool selected;
    private int badge;
    private Led badgeTone;
    public string Glyph { get; set; } = "";
    public bool Mini { get; set; }
    public bool Selected { get => selected; set { selected = value; Invalidate(); } }
    /// <summary>Badge count; tone Idle = neutral, Warn/Err = colored.</summary>
    public void SetBadge(int count, Led? tone) { badge = count; badgeTone = tone ?? Led.Idle; Invalidate(); }

    public NavItem() { Height = 40; Cursor = Cursors.Hand; Margin = new Padding(0, 0, 0, 2); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P; var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        if (selected) Ui.FillRound(g, ColorX.Mix(p.Panel, p.Accent, 0.16), bounds, Theme.R(9), ColorX.Mix(p.Line, p.Accent, 0.35));
        else if (HoverT > 0.01f) Ui.FillRound(g, ColorX.Mix(Ui.SolidBack(Parent), ColorX.Mix(p.Panel, p.Field, 0.8), HoverT), bounds, Theme.R(9));
        if (selected) Ui.FillRound(g, p.Accent, new RectangleF(0, 9, 3.5f, Height - 18), 2);
        var ink = selected ? p.Ink : ColorX.Mix(p.Muted, p.Ink, HoverT);
        var glyphRect = Mini ? new Rectangle(0, 0, Width, Height) : new Rectangle(10, 0, 24, Height);
        Ui.DrawGlyph(g, Glyph, 11.5f, selected ? p.Accent : ink, glyphRect);
        if (!Mini) Ui.Text(g, Text, selected ? Fonts.Semibold(9.75f) : Fonts.Body(9.75f), new Rectangle(42, 0, Width - 84, Height), ink);
        if (badge > 0)
        {
            var color = badgeTone == Led.Err ? p.Err : badgeTone == Led.Warn ? p.Warn : ColorX.Mix(p.Field, p.Accent, 0.35);
            var label = badge > 99 ? "99+" : badge.ToString();
            var w = Math.Max(18, TextRenderer.MeasureText(label, Fonts.Semibold(7.5f)).Width + 6);
            var r = Mini ? new Rectangle(Width - w - 2, 4, w, 16) : new Rectangle(Width - w - 10, (Height - 18) / 2, w, 18);
            Ui.FillRound(g, color, r, r.Height / 2f);
            Ui.Text(g, label, Fonts.Semibold(7.5f), r, ColorX.Luma(color) > 0.5 ? Color.Black : Color.White, TextFormatFlags.HorizontalCenter);
        }
    }
}

public enum Led { Idle, Ok, Warn, Err, Busy }

/// <summary>Rounded status chip with a glowing LED dot.</summary>
internal sealed class StatusPill : ThemedControl
{
    private Led state;
    private bool compact;
    /// <summary>Label used when space is tight (full text goes in the tooltip).</summary>
    public string ShortText { get; set; } = "";
    public bool Compact { get => compact; set { if (compact == value) return; compact = value; Width = GetPreferredSize(Size.Empty).Width; Invalidate(); } }
    private string Shown => compact && ShortText.Length > 0 ? ShortText : Text;
    public Led State { get => state; set { state = value; Invalidate(); } }

    public StatusPill() { Height = 30; Margin = new Padding(6, 0, 0, 0); Font = Fonts.Body(9f); }

    public override Size GetPreferredSize(Size proposedSize) =>
        new(TextRenderer.MeasureText(Shown, Font).Width + 34, 30);

    protected override void OnTextChanged(EventArgs e) { Width = GetPreferredSize(Size.Empty).Width; base.OnTextChanged(e); }

    public static Color LedColor(Led led) => led switch
    {
        Led.Ok => Theme.P.Ok, Led.Warn => Theme.P.Warn, Led.Err => Theme.P.Err, Led.Busy => Theme.P.Accent, _ => Theme.P.Dim
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P; var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Ui.FillRound(g, p.Panel2, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), (Height - 1) / 2f, p.Border);
        var led = LedColor(state);
        var cy = Height / 2f;
        if (state != Led.Idle)
            using (var glow = new SolidBrush(ColorX.Alpha(led, 70))) g.FillEllipse(glow, 7.5f, cy - 7, 14, 14);
        using (var dot = new SolidBrush(led)) g.FillEllipse(dot, 10.5f, cy - 4, 8, 8);
        var ink = state switch { Led.Ok => ColorX.Mix(p.Ok, p.Ink, 0.45), Led.Warn => ColorX.Mix(p.Warn, p.Ink, 0.35), Led.Err => ColorX.Mix(p.Err, p.Ink, 0.35), _ => p.Muted };
        Ui.Text(g, Shown, Font, new Rectangle(24, 0, Width - 28, Height), ink);
    }
}

/// <summary>Small KPI tile: uppercase caption over a big number.</summary>
internal sealed class KpiTile : ThemedControl
{
    private string caption = "", value = "—";
    private Color? valueColor;
    public string Caption { get => caption; set { caption = value; Invalidate(); } }
    public string Value { get => value; set { this.value = value; Invalidate(); } }
    public Color? ValueColor { get => valueColor; set { valueColor = value; Invalidate(); } }

    public KpiTile() { Size = new Size(118, 58); Margin = new Padding(0, 0, 8, 0); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P; var g = e.Graphics;
        Ui.FillRound(g, p.Panel2, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.R(10), p.Line);
        Ui.Text(g, caption.ToUpperInvariant(), Fonts.Body(7.5f, FontStyle.Bold), new Rectangle(12, 6, Width - 18, 16), p.Dim);
        Ui.Text(g, value, Fonts.Display(16f), new Rectangle(11, 22, Width - 16, 30), valueColor ?? p.Ink);
    }
}

/// <summary>Pill-shaped on/off switch (a CheckBox underneath, so Checked/CheckedChanged keep working).</summary>
internal sealed class ToggleSwitch : CheckBox, ISelfBackground
{
    private float knob;
    private Tween? anim;
    private bool hovered;
    public bool Danger { get; set; }

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        AutoSize = false;
        Size = new Size(44, 26);
        Margin = new Padding(0);
        Font = Fonts.Body(9.5f);

    }

    public override Size GetPreferredSize(Size proposedSize) =>
        Text.Length == 0 ? new Size(44, 26) : new Size(52 + TextRenderer.MeasureText(Text, Font).Width, 26);

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        if (anim is null) { anim = new Tween(this, 0.16); anim.Changed += () => { knob = anim.Eased; Invalidate(); }; anim.Snap(Checked ? 0 : 1); }
        if (Theme.Animations && IsHandleCreated && Visible) anim.To(Checked ? 1 : 0); else { anim.Snap(Checked ? 1 : 0); knob = Checked ? 1 : 0; Invalidate(); }
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); knob = Checked ? 1 : 0; }
    protected override void OnMouseEnter(EventArgs eventargs) { hovered = true; Invalidate(); base.OnMouseEnter(eventargs); }
    protected override void OnMouseLeave(EventArgs eventargs) { hovered = false; Invalidate(); base.OnMouseLeave(eventargs); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Ui.PaintBack(e.Graphics, this);
        var p = Theme.P; var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(1, (Height - 22) / 2f, 40, 22);
        var on = Danger ? p.Err : p.Accent;
        var fill = ColorX.Mix(ColorX.Mix(p.Field, p.Well, 0.3), on, knob);
        var border = ColorX.Mix(hovered ? ColorX.Mix(p.Border, p.Accent, 0.5) : p.Border, on, knob);
        if (!Enabled) { fill = ColorX.Mix(fill, p.Panel, 0.6); border = p.Line; }
        Ui.FillRound(g, fill, track, 11, border);
        var knobColor = ColorX.Mix(p.Muted, Danger ? Color.White : p.AccentInk, knob);
        var x = track.X + 4 + knob * (track.Width - 22);
        using (var brush = new SolidBrush(Enabled ? knobColor : p.Dim)) g.FillEllipse(brush, x, track.Y + 4, 14, 14);
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(ColorX.Alpha(p.Accent, 160), 1.5f);
            using var path = Ui.Rounded(RectangleF.Inflate(track, 2.5f, 2.5f), 13);
            g.DrawPath(pen, path);
        }
        if (Text.Length > 0) Ui.Text(g, Text, Font, new Rectangle(50, 0, Width - 50, Height), Enabled ? p.Ink : p.Dim);
    }


}

/// <summary>A rounded, themed host around a borderless TextBox (optional leading glyph and clear button).</summary>
internal class FieldBox : ThemedControl
{
    public TextBox Inner { get; } = new() { BorderStyle = BorderStyle.None };
    public string LeadingGlyph { get; set; } = "";
    public bool ClearButton { get; set; }
    protected int TrailingWidth;
    private bool clearHover;

    public FieldBox()
    {
        Height = 34; Cursor = Cursors.IBeam; Margin = new Padding(0);
        Inner.Font = Fonts.Body(9.75f);
        Controls.Add(Inner);
        Inner.GotFocus += (_, _) => Invalidate();
        Inner.LostFocus += (_, _) => Invalidate();
        Inner.TextChanged += (_, _) => { OnTextChanged(EventArgs.Empty); Invalidate(); };
        Inner.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && ClearButton && Inner.TextLength > 0) { Inner.Clear(); e.SuppressKeyPress = true; } };
        Theme.Changed += Recolor;
        Recolor();
    }

    private void Recolor() { Inner.BackColor = Theme.P.Well; Inner.ForeColor = Theme.P.Ink; Invalidate(); }

#pragma warning disable CS8765
    public override string Text { get => Inner.Text; set => Inner.Text = value ?? ""; }
#pragma warning restore CS8765
    public string Placeholder { get => Inner.PlaceholderText; set => Inner.PlaceholderText = value; }
    public bool Multiline { get => Inner.Multiline; set { Inner.Multiline = value; Inner.ScrollBars = value ? ScrollBars.Vertical : ScrollBars.None; Inner.AcceptsReturn = value; if (value) Ui.DarkScrollbars(Inner); PerformLayout(); } }
    public bool Password { get => Inner.UseSystemPasswordChar; set => Inner.UseSystemPasswordChar = value; }

    private Rectangle ClearBounds => new(Width - 30 - TrailingWidth, (Height - 24) / 2, 24, 24);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var left = LeadingGlyph.Length > 0 ? 32 : 11;
        var right = 10 + TrailingWidth + (ClearButton ? 26 : 0);
        if (Inner.Multiline) Inner.SetBounds(left, 8, Math.Max(10, Width - left - right), Math.Max(10, Height - 16));
        else Inner.SetBounds(left, (Height - Inner.PreferredHeight) / 2 + 1, Math.Max(10, Width - left - right), Inner.PreferredHeight);
    }

    protected override void OnClick(EventArgs e) { base.OnClick(e); Inner.Focus(); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hover = ClearButton && Inner.TextLength > 0 && ClearBounds.Contains(e.Location);
        if (hover != clearHover) { clearHover = hover; Invalidate(); }
        Cursor = hover ? Cursors.Hand : Cursors.IBeam;
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (ClearButton && Inner.TextLength > 0 && ClearBounds.Contains(e.Location)) { Inner.Clear(); Inner.Focus(); return; }
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P; var g = e.Graphics;
        var focused = Inner.Focused;
        var border = !Enabled ? p.Line : focused ? p.Accent : Hovered ? ColorX.Mix(p.Border, p.Ink, 0.15) : p.Border;
        Ui.FillRound(g, p.Well, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.R(8), border, focused ? 1.5f : 1f);
        if (LeadingGlyph.Length > 0) Ui.DrawGlyph(g, LeadingGlyph, 10f, focused ? p.Accent : p.Dim, new Rectangle(8, Inner.Multiline ? 6 : 0, 20, Inner.Multiline ? 22 : Height));
        if (ClearButton && Inner.TextLength > 0)
        {
            if (clearHover) Ui.FillRound(g, p.Hover, ClearBounds, Theme.R(6));
            Ui.DrawGlyph(g, Glyph.Close, 8f, clearHover ? p.Ink : p.Muted, ClearBounds);
        }
        PaintTrailing(g);
    }

    protected virtual void PaintTrailing(Graphics g) { }

    protected override void Dispose(bool disposing) { if (disposing) Theme.Changed -= Recolor; base.Dispose(disposing); }
}

/// <summary>Numeric field with − / + steppers and mouse-wheel support.</summary>
internal sealed class NumberBox : FieldBox
{
    private decimal value;
    private int hoverStep; // -1 minus, 1 plus
    public decimal Minimum { get; }
    public decimal Maximum { get; }
    public event EventHandler? ValueChanged;

    public NumberBox(decimal min, decimal max)
    {
        Minimum = min; Maximum = max; TrailingWidth = 52; Width = 150;
        Inner.KeyPress += (_, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
        Inner.Leave += (_, _) => Commit();
        Inner.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Up) { Step(1); e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { Step(-1); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { Commit(); e.Handled = true; }
        };
        Inner.MouseWheel += (_, e) => { if (Inner.Focused) Step(Math.Sign(e.Delta)); };
        Value = min;
    }

    public decimal Value
    {
        get { Commit(); return value; }
        set
        {
            var clamped = Math.Clamp(value, Minimum, Maximum);
            var changed = clamped != this.value;
            this.value = clamped;
            var text = clamped.ToString("0");
            if (Inner.Text != text) Inner.Text = text;
            if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Commit()
    {
        if (decimal.TryParse(Inner.Text, out var parsed)) { if (Math.Clamp(parsed, Minimum, Maximum) != value || Inner.Text != Math.Clamp(parsed, Minimum, Maximum).ToString("0")) Value = parsed; }
        else Inner.Text = value.ToString("0");
    }

    private void Step(int delta) { Commit(); Value = value + delta; }

    private Rectangle Minus => new(Width - 56, 4, 24, Height - 8);
    private Rectangle Plus => new(Width - 30, 4, 24, Height - 8);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var step = Minus.Contains(e.Location) ? -1 : Plus.Contains(e.Location) ? 1 : 0;
        if (step != hoverStep) { hoverStep = step; Invalidate(); }
        base.OnMouseMove(e);
        if (step != 0) Cursor = Cursors.Hand;
    }

    protected override void OnMouseLeave(EventArgs e) { hoverStep = 0; base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Minus.Contains(e.Location)) { Step(-1); return; }
        if (Plus.Contains(e.Location)) { Step(1); return; }
        base.OnMouseDown(e);
    }

    protected override void PaintTrailing(Graphics g)
    {
        var p = Theme.P;
        foreach (var (rect, glyph, step) in new[] { (Minus, Glyph.Remove, -1), (Plus, Glyph.Add, 1) })
        {
            if (hoverStep == step) Ui.FillRound(g, p.Hover, rect, Theme.R(6));
            Ui.DrawGlyph(g, glyph, 8.5f, hoverStep == step ? p.Accent : p.Muted, rect);
        }
    }
}

/// <summary>Toggleable filter chip with an optional count and tone color.</summary>
internal sealed class Chip : ThemedControl
{
    private bool selected;
    private int? count;
    private string glyph = "";
    public Color? Tone { get; set; }
    public string Glyph { get => glyph; set { glyph = value; Width = GetPreferredSize(Size.Empty).Width; Invalidate(); } }
    public bool Selected { get => selected; set { selected = value; Invalidate(); } }
    public int? Count { get => count; set { count = value; Width = GetPreferredSize(Size.Empty).Width; Invalidate(); } }

    public Chip() { Height = 30; Cursor = Cursors.Hand; Margin = new Padding(0, 0, 6, 0); Font = Fonts.Body(9f); }

    private string Label => count is null ? Text : $"{Text}  {count}";

    public override Size GetPreferredSize(Size proposedSize) =>
        new(TextRenderer.MeasureText(Label, Font).Width + (Glyph.Length > 0 ? 40 : 24), 30);

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Width = GetPreferredSize(Size.Empty).Width; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P; var g = e.Graphics;
        var tone = Tone ?? p.Accent;
        var fill = selected ? ColorX.Mix(p.Field, tone, 0.24) : ColorX.Mix(p.Panel2, p.Hover, HoverT);
        var border = selected ? tone : ColorX.Mix(p.Border, ColorX.Mix(p.Border, tone, 0.5), HoverT);
        Ui.FillRound(g, fill, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), (Height - 1) / 2f, border);
        var x = 12;
        if (Glyph.Length > 0) { Ui.DrawGlyph(g, Glyph, 9f, selected ? tone : p.Muted, new Rectangle(x, 0, 16, Height)); x += 20; }
        Ui.Text(g, Label, Font, new Rectangle(x, 0, Width - x - 8, Height), selected ? ColorX.Mix(tone, p.Ink, 0.55) : p.Muted);
    }
}

/// <summary>Indeterminate activity bar that glides in the accent color.</summary>
internal sealed class SoftProgressBar : ThemedControl
{
    private int frame;
    private bool active;

    public bool Active
    {
        get => active;
        set
        {
            if (active == value) return;
            active = value;
            if (active) FrameClock.Subscribe(this, Glide); else { FrameClock.Unsubscribe(Glide); frame = 0; }
            Invalidate();
        }
    }

    public SoftProgressBar()
    {

        MinimumSize = new Size(60, 8);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P; var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(0, Math.Max(0, Height / 2f - 3), Math.Max(1, Width - 1), 6);
        using var trackPath = Ui.Rounded(track, 3);
        using (var trackBrush = new SolidBrush(p.Well)) g.FillPath(trackBrush, trackPath);
        if (!Active) return;
        var oldClip = g.Clip;
        g.SetClip(trackPath);
        if (!Theme.Animations)
        {
            using var flat = new SolidBrush(ColorX.Mix(p.Well, p.Accent, 0.65));
            g.FillRectangle(flat, track);
        }
        else
        {
            var chunkWidth = Math.Min(200, Math.Max(80, Width / 4));
            var chunk = new RectangleF(frame - chunkWidth, track.Y, chunkWidth, track.Height);
            using var glow = new LinearGradientBrush(new RectangleF(chunk.X - 1, chunk.Y, chunk.Width + 2, chunk.Height), Color.Transparent, p.Accent, LinearGradientMode.Horizontal);
            glow.InterpolationColors = new ColorBlend { Colors = [ColorX.Alpha(p.Accent, 0), p.AccentHi, ColorX.Alpha(p.Accent2, 0)], Positions = [0f, .5f, 1f] };
            g.FillRectangle(glow, chunk);
        }
        g.Clip = oldClip;
    }

    private double glide;
    private void Glide(double dt) { glide = (glide + dt * 240) % Math.Max(1, Width + 180); frame = (int)glide; Invalidate(); }
    protected override void Dispose(bool disposing) { if (disposing) FrameClock.Unsubscribe(Glide); base.Dispose(disposing); }
}

/// <summary>Owner-drawn dark drop-down list.</summary>
internal sealed class DarkComboBox : ComboBox
{
    public DarkComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        ItemHeight = 24;
        Font = Fonts.Body(9.75f);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.P.Well; ForeColor = Theme.P.Ink;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Ui.SetWindowTheme(Handle, "DarkMode_CFD", null); } catch { }
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var p = Theme.P;
        var selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using var background = new SolidBrush(selected ? p.Sel : p.Well);
        e.Graphics.FillRectangle(background, e.Bounds);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, Rectangle.Inflate(e.Bounds, -6, 0),
            p.Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Plain, opaque content host in the window background color.</summary>
internal sealed class GlowHost : Panel
{
    public GlowHost()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        DoubleBuffered = true;
        BackColor = Theme.P.Bg;
        Theme.Changed += () => BackColor = Theme.P.Bg; // before children re-inherit their color
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (BackColor != Theme.P.Bg) BackColor = Theme.P.Bg;
        using var bg = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(bg, e.ClipRectangle);
    }
}

/// <summary>
/// Page title area: eyebrow, title and subtitle drawn directly, over a two-color glow that is rendered
/// once into a cached bitmap (re-rendered only on resize or theme change). Child pills paint from it.
/// </summary>
internal sealed class PageHeader : Control, IBackdrop
{
    private Bitmap? cache;
    private string cacheKey = "";
    private string eyebrow = "", title = "", subtitle = "";
    public int GlowExtentLeft { get; set; }

    public PageHeader()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        DoubleBuffered = true;
        Theme.Changed += Invalidate;
    }

    /// <summary>Right edge of the page title, so pills can avoid it.</summary>
    public int TitleRight => TextRenderer.MeasureText(title, Fonts.Display(25f)).Width + 24;

    public void SetText(string eyebrowText, string titleText, string subtitleText)
    {
        eyebrow = eyebrowText; title = titleText; subtitle = subtitleText;
        Invalidate();
    }

    private Bitmap Backdrop()
    {
        var p = Theme.P;
        var key = $"{Width}x{Height}{p.Bg}{p.Glow}{p.Glow2}{Theme.Glow}{p.Fx}";
        if (cache is not null && key == cacheKey) return cache;
        cache?.Dispose();
        cache = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
        cacheKey = key;
        using var g = Graphics.FromImage(cache);
        g.Clear(p.Bg);
        if (Theme.Glow && Width > 10)
        {
            void Glow(Color color, float cx, float w)
            {
                using var path = new GraphicsPath();
                var rect = new RectangleF(cx - w / 2, -Height * 1.6f, w, Height * 2.6f);
                path.AddEllipse(rect);
                using var brush = new PathGradientBrush(path) { CenterColor = color, SurroundColors = [ColorX.Alpha(p.Bg, 0)] };
                g.FillPath(brush, path);
            }
            g.SmoothingMode = SmoothingMode.HighQuality;
            if (p.Duotone) { Glow(p.Glow, Width * 0.38f, Width * 0.75f); Glow(p.Glow2, Width * 0.82f, Width * 0.7f); }
            else Glow(p.Glow, Width * 0.6f, Width * 1.0f);
        }
        if (Theme.Glow && p.Fx.Length > 0 && Width > 10) DrawFx(g, p, Width, Height);
        return cache;
    }

    /// <summary>Theme backdrop effects. Drawn once into the cached bitmap, so they cost nothing while the app runs.</summary>
    private static void DrawFx(Graphics g, Palette p, int w, int h)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rnd = new Random(1234); // fixed seed: the same pattern every time
        Color A(Color c, int alpha) => ColorX.Alpha(c, alpha);
        switch (p.Fx)
        {
            case "stars":
                for (var i = 0; i < w / 9; i++)
                {
                    var x = rnd.Next(w); var y = rnd.Next(h); var s = rnd.NextDouble() < 0.12 ? 2.6f : 1.4f;
                    using var b = new SolidBrush(A(rnd.Next(4) == 0 ? p.Accent2 : p.Ink, 40 + rnd.Next(110)));
                    g.FillEllipse(b, x, y, s, s);
                }
                break;
            case "grid": // retro horizon grid fading in from the bottom
                using (var pen = new Pen(A(p.Accent2, 46), 1f))
                {
                    var horizon = h * 0.15f;
                    for (var i = -24; i <= 24; i++) g.DrawLine(pen, w / 2f + i * 18, horizon, w / 2f + i * w / 10f, h);
                    for (var k = 1; k < 9; k++) { var y = horizon + (h - horizon) * (float)Math.Pow(k / 8.0, 1.8); g.DrawLine(pen, 0, y, w, y); }
                }
                break;
            case "scan":
                using (var pen = new Pen(A(p.Accent, 16))) for (var y = 0; y < h; y += 3) g.DrawLine(pen, 0, y, w, y);
                break;
            case "rays":
                using (var b = new SolidBrush(A(p.Accent, 18)))
                {
                    var c = new PointF(w * 0.78f, -h * 0.4f);
                    for (var i = 0; i < 18; i++)
                    {
                        var a0 = Math.PI * (0.05 + i / 18.0 * 0.95); var a1 = a0 + Math.PI / 60;
                        var r = w * 1.2f;
                        g.FillPolygon(b, [c, new PointF(c.X + (float)Math.Cos(a0) * r, c.Y + (float)Math.Sin(a0) * r), new PointF(c.X + (float)Math.Cos(a1) * r, c.Y + (float)Math.Sin(a1) * r)]);
                    }
                }
                break;
            case "waves":
                for (var k = 0; k < 5; k++)
                {
                    using var pen = new Pen(A(k % 2 == 0 ? p.Accent : p.Accent2, 34 - k * 4), 1.4f);
                    var pts = new PointF[w / 8 + 2];
                    for (var i = 0; i < pts.Length; i++)
                    {
                        var x = i * 8f;
                        pts[i] = new PointF(x, h * (0.35f + k * 0.13f) + (float)Math.Sin(x / (90 + k * 25) + k) * (6 + k * 2));
                    }
                    g.DrawCurve(pen, pts);
                }
                break;
            case "rain":
                using (var pen = new Pen(A(p.Accent2, 30), 1f))
                    for (var i = 0; i < w / 14; i++) { var x = rnd.Next(w + 40); var y = rnd.Next(h); var len = 8 + rnd.Next(14); g.DrawLine(pen, x, y, x - len * 0.35f, y + len); }
                break;
            case "embers":
                for (var i = 0; i < w / 16; i++)
                {
                    var x = rnd.Next(w); var y = (float)(h * Math.Sqrt(rnd.NextDouble())); var s = 1.5f + (float)rnd.NextDouble() * 2.5f;
                    var color = rnd.Next(3) == 0 ? p.Accent2 : p.Accent;
                    using (var halo = new SolidBrush(A(color, 18))) g.FillEllipse(halo, x - s * 2, y - s * 2, s * 5, s * 5);
                    using (var core = new SolidBrush(A(color, 70 + rnd.Next(80)))) g.FillEllipse(core, x, y, s, s);
                }
                break;
        }
    }

    public void PaintBackdrop(Graphics g, Rectangle childBounds) =>
        g.DrawImage(Backdrop(), new Rectangle(0, 0, childBounds.Width, childBounds.Height), childBounds, GraphicsUnit.Pixel);

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.DrawImageUnscaled(Backdrop(), 0, 0);

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P; var g = e.Graphics;
        Ui.Text(g, eyebrow, Fonts.Mono(8.25f, FontStyle.Bold), new Rectangle(2, 4, Width / 2, 18), p.Accent2);
        Ui.Text(g, title, Fonts.Display(25f), new Rectangle(0, 20, Width - 40, 44), p.Ink);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Theme.Changed -= Invalidate; cache?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>Theme gallery: draws every theme as a mini window preview; click to apply.</summary>
internal sealed class SwatchGrid : ThemedControl, IHeightForWidth
{
    private const int TileW = 138, TileH = 86, Gap = 10, GroupH = 26;
    private readonly List<(ThemeDef Def, Palette Pal)> items;
    private readonly List<(Rectangle Bounds, int Index)> hits = new();
    private int hover = -1;
    public string SelectedId { get; set; } = "";
    public event Action<ThemeDef>? Picked;

    public SwatchGrid()
    {
        items = ThemeCatalog.All.Select(d => (d, Palette.From(d))).ToList();
        Cursor = Cursors.Hand;
    }

    public int HeightFor(int width)
    {
        var perRow = Math.Max(1, (width + Gap) / (TileW + Gap));
        var total = 0;
        foreach (var group in ThemeCatalog.Groups)
        {
            var count = items.Count(i => i.Def.Group == group);
            total += GroupH + (int)Math.Ceiling(count / (double)perRow) * (TileH + Gap);
        }
        return total;
    }

    // All tiles are rendered once into a cached image (re-rendered only on resize, theme or selection change).
    // Painting every anti-aliased mini window on each scroll step made scrolling past the gallery stutter.
    private Bitmap? cache;
    private string cacheKey = "";

    private void EnsureCache()
    {
        var p = Theme.P;
        var key = $"{Width}x{Height}|{SelectedId}|{p.Bg}|{p.Panel}|{p.Accent}|{p.Dim}|{Fonts.Body(8.5f).Name}";
        if (cache is not null && key == cacheKey) return;
        cache?.Dispose();
        cache = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
        cacheKey = key;
        using var g = Graphics.FromImage(cache);
        Ui.PaintBack(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        hits.Clear();
        var perRow = Math.Max(1, (Width + Gap) / (TileW + Gap));
        var y = 0;
        foreach (var group in ThemeCatalog.Groups)
        {
            Ui.Text(g, group.ToUpperInvariant(), Fonts.Mono(7.75f, FontStyle.Bold), new Rectangle(0, y, Width, GroupH - 6), p.Dim);
            y += GroupH;
            var col = 0;
            for (var index = 0; index < items.Count; index++)
            {
                var (def, pal) = items[index];
                if (def.Group != group) continue;
                var rect = new Rectangle(col * (TileW + Gap), y, TileW, TileH);
                hits.Add((rect, index));
                DrawTile(g, rect, def, pal, def.Id.Equals(SelectedId, StringComparison.OrdinalIgnoreCase), false);
                if (++col >= perRow) { col = 0; y += TileH + Gap; }
            }
            if (col != 0) y += TileH + Gap;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { } // the cached image includes the background

    protected override void OnPaint(PaintEventArgs e)
    {
        EnsureCache();
        var g = e.Graphics;
        g.DrawImage(cache!, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
        var hit = hover >= 0 ? hits.FirstOrDefault(h => h.Index == hover) : default;
        if (hover >= 0 && !hit.Bounds.IsEmpty && hit.Bounds.IntersectsWith(e.ClipRectangle))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var (def, pal) = items[hover];
            DrawTile(g, hit.Bounds, def, pal, def.Id.Equals(SelectedId, StringComparison.OrdinalIgnoreCase), true);
        }
    }

    private void InvalidateTile(int index)
    {
        if (index < 0) return;
        var hit = hits.FirstOrDefault(h => h.Index == index);
        if (!hit.Bounds.IsEmpty) Invalidate(Rectangle.Inflate(hit.Bounds, 2, 2));
    }

    protected override void Dispose(bool disposing) { if (disposing) cache?.Dispose(); base.Dispose(disposing); }

    private static void DrawTile(Graphics g, Rectangle r, ThemeDef def, Palette t, bool selected, bool hover)
    {
        var p = Theme.P;
        Ui.FillRound(g, t.Bg, r, Theme.R(10), selected ? p.Accent : hover ? t.Accent : t.Border, selected ? 2.5f : 1f);
        // mini sidebar + card
        Ui.FillRound(g, t.Panel, new RectangleF(r.X + 7, r.Y + 8, 22, r.Height - 34), 4);
        using (var acc = new SolidBrush(t.Accent)) g.FillRectangle(acc, r.X + 11, r.Y + 14, 14, 4);
        using (var mutedBrush = new SolidBrush(t.Dim)) { g.FillRectangle(mutedBrush, r.X + 11, r.Y + 23, 14, 3); g.FillRectangle(mutedBrush, r.X + 11, r.Y + 30, 14, 3); }
        Ui.FillRound(g, t.Panel2, new RectangleF(r.X + 34, r.Y + 8, r.Width - 41, r.Height - 34), 4, t.Line);
        using (var ink = new SolidBrush(t.Ink)) g.FillRectangle(ink, r.X + 40, r.Y + 14, 44, 4);
        using (var muted = new SolidBrush(t.Muted)) g.FillRectangle(muted, r.X + 40, r.Y + 22, 66, 3);
        Ui.FillRound(g, t.Accent, new RectangleF(r.X + 40, r.Y + 32, 34, 12), 4);
        if (t.Duotone) Ui.FillRound(g, t.Accent2, new RectangleF(r.X + 78, r.Y + 32, 14, 12), 4);
        Ui.Text(g, def.Name, Fonts.Body(8.5f, selected ? FontStyle.Bold : FontStyle.Regular), new Rectangle(r.X + 8, r.Bottom - 24, r.Width - 30, 20), t.Ink);
        if (selected) Ui.DrawGlyph(g, Glyph.Check, 8.5f, t.Accent, new Rectangle(r.Right - 24, r.Bottom - 24, 18, 20));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        EnsureCache();
        var hit = hits.FirstOrDefault(h => h.Bounds.Contains(e.Location));
        var index = hit.Bounds.IsEmpty ? -1 : hit.Index;
        if (index != hover) { InvalidateTile(hover); hover = index; InvalidateTile(hover); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { InvalidateTile(hover); hover = -1; base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        EnsureCache();
        var hit = hits.FirstOrDefault(h => h.Bounds.Contains(e.Location));
        if (hit.Bounds.IsEmpty) return;
        SelectedId = items[hit.Index].Def.Id;
        Invalidate();
        Picked?.Invoke(items[hit.Index].Def);
    }
}

/// <summary>A FlowLayoutPanel that is transparent and double-buffered.</summary>
/// <summary>Toolbar that wraps onto extra lines when the window is narrow (never overlaps).</summary>
internal sealed class WrapRow : FlowLayoutPanel
{
    public WrapRow()
    {
        DoubleBuffered = true; WrapContents = true; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top; Margin = new Padding(0); Padding = new Padding(0, 2, 0, 4); FlowDirection = FlowDirection.LeftToRight;
    }

    /// <summary>Moves the controls of existing rows into this one, in visual order, with even spacing.</summary>
    public WrapRow Absorb(params FlowLayoutPanel[] rows)
    {
        foreach (var row in rows)
        {
            var items = row.Controls.Cast<Control>().ToList();
            if (row.FlowDirection == FlowDirection.RightToLeft) items.Reverse();
            foreach (var c in items)
            {
                row.Controls.Remove(c);
                c.Margin = new Padding(0, 0, 8, 8);
                if (c is ToggleSwitch t) { t.Margin = new Padding(4, 4, 12, 8); t.Width = t.GetPreferredSize(Size.Empty).Width; }
                if (c is System.Windows.Forms.Label l) l.Margin = new Padding(4, 9, 12, 8);
                Controls.Add(c);
            }
            row.Dispose();
        }
        return this;
    }
}

internal sealed class Row : FlowLayoutPanel
{
    public Row(bool rightToLeft = false)
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent; DoubleBuffered = true;
        WrapContents = false; AutoSize = false; Margin = new Padding(0); Padding = new Padding(0);
        FlowDirection = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }
}
