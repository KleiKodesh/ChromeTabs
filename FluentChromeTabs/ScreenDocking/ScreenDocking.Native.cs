using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FluentChromeTabs
{
    public static partial class ScreenDocking
    {
        // A window's rectangle includes invisible resize borders; what the eye sees is the
        // DWM frame. Halves are measured and set by what is visible, so the two windows
        // meet edge to edge with no gap.
        private static Rectangle VisibleBounds(Form form)
        {
            RECT frame;
            if (DwmGetWindowAttribute(form.Handle, DWMWA_EXTENDED_FRAME_BOUNDS, out frame, Marshal.SizeOf(typeof(RECT))) == 0
                && frame.Right > frame.Left)
                return Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
            return form.Bounds;
        }

        private static void SetVisibleBounds(Form form, Rectangle visible)
        {
            Rectangle outer = form.Bounds, seen = VisibleBounds(form);
            form.Bounds = Rectangle.FromLTRB(
                visible.Left - (seen.Left - outer.Left),
                visible.Top - (seen.Top - outer.Top),
                visible.Right + (outer.Right - seen.Right),
                visible.Bottom + (outer.Bottom - seen.Bottom));
        }

        // ── Win32 ─────────────────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        public const int WMSZ_LEFT = 1, WMSZ_RIGHT = 2, WMSZ_TOP = 3, WMSZ_TOPLEFT = 4, WMSZ_TOPRIGHT = 5, WMSZ_BOTTOM = 6, WMSZ_BOTTOMLEFT = 7, WMSZ_BOTTOMRIGHT = 8;
        private const uint GW_HWNDNEXT = 2;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetTopWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
