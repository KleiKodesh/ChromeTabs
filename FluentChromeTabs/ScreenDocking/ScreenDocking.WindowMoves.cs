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

        // Windows moved by their title bar since the move began (a resize ends the same way).
        private static readonly HashSet<Form> _movedWindows = new HashSet<Form>();

        private static void OnWindowMoving(Form window)
        {
            _movedWindows.Add(window);
            // Over another window's tab strip: this window's tabs go in there on release.
            FluentChromeTabsForm strip = FluentChromeTabsForm.FormWithStripAt(Cursor.Position, window.Handle);
            if (strip != null && strip != window && IsDropTarget(strip))
            {
                _mergingWindow = window;
                SetStripTarget(strip);
                return;
            }
            if (_mergingWindow == window)
            {
                _mergingWindow = null;
                SetStripTarget(null);
            }
        }

        private static void OnWindowMoveEnded(Form window)
        {
            // A window let go anywhere but on a strip takes the place nearest the cursor.
            if (_mergingWindow != window)
            {
                if (!window.IsDisposed && window.WindowState == FormWindowState.Normal && _movedWindows.Remove(window))
                    TileNear(window, Cursor.Position);
                return;
            }
            _movedWindows.Remove(window);
            FluentChromeTabsForm strip = _stripTarget;
            _mergingWindow = null;
            SetStripTarget(null);
            if (strip == null || strip.IsDisposed || window.IsDisposed) return;

            // Every tab of the moved window goes into the strip at the drop point, in order;
            // the window, left empty by the last one, closes (the host moves them).
            int position = strip.DropIndexAtScreenPoint(Cursor.Position);
            if (!(window is FluentChromeTabsForm source))
            {
                window.BeginInvoke(new Action(() => _host?.MergeIntoTabs(window, strip)));
                return;
            }
            var tabs = new List<FluentTab>(source.Tabs);
            window.BeginInvoke(new Action(() => { _ = MergeWindowIntoAsync(source, tabs, strip, position); }));
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
