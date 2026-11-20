using System.Runtime.InteropServices;

/// <summary>
/// Screen capture that includes layered windows (CAPTUREBLT). Graphics.CopyFromScreen uses plain SRCCOPY,
/// which leaves out layered windows such as the cross-fade overlay - so it can never see a flash they cause.
/// </summary>
internal static class ScreenGrab
{
    public static void Copy(Graphics target, Rectangle source)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var dc = target.GetHdc();
        try { BitBlt(dc, 0, 0, source.Width, source.Height, screenDc, source.X, source.Y, 0x00CC0020 | 0x40000000); }
        finally { target.ReleaseHdc(dc); ReleaseDC(IntPtr.Zero, screenDc); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
}
