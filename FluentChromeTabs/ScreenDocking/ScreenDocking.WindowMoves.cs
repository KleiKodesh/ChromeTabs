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
    // A whole window moved by its title bar: over another window's strip its tabs go in there
    // (a tool window becomes a tab); let go anywhere else it takes the place nearest the cursor.
    public static partial class ScreenDocking
    {
        // ── A whole window moved by its title bar ───────────────────

        // The window moved by its title bar now (one at a time: there is one mouse), and whether it
        // has moved since the move began (a resize ends the same way).
        private static Form _movingWindow;

        // A window moved by its title bar reacts only over another of our windows: its strip takes
        // the moved window's tabs, its edge zones dock the moved window beside it (with the same
        // preview as a tab). Nothing else - no screen zones, no window middles - so an ordinary move
        // stays an ordinary move. Let go elsewhere it stays where it was put, unless it covers
        // another window; then it takes the nearest place.
        private static void OnWindowMoving(Form window)
        {
            _movingWindow = window;
            _mergingWindow = window;
            Point cursor = Cursor.Position;
            IntPtr self = window.IsHandleCreated ? window.Handle : IntPtr.Zero;

            FluentChromeTabsForm strip = FluentChromeTabsForm.FormWithStripAt(cursor, self);
            if (strip != null && strip != window && IsDropTarget(strip))
            {
                SetDropTarget(DropKind.Strip, strip, DropSide.Left, Rectangle.Empty);
                return;
            }
            Form under = OurWindowAt(cursor, self);
            if (under != null && under != window)
            {
                DropSide? side = EdgeZone(VisibleBounds(under), cursor);
                if (side.HasValue && DockBesidePreview(under, side.Value).HasValue)
                {
                    SetDropTarget(DropKind.BesideWindow, under, side.Value, Rectangle.Empty);
                    return;
                }
            }
            SetDropTarget(DropKind.None, null, DropSide.Left, Rectangle.Empty);
        }

        private static void OnWindowMoveEnded(Form window)
        {
            if (_movingWindow != window) return; // a resize
            _movingWindow = null;
            _mergingWindow = null;
            DropKind kind = _dropKind;
            Form target = _dropWindow;
            DropSide side = _dropSide;
            SnapCell cell = _dropCell;
            Screen screen = _dropScreen;
            EndTabDragVisuals();
            if (window.IsDisposed || window.WindowState != FormWindowState.Normal) return;
            Point cursor = Cursor.Position;

            switch (kind)
            {
                case DropKind.Strip:
                case DropKind.IntoWindow:
                    if (!(target is FluentChromeTabsForm strip) || strip.IsDisposed) return;
                    // Every tab of the moved window goes in there, in order; the window, left empty
                    // by the last one, closes (the host moves them). A tool window becomes a tab.
                    int position = kind == DropKind.Strip ? strip.DropIndexAtScreenPoint(cursor) : -1;
                    if (!(window is FluentChromeTabsForm source))
                    {
                        window.BeginInvoke(new Action(() => _host?.MergeIntoTabs(window, strip)));
                        return;
                    }
                    var tabs = new List<FluentTab>(source.Tabs);
                    window.BeginInvoke(new Action(() => { _ = MergeWindowIntoAsync(source, tabs, strip, position); }));
                    return;

                case DropKind.BesideWindow:
                    if (target == null || target.IsDisposed) return;
                    window.BeginInvoke(new Action(() =>
                    {
                        if (window.IsDisposed || target.IsDisposed) return;
                        DockBeside(window, target, side);
                        TidyScreen(Screen.FromHandle(target.Handle));
                    }));
                    return;

                case DropKind.ScreenCell:
                    if (screen == null) return;
                    window.BeginInvoke(new Action(() =>
                    {
                        if (window.IsDisposed) return;
                        DockToScreen(window, screen, cell);
                        TidyScreen(screen);
                    }));
                    return;

                default:
                    // Where it was put - only a window that now covers another takes a place.
                    Rectangle mine = VisibleBounds(window);
                    bool covers = false;
                    foreach (Form other in AllAppWindows())
                        if (other != window && !other.IsDisposed && other.Visible && other.WindowState == FormWindowState.Normal
                            && VisibleBounds(other).IntersectsWith(mine)) { covers = true; break; }
                    if (covers) TileNear(window, cursor);
                    return;
            }
        }

        private static void SetStripTarget(FluentChromeTabsForm strip)
        {
            if (_stripTarget == strip) return;
            if (_stripTarget != null && !_stripTarget.IsDisposed) _stripTarget.DropHintVisible = false;
            _stripTarget = strip;
            if (strip != null) strip.DropHintVisible = true;
        }

        /// <summary>A window is being resized by an edge: a docked one moves the lines it is on (ScreenDocking.Layout.cs).</summary>
        private static void OnWindowSizing(Form window, int edge, ref RECT proposed)
        {
            OnDockedWindowSizing(window, edge, ref proposed);
        }


        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd, hwndInsertAfter;
            public int x, y, cx, cy;
            public uint flags;
        }

        /// <summary>
        /// A window is about to change its place in the window order (WM_WINDOWPOSCHANGING) -
        /// a click inside it, activation, anything. The upper window of a stacked pair never goes
        /// above the lower one: it lands directly beneath it, and when it is coming to the front
        /// the lower window comes too (without the focus), so the pair stays in front together.
        /// </summary>
        private static void OnWindowPosChanging(Form window, ref WINDOWPOS pos)
        {
            const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
            if ((pos.flags & SWP_NOZORDER) != 0) return;
            if (window == _movingWindow) return; // being dragged: it stays in front
            Form below = WindowBelow(window);
            if (below == null || below.IsDisposed || !below.IsHandleCreated) return;
            if (below.WindowState == FormWindowState.Minimized) return;
            IntPtr lower = below.Handle;
            if (pos.hwndInsertAfter == lower) return;

            // To the front (HWND_TOP / HWND_TOPMOST): the window below first.
            if (pos.hwndInsertAfter == IntPtr.Zero || pos.hwndInsertAfter == new IntPtr(-1))
                SetWindowPos(lower, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            pos.hwndInsertAfter = lower;
        }
    }
}
