using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FluentChromeTabs
{
    // Screen docking - optional. Windows a host attaches (ScreenDocking.Attach) dock on the
    // screen the way Windows Snap places windows, reached by dragging tabs. Nothing here runs
    // for a window that was never attached.
    //
    // Dragging a tab out of the strip - VS Code's way. What follows the cursor is the TAB (a
    // small floating copy with a preview of its page), never a window, so nothing is decided
    // until the button is released, and Windows' own Snap never fights the drag:
    //   over a tab strip          -> the tab goes into that strip at that spot
    //   over the zone at an edge
    //     of one of our windows   -> it opens as a window beside (or above/below) that one,
    //                                half each
    //   over the middle of
    //     another of our windows  -> it joins that window's tabs, as on its strip
    //   at a screen's edge        -> it opens as a window filling that half of the screen
    //   anywhere else             -> it opens as a window there
    //   Escape                    -> nothing happens
    // A window's only tab moves the window itself instead of opening a new one.
    // Windows placed side by side keep their shared edge shared (resizing one resizes the
    // other) until either is moved. Moving a whole window (by its title bar) over another
    // window's strip merges its tabs into that strip.
    // UI thread only.
    public static partial class ScreenDocking
    {
        // How much of a window's width (left/right) or height (top/bottom), from each edge,
        // places the tab beside it. The middle takes the tab in, like the strip.
        private const double DropZoneShare = 0.3;
        // The same for a screen, off the app: a smaller share, the screen being much larger.
        private const double ScreenZoneShare = 0.2;

        // IntoWindow: the middle of a window - the tab joins its tabs, at the end, as on its strip.
        // ScreenCell: a screen's half or quarter (ScreenDocking.Layout.cs).
        private enum DropKind { None, Strip, IntoWindow, BesideWindow, ScreenCell }

        // The tab drag in progress (one at a time: there is one mouse).
        private static TabDragGhost _ghost;
        private static DropKind _dropKind;
        private static FluentChromeTabsForm _dropWindow;       // Strip: that window's strip; BesideWindow: that window
        private static DropSide _dropSide;
        private static Rectangle _dropArea;        // ScreenCell: the cell
        private static SnapCell _dropCell;         // ScreenCell: which one
        private static SnapCell _dropCellAtDrop;
        private static Screen _dropScreenAtDrop;
        private static Screen _dropScreen;         // ScreenCell: on which screen
        private static DropZonePreview _dropPreview;

        // A window whose tab strip a window being MOVED (title bar) is over: on release the
        // moved window's tabs go in there.
        private static FluentChromeTabsForm _stripTarget;
        private static FluentChromeTabsForm _mergingWindow;

        /// <summary>A side of a window (where a tab dropped at its edge goes).</summary>
        public enum DropSide { Left, Right, Top, Bottom }

        // ── A tab dragged out of a strip (FluentChromeTabsForm.TabDragOutside) ──────────

        private static void OnTabDragOutside(FluentChromeTabsForm source, FluentTabDragOutsideEventArgs e)
        {
            switch (e.Phase)
            {
                case FluentTabDragPhase.Started:
                    EndTabDragVisuals();
                    // A window's only tab moves its own window: nothing to start.
                    _ghost = new TabDragGhost(e.Tab.Title, e.Tab.Icon, source.DeviceDpi);
                    _ghost.MoveTo(e.ScreenPoint);
                    _ghost.Show();
                    ShowPicture(_ghost, CapturePicture(source));
                    UpdateTabDropTarget(source, e.ScreenPoint);
                    break;
                case FluentTabDragPhase.Moved:
                    _ghost?.MoveTo(e.ScreenPoint);
                    UpdateTabDropTarget(source, e.ScreenPoint);
                    break;
                case FluentTabDragPhase.Returned:
                case FluentTabDragPhase.Cancelled:
                    EndTabDragVisuals();
                    break;
                case FluentTabDragPhase.Dropped:
                    DropKind kind = _dropKind;
                    FluentChromeTabsForm target = _dropWindow;
                    DropSide side = _dropSide;
                    Rectangle area = _dropArea;
                    _dropCellAtDrop = _dropCell;
                    _dropScreenAtDrop = _dropScreen;
                    EndTabDragVisuals();
                    DropTab(source, e.Tab, e.ScreenPoint, kind, target, side, area);
                    break;
            }
        }

        private static async void ShowPicture(TabDragGhost ghost, Task<Image> picture)
        {
            Image image = null;
            try { image = await picture; } catch (Exception) { }
            if (image != null && !ghost.IsDisposed) ghost.SetPicture(image);
        }

        private static void UpdateTabDropTarget(FluentChromeTabsForm source, Point cursor)
        {
            IntPtr ghost = _ghost != null && _ghost.IsHandleCreated ? _ghost.Handle : IntPtr.Zero;

            // The very corner of a screen is that quarter, and a thin band along its edge that half,
            // even over a docked window's strip or edge there.
            {
                Screen s = Screen.FromPoint(cursor);
                Rectangle wa = s.WorkingArea;
                double cx = (cursor.X - wa.Left) / (double)Math.Max(1, wa.Width), cy = (cursor.Y - wa.Top) / (double)Math.Max(1, wa.Height);
                bool nearX = cx <= ScreenCornerTightShare || cx >= 1 - ScreenCornerTightShare;
                bool nearY = cy <= ScreenCornerTightShare || cy >= 1 - ScreenCornerTightShare;
                SnapCell? tight = nearX && nearY ? ScreenZone(wa, cursor) : null;
                // A thin band along the screen's edge is that half, the same way.
                if (!tight.HasValue)
                {
                    if (cx <= ScreenEdgeTightShare) tight = SnapCell.Left;
                    else if (cx >= 1 - ScreenEdgeTightShare) tight = SnapCell.Right;
                    else if (cy <= ScreenEdgeTightShare) tight = SnapCell.Top;
                    else if (cy >= 1 - ScreenEdgeTightShare) tight = SnapCell.Bottom;
                }
                if (tight.HasValue)
                {
                    _dropCell = tight.Value;
                    _dropScreen = s;
                    SetDropTarget(DropKind.ScreenCell, null, DropSide.Left, ScreenCellRect(s, tight.Value));
                    return;
                }
            }

            // Over a strip: the tab goes in there. Its own strip is where the drag came from.
            FluentChromeTabsForm strip = FluentChromeTabsForm.FormWithStripAt(cursor, ghost);
            if (strip != null && strip != source && IsDropTarget(strip))
            {
                SetDropTarget(DropKind.Strip, strip, DropSide.Left, Rectangle.Empty);
                return;
            }
            if (strip == source)
            {
                SetDropTarget(DropKind.None, null, DropSide.Left, Rectangle.Empty);
                return;
            }

            // A screen's corner is a quarter of that screen even over one of our windows (a docked
            // window reaches into the corner; its own edge zones would win there otherwise).
            Screen cornerScreen = Screen.FromPoint(cursor);
            SnapCell? corner = ScreenZone(cornerScreen.WorkingArea, cursor);
            if (corner.HasValue && !IsHalf(corner.Value))
            {
                _dropCell = corner.Value;
                _dropScreen = cornerScreen;
                SetDropTarget(DropKind.ScreenCell, null, DropSide.Left, ScreenCellRect(cornerScreen, corner.Value));
                return;
            }

            // Over one of our windows: the zone at its nearest edge places the tab beside it
            // (or above or below it); the middle of another window takes the tab in, like its strip.
            FluentChromeTabsForm under = OurWindowAt(cursor, ghost);
            if (under != null)
            {
                Rectangle visible = VisibleBounds(under);
                DropSide? side = EdgeZone(visible, cursor);
                // A window's only tab dropped on its own window has nothing to be beside.
                bool alone = under == source && TabCountOf(source) <= 1;
                // Only where two windows fit (SplitArea): otherwise the middle's rule applies.
                if (side.HasValue && !alone && DockBesidePreview(under, side.Value).HasValue)
                    SetDropTarget(DropKind.BesideWindow, under, side.Value, Rectangle.Empty);
                else if (under != source && IsDropTarget(under))
                    SetDropTarget(DropKind.IntoWindow, under, DropSide.Left, Rectangle.Empty);
                else
                    SetDropTarget(DropKind.None, null, DropSide.Left, Rectangle.Empty);
                return;
            }

            // Off the app: a screen's corner gives that quarter, the zone at its nearest edge that
            // half (Windows Snap's places); the middle opens a window where the tab is let go.
            Screen screen = Screen.FromPoint(cursor);
            SnapCell? cell = ScreenZone(screen.WorkingArea, cursor);
            if (cell.HasValue)
            {
                _dropCell = cell.Value;
                _dropScreen = screen;
                SetDropTarget(DropKind.ScreenCell, null, DropSide.Left, ScreenCellRect(screen, cell.Value));
                return;
            }

            SetDropTarget(DropKind.None, null, DropSide.Left, Rectangle.Empty);
        }

        // How close to a screen's corner (a share of its width and of its height) gives a quarter.
        private const double ScreenCornerShare = 0.12;
        // The part of that which wins even over a tab strip reaching into the corner.
        private const double ScreenCornerTightShare = 0.04;
        private const double ScreenEdgeTightShare = 0.012;

        private static SnapCell? ScreenZone(Rectangle work, Point cursor)
        {
            double x = (cursor.X - work.Left) / (double)Math.Max(1, work.Width);
            double y = (cursor.Y - work.Top) / (double)Math.Max(1, work.Height);
            bool left = x <= ScreenCornerShare, right = x >= 1 - ScreenCornerShare;
            bool top = y <= ScreenCornerShare, bottom = y >= 1 - ScreenCornerShare;
            if (top && left) return SnapCell.TopLeft;
            if (top && right) return SnapCell.TopRight;
            if (bottom && left) return SnapCell.BottomLeft;
            if (bottom && right) return SnapCell.BottomRight;
            DropSide? side = EdgeZone(work, cursor, ScreenZoneShare);
            if (!side.HasValue) return null;
            switch (side.Value)
            {
                case DropSide.Left: return SnapCell.Left;
                case DropSide.Right: return SnapCell.Right;
                case DropSide.Top: return SnapCell.Top;
                default: return SnapCell.Bottom;
            }
        }

        // The edge zone of a window the cursor is in (the nearest edge, when within its
        // share of the window), or null in the middle.
        private static DropSide? EdgeZone(Rectangle visible, Point cursor, double share = DropZoneShare)
        {
            double x = (cursor.X - visible.Left) / (double)Math.Max(1, visible.Width);
            double y = (cursor.Y - visible.Top) / (double)Math.Max(1, visible.Height);
            double toLeft = x, toRight = 1 - x, toTop = y, toBottom = 1 - y;
            double nearest = Math.Min(Math.Min(toLeft, toRight), Math.Min(toTop, toBottom));
            if (nearest > share) return null;
            if (nearest == toLeft) return DropSide.Left;
            if (nearest == toRight) return DropSide.Right;
            return nearest == toTop ? DropSide.Top : DropSide.Bottom;
        }

        private static void SetDropTarget(DropKind kind, FluentChromeTabsForm window, DropSide side, Rectangle area)
        {
            _dropKind = kind;
            _dropWindow = window;
            _dropSide = side;
            _dropArea = area;

            // Strip and window drops mark the strip too: that is where the tab will appear.
            SetStripTarget(kind == DropKind.Strip || kind == DropKind.IntoWindow ? window : null);
            if (kind == DropKind.IntoWindow) ShowDropPreview(VisibleBounds(window));
            else if (kind == DropKind.BesideWindow) ShowDropPreview(DockBesidePreview(window, side).Value);
            else if (kind == DropKind.ScreenCell) ShowDropPreview(area);
            else HideDropPreview();
        }

        private static void EndTabDragVisuals()
        {
            if (_ghost != null && !_ghost.IsDisposed) _ghost.Dispose();
            _ghost = null;
            _dropKind = DropKind.None;
            _dropWindow = null;
            SetStripTarget(null);
            HideDropPreview();
        }

        private static void DropTab(FluentChromeTabsForm source, FluentTab tab, Point cursor,
            DropKind kind, FluentChromeTabsForm target, DropSide side, Rectangle area)
        {
            if (source.IsDisposed) return;
            bool onlyTab = TabCountOf(source) <= 1;

            switch (kind)
            {
                case DropKind.Strip:
                case DropKind.IntoWindow:
                    if (target == null || target.IsDisposed) return;
                    int position = kind == DropKind.Strip ? target.DropIndexAtScreenPoint(cursor) : -1;
                    MoveTab(source, tab, target, position);
                    target.Activate();
                    return;

                case DropKind.BesideWindow:
                    if (target == null || target.IsDisposed || !DockBesidePreview(target, side).HasValue) return;
                    if (onlyTab) { DockBeside(source, target, side); TidyScreen(Screen.FromHandle(target.Handle)); }
                    else OpenWindowForTab(source, tab, window => { DockBeside(window, target, side); TidyScreen(Screen.FromHandle(target.Handle)); });
                    return;

                case DropKind.ScreenCell:
                    Screen screen = _dropScreenAtDrop;
                    SnapCell cell = _dropCellAtDrop;
                    if (onlyTab) { DockToScreen(source, screen, cell); TidyScreen(screen); }
                    else OpenWindowForTab(source, tab, window => { DockToScreen(window, screen, cell); TidyScreen(screen); });
                    return;

                default:
                    // A window where the tab was let go, the size of the one it came from; the
                    // cursor lands on its strip, near where tabs start (the right side: RTL).
                    Size size = source.WindowState == FormWindowState.Normal ? source.Size : source.RestoreBounds.Size;
                    int inset = (int)(140 * source.DeviceDpi / 96f);
                    var bounds = KeepOnScreen(
                        new Rectangle(cursor.X - size.Width + inset, cursor.Y - (int)(16 * source.DeviceDpi / 96f), size.Width, size.Height),
                        cursor);
                    Action<FluentChromeTabsForm> place = window =>
                    {
                        if (window.WindowState != FormWindowState.Normal) window.WindowState = FormWindowState.Normal;
                        window.Bounds = bounds;
                        window.Activate();
                        // Never over another of our windows: it takes the place nearest the cursor.
                        TileNear(window, cursor);
                    };
                    if (onlyTab) place(source);
                    else OpenWindowForTab(source, tab, place);
                    return;
            }
        }

        // ── The windows taking part, and the host ──────────────────────────────────

        private static IScreenDockingHost _host;
        private static readonly List<FluentChromeTabsForm> Windows = new List<FluentChromeTabsForm>();

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
            window.Activated += (s, e) => KeepUpperBelowLower(window);
            window.FormClosed += (s, e) =>
            {
                Forget(window);
                Windows.Remove(window);
            };
        }

        /// <summary>Takes a window out of its place (it is hidden, say): its neighbours take its space.</summary>
        public static void Forget(FluentChromeTabsForm window)
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

        private static IEnumerable<FluentChromeTabsForm> AllAppWindows() => Windows.ToArray();

        private static bool IsDropTarget(FluentChromeTabsForm window)
            => window != null && !window.IsDisposed && window.Visible && Windows.Contains(window)
               && (_host == null || _host.AcceptsDrops(window));

        private static int TabCountOf(FluentChromeTabsForm window) => _host?.TabCount(window) ?? window.Tabs.Count;

        private static Task<Image> CapturePicture(FluentChromeTabsForm window)
            => _host?.CaptureTabPicture(window) ?? Task.FromResult<Image>(null);

        private static void MoveTab(FluentChromeTabsForm source, FluentTab tab, FluentChromeTabsForm target, int position)
            => _ = _host?.MoveTabAsync(source, tab, target, position);

        private static void OpenWindowForTab(FluentChromeTabsForm source, FluentTab tab, Action<FluentChromeTabsForm> place)
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

        // A dropped window lands whole on the screen it was dropped on: no larger than its
        // working area, and moved in from any edge it would cross.
        private static Rectangle KeepOnScreen(Rectangle bounds, Point cursor)
        {
            Rectangle work = Screen.FromPoint(cursor).WorkingArea;
            int width = Math.Min(bounds.Width, work.Width), height = Math.Min(bounds.Height, work.Height);
            int left = Math.Max(work.Left, Math.Min(bounds.Left, work.Right - width));
            int top = Math.Max(work.Top, Math.Min(bounds.Top, work.Bottom - height));
            return new Rectangle(left, top, width, height);
        }

        private static Rectangle HalfOf(Rectangle area, DropSide side)
        {
            switch (side)
            {
                case DropSide.Left: return new Rectangle(area.Left, area.Top, area.Width / 2, area.Height);
                case DropSide.Right: return new Rectangle(area.Left + area.Width / 2, area.Top, area.Width - area.Width / 2, area.Height);
                case DropSide.Top: return new Rectangle(area.Left, area.Top, area.Width, area.Height / 2);
                default: return new Rectangle(area.Left, area.Top + area.Height / 2, area.Width, area.Height - area.Height / 2);
            }
        }

        private static DropSide Opposite(DropSide side)
        {
            switch (side)
            {
                case DropSide.Left: return DropSide.Right;
                case DropSide.Right: return DropSide.Left;
                case DropSide.Top: return DropSide.Bottom;
                default: return DropSide.Top;
            }
        }

        // ── Window messages of attached windows (FluentChromeTabsForm.WndProc) ─────────

        // Before the window handles the message: edge resizes move the lines, a title-bar move
        // takes a docked window out, and the window order keeps shadows hidden.
        internal static void BeforeWindowMessage(FluentChromeTabsForm window, ref Message m)
        {
            const int WM_SIZING = 0x0214, WM_MOVING = 0x0216, WM_WINDOWPOSCHANGING = 0x0046;
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
        internal static void AfterWindowMessage(FluentChromeTabsForm window, ref Message m)
        {
            const int WM_MOVING = 0x0216, WM_EXITSIZEMOVE = 0x0232;
            if (m.Msg == WM_MOVING) OnWindowMoving(window);
            else if (m.Msg == WM_EXITSIZEMOVE) OnWindowMoveEnded(window);
        }

        // ── A whole window moved by its title bar ───────────────────

        // Windows moved by their title bar since the move began (a resize ends the same way).
        private static readonly HashSet<FluentChromeTabsForm> _movedWindows = new HashSet<FluentChromeTabsForm>();

        private static void OnWindowMoving(FluentChromeTabsForm window)
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

        private static void OnWindowMoveEnded(FluentChromeTabsForm window)
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
            var tabs = new List<FluentTab>(window.Tabs);
            int position = strip.DropIndexAtScreenPoint(Cursor.Position);
            window.BeginInvoke(new Action(() => { _ = MergeWindowIntoAsync(window, tabs, strip, position); }));
        }

        private static void SetStripTarget(FluentChromeTabsForm strip)
        {
            if (_stripTarget == strip) return;
            if (_stripTarget != null && !_stripTarget.IsDisposed) _stripTarget.DropHintVisible = false;
            _stripTarget = strip;
            if (strip != null) strip.DropHintVisible = true;
        }

        /// <summary>A window is being resized by an edge: a docked one moves the lines it is on (ScreenDocking.Layout.cs).</summary>
        private static void OnWindowSizing(FluentChromeTabsForm window, int edge, ref RECT proposed)
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
        private static void OnWindowPosChanging(FluentChromeTabsForm window, ref WINDOWPOS pos)
        {
            const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
            if ((pos.flags & SWP_NOZORDER) != 0) return;
            FluentChromeTabsForm below = WindowBelow(window);
            if (below == null || below.IsDisposed || !below.IsHandleCreated) return;
            if (below.WindowState == FormWindowState.Minimized) return;
            IntPtr lower = below.Handle;
            if (pos.hwndInsertAfter == lower) return;

            // To the front (HWND_TOP / HWND_TOPMOST): the window below first.
            if (pos.hwndInsertAfter == IntPtr.Zero || pos.hwndInsertAfter == new IntPtr(-1))
                SetWindowPos(lower, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            pos.hwndInsertAfter = lower;
        }

        // The topmost of our windows at the point, looking past `ignore` (the drag ghost);
        // null when another program's window covers the point.
        private static FluentChromeTabsForm OurWindowAt(Point cursor, IntPtr ignore)
        {
            IntPtr preview = _dropPreview != null && _dropPreview.IsHandleCreated ? _dropPreview.Handle : IntPtr.Zero;
            for (IntPtr h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDNEXT))
            {
                if (h == ignore || h == preview || !IsWindowVisible(h)) continue;
                RECT r;
                if (!GetWindowRect(h, out r)) continue;
                if (cursor.X < r.Left || cursor.X >= r.Right || cursor.Y < r.Top || cursor.Y >= r.Bottom) continue;
                foreach (FluentChromeTabsForm open in AllAppWindows())
                    if (!open.IsDisposed && open.IsHandleCreated && open.Handle == h
                        && open.WindowState != FormWindowState.Minimized && IsDropTarget(open))
                        return open;
                return null;
            }
            return null;
        }

        /// <summary>
        /// What follows the cursor while a tab is dragged: the tab as a rounded card, with a
        /// preview of its page under it - Windows 11's rounded corners and shadow, and the
        /// app's light or dark colors.
        /// </summary>
        private sealed class TabDragGhost : Form
        {
            private readonly string _title;
            private readonly Image _icon;
            private readonly float _scale;
            private readonly bool _dark;
            private readonly Color _accent;
            private Image _picture;

            public TabDragGhost(string title, Image icon, int dpi)
            {
                _title = title ?? "";
                _icon = icon;
                _scale = dpi / 96f;
                _dark = DarkMode;
                _accent = AccentColor;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                RightToLeft = RightToLeft.Yes;
                BackColor = _dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(249, 249, 249);
                Font = new Font("Segoe UI", 9f);
                Size = new Size(Px(260), Px(44));
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            }

            private int Px(int logical) => (int)(logical * _scale);

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    const int CS_DROPSHADOW = 0x20000;
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                    cp.ClassStyle |= CS_DROPSHADOW;
                    return cp;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                // Windows 11: rounded corners with the system's own shadow. Older Windows
                // ignores the attribute and keeps square corners with the class shadow.
                int round = DWMWCP_ROUND;
                DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            }

            /// <summary>The cursor holds the tab near its start (the right side: RTL).</summary>
            public void MoveTo(Point cursor)
            {
                Location = new Point(cursor.X - Width + Px(28), cursor.Y - Px(20));
            }

            /// <summary>The page's picture, shown small under the tab.</summary>
            public void SetPicture(Image picture)
            {
                _picture = picture;
                int width = Px(260);
                int previewWidth = width - Px(12);
                int previewHeight = Math.Min(Px(160), (int)((long)picture.Height * previewWidth / Math.Max(1, picture.Width)));
                Point anchor = new Point(Right - Px(28), Top + Px(20));
                Size = new Size(width, Px(44) + previewHeight + Px(6));
                MoveTo(anchor);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(BackColor);

                Color card = _dark ? Color.FromArgb(45, 45, 45) : Color.White;
                Color text = _dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(26, 26, 26);
                Color hairline = _dark ? Color.FromArgb(64, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0);

                // The tab: a rounded card, an accent mark at its start, icon and title.
                var tab = new Rectangle(Px(6), Px(6), Width - Px(12), Px(32));
                using (GraphicsPath path = Rounded(tab, Px(6)))
                {
                    using (var brush = new SolidBrush(card)) g.FillPath(brush, path);
                    using (var pen = new Pen(hairline)) g.DrawPath(pen, path);
                }
                using (var mark = new SolidBrush(_accent))
                    g.FillRectangle(mark, tab.Right - Px(3), tab.Top + Px(9), Px(2), tab.Height - Px(18));

                int x = tab.Right - Px(12);
                if (_icon != null)
                {
                    int icon = Px(16);
                    x -= icon;
                    g.DrawImage(_icon, new Rectangle(x, tab.Top + (tab.Height - icon) / 2, icon, icon));
                    x -= Px(8);
                }
                var textRect = new Rectangle(tab.Left + Px(10), tab.Top, Math.Max(0, x - tab.Left - Px(10)), tab.Height);
                TextRenderer.DrawText(g, _title, Font, textRect, text,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.RightToLeft | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                // The page, small, in rounded corners under the tab.
                if (_picture != null)
                {
                    var preview = new Rectangle(Px(6), tab.Bottom + Px(4), Width - Px(12), Height - tab.Bottom - Px(10));
                    if (preview.Height > 0)
                    {
                        using (GraphicsPath clip = Rounded(preview, Px(6)))
                        {
                            GraphicsState state = g.Save();
                            g.SetClip(clip);
                            int sourceHeight = Math.Min(_picture.Height, (int)((long)_picture.Width * preview.Height / Math.Max(1, preview.Width)));
                            g.DrawImage(_picture, preview, 0, 0, _picture.Width, sourceHeight, GraphicsUnit.Pixel);
                            g.Restore(state);
                            using (var pen = new Pen(hairline)) g.DrawPath(pen, clip);
                        }
                    }
                }
            }

            private static GraphicsPath Rounded(Rectangle r, int radius)
            {
                float d = radius * 2f;
                var path = new GraphicsPath();
                path.AddArc(r.Left, r.Top, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }

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

        // ── The preview ───────────────────────────────────────────────────────────

        private static void ShowDropPreview(Rectangle bounds)
        {
            if (_dropPreview == null || _dropPreview.IsDisposed) _dropPreview = new DropZonePreview();
            if (_dropPreview.Bounds != bounds) _dropPreview.Bounds = bounds;
            if (!_dropPreview.Visible) _dropPreview.Show();
        }

        private static void HideDropPreview()
        {
            if (_dropPreview != null && !_dropPreview.IsDisposed && _dropPreview.Visible) _dropPreview.Hide();
        }

        /// <summary>The see-through sheet that marks where a dropped window will land. Never takes focus or clicks.</summary>
        private sealed class DropZonePreview : Form
        {
            public DropZonePreview()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                BackColor = AccentColor;
                Opacity = 0.28;
            }

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                    return cp;
                }
            }
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
