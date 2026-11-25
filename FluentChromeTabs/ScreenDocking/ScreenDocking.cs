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
    // Screen docking - optional. A host attaches its windows (Attach for tab windows,
    // AttachWindow for tool windows) and they dock on the screen the way Windows Snap places
    // windows, reached by dragging tabs; nothing here runs for a window never attached.
    //
    //   ScreenDocking.cs            attaching windows, the host, the window messages
    //   ScreenDocking.TabDrag.cs    a tab dragged out of a strip: where it goes, the drop
    //   ScreenDocking.WindowMoves.cs a whole window moved by its title bar
    //   ScreenDocking.Layout.cs     the Snap layout: halves, quarters, lines, shadows
    //   ScreenDocking.Tiling.cs     our windows never cover each other
    //   ScreenDocking.Api.cs        docking without a drag (hosts, tests)
    //   ScreenDocking.TabDragGhost.cs, .DropPreview.cs, .Native.cs
    // UI thread only.
    public static partial class ScreenDocking
    {
        // ── The windows taking part, and the host ──────────────────────────────────

        private static IScreenDockingHost _host;
        private static readonly List<Form> Windows = new List<Form>();

        /// <summary>
        /// Lets a window take part in screen docking: its tabs can be dragged out to dock on the
        /// screen, and it is docked, tiled and resized with the others. <paramref name="host"/>
        /// moves tabs and opens windows for them (one host per process). Turns on the strip's
        /// drag-outside mode (HostMovesTabsBetweenWindows) for the window.
        /// </summary>
        public static void Attach(FluentChromeTabsForm window, IScreenDockingHost host)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            _host = host ?? throw new ArgumentNullException(nameof(host));
            if (Windows.Contains(window)) return;
            Windows.Add(window);
            window.AllowTabDetach = true;
            window.HostMovesTabsBetweenWindows = true;
            window.ScreenDockingAttached = true;
            window.TabDragOutside += (s, e) => OnTabDragOutside(window, e);
            Join(window);
        }

        /// <summary>
        /// Lets a window without a tab strip (a tool window) take part in screen docking: it docks,
        /// tiles and never covers the others like any attached window; let go on a tab strip it
        /// becomes a tab there (IScreenDockingHost.MergeIntoTabs). Loose, it keeps its own size.
        /// </summary>
        public static void AttachWindow(Form window, IScreenDockingHost host)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            _host = host ?? throw new ArgumentNullException(nameof(host));
            if (window is FluentChromeTabsForm tabs) { Attach(tabs, host); return; }
            if (Windows.Contains(window)) return;
            Windows.Add(window);
            // Hooked now and again whenever Windows recreates the handle (the hook ends with it).
            if (window.IsHandleCreated) new MessageHook(window);
            window.HandleCreated += (s, e) => new MessageHook(window);
            window.Resize += (s, e) => OnDockedWindowResized(window);
            Join(window);
        }

        private static void Join(Form window)
        {
            window.Activated += (s, e) => KeepUpperBelowLower(window);
            window.FormClosed += (s, e) =>
            {
                Forget(window);
                Windows.Remove(window);
                LastRelayoutCell.Remove(window);
            };
        }

        // A tool window's move, size and order messages (a tab window's own WndProc forwards them).
        private sealed class MessageHook : NativeWindow
        {
            private readonly Form _window;

            public MessageHook(Form window)
            {
                _window = window;
                AssignHandle(window.Handle);
                window.HandleDestroyed += (s, e) => ReleaseHandle();
            }

            protected override void WndProc(ref Message m)
            {
                BeforeWindowMessage(_window, ref m);
                base.WndProc(ref m);
                AfterWindowMessage(_window, ref m);
            }
        }

        /// <summary>Takes a window out of its place (it is hidden, say): its neighbours take its space.</summary>
        public static void Forget(Form window)
        {
            Undock(window, restoreSize: false);
            MinimizedPlaces.Remove(window);
            if (_dropWindow == window) EndTabDragVisuals();
            if (_mergingWindow == window) { _mergingWindow = null; SetStripTarget(null); }
            if (_stripTarget == window) SetStripTarget(null);
        }

        /// <summary>The floating tab's look: dark or light, and its accent mark. Set by the host.</summary>
        public static bool DarkMode { get; set; }

        public static Color AccentColor { get; set; } = Color.FromArgb(124, 92, 252);

        private static IEnumerable<Form> AllAppWindows() => Windows.ToArray();

        private static bool IsDropTarget(Form window)
            => window != null && !window.IsDisposed && window.Visible && Windows.Contains(window)
               && (_host == null || _host.AcceptsDrops(window));

        // A tool window counts as one tab: dragged out, it moves itself.
        private static int TabCountOf(Form window)
            => window is FluentChromeTabsForm tabs ? (_host?.TabCount(tabs) ?? tabs.Tabs.Count) : 1;

        private static Task<Image> CapturePicture(FluentChromeTabsForm window)
            => _host?.CaptureTabPicture(window) ?? Task.FromResult<Image>(null);

        private static void MoveTab(FluentChromeTabsForm source, FluentTab tab, Form target, int position)
        {
            if (target is FluentChromeTabsForm tabs) _ = _host?.MoveTabAsync(source, tab, tabs, position);
        }

        private static void OpenWindowForTab(FluentChromeTabsForm source, FluentTab tab, Action<Form> place)
            => _host?.OpenWindowForTab(source, tab, place);

        // Every tab of a window into another, in order (the source closes when its last one leaves).
        private static async Task MergeWindowIntoAsync(FluentChromeTabsForm source, List<FluentTab> tabs, FluentChromeTabsForm target, int position)
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                if (_host == null || source.IsDisposed || target.IsDisposed) return;
                // A failed move leaves that tab (and the rest) where they were.
                if (!await _host.MoveTabAsync(source, tabs[i], target, position < 0 ? -1 : position + i)) return;
            }
        }

        // ── Window messages of attached windows (FluentChromeTabsForm.WndProc, or a hook for a tool window) ─────────

        // Before the window handles the message: edge resizes move the lines, a title-bar move
        // takes a docked window out, and the window order keeps shadows hidden.
        internal static void BeforeWindowMessage(Form window, ref Message m)
        {
            const int WM_SIZING = 0x0214, WM_MOVING = 0x0216, WM_WINDOWPOSCHANGING = 0x0046, WM_SYSCOMMAND = 0x0112, SC_MINIMIZE = 0xF020;
            // About to be minimized: its picture for the group's taskbar preview (ScreenDocking.GroupTaskbar.cs).
            if (m.Msg == WM_SYSCOMMAND && (m.WParam.ToInt32() & 0xFFF0) == SC_MINIMIZE) RememberPicture(window);
            if (m.LParam == IntPtr.Zero) return;
            if (m.Msg == WM_WINDOWPOSCHANGING)
            {
                var pos = (WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(WINDOWPOS));
                IntPtr before = pos.hwndInsertAfter;
                OnWindowPosChanging(window, ref pos);
                if (pos.hwndInsertAfter != before) Marshal.StructureToPtr(pos, m.LParam, false);
            }
            else if (m.Msg == WM_SIZING)
            {
                var proposed = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                OnWindowSizing(window, m.WParam.ToInt32(), ref proposed);
                Marshal.StructureToPtr(proposed, m.LParam, false);
            }
            else if (m.Msg == WM_MOVING)
            {
                var moving = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                OnWindowMovingRect(window, ref moving);
                Marshal.StructureToPtr(moving, m.LParam, false);
            }
        }

        // After it: a title-bar move over a strip, and the end of a move.
        internal static void AfterWindowMessage(Form window, ref Message m)
        {
            const int WM_MOVING = 0x0216, WM_EXITSIZEMOVE = 0x0232;
            if (m.Msg == WM_MOVING) OnWindowMoving(window);
            else if (m.Msg == WM_EXITSIZEMOVE) OnWindowMoveEnded(window);
        }
    }
}
