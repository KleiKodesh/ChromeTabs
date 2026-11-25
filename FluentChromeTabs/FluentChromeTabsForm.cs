using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FluentChromeTabs
{
    /// <summary>
    /// A WinForms window that hosts Fluent (Windows 11) style tabs directly in its title bar.
    /// <para>
    /// The standard window frame is kept, so DWM shadows, rounded corners, snap layouts, and
    /// minimize/maximize animations all behave natively; the caption area is reclaimed as client
    /// space and the tab strip is painted there. There is no separate overlay window.
    /// </para>
    /// <para>Minimal usage:</para>
    /// <code>
    /// var window = new FluentChromeTabsForm { Text = "My App" };
    /// window.AddTab("Home", new HomeControl());
    /// Application.Run(window);
    /// </code>
    /// </summary>
    public partial class FluentChromeTabsForm : Form
    {
        private static readonly List<FluentChromeTabsForm> OpenTabForms = new List<FluentChromeTabsForm>();

        private readonly List<FluentTab> _tabs = new List<FluentTab>();
        private ThemedToolTip _toolTip;

        private int _selectedIndex = -1;
        private FluentChromeTabsTheme _theme = FluentChromeTabsTheme.Auto;
        private Palette _palette;
        private bool _isDark;
        private bool _windowActive = true;
        private int _newTabCounter;

        public FluentChromeTabsForm()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw,
                true);

            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(1000, 640);
            Text = "FluentChromeTabs";

            ApplyTheme();
            UpdateMetrics();
        }

        #region Public API

        /// <summary>The tabs currently hosted by this window, in display order.</summary>
        public IReadOnlyList<FluentTab> Tabs
        {
            get { return _tabs; }
        }

        /// <summary>Index of the selected tab, or -1 when the window has no tabs.</summary>
        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set
            {
                if (value >= 0 && value < _tabs.Count)
                {
                    SelectTabCore(value);
                }
            }
        }

        /// <summary>The selected tab, or null when the window has no tabs.</summary>
        public FluentTab SelectedTab
        {
            get { return _selectedIndex >= 0 && _selectedIndex < _tabs.Count ? _tabs[_selectedIndex] : null; }
            set { SelectedIndex = value == null ? -1 : _tabs.IndexOf(value); }
        }

        /// <summary>Whether the window closes itself when its last tab is closed. Defaults to true.</summary>
        public bool ExitOnLastTabClose { get; set; } = true;

        /// <summary>Whether the "+" new-tab button is shown. Defaults to true.</summary>
        public bool ShowNewTabButton
        {
            get { return _showNewTabButton; }
            set
            {
                _showNewTabButton = value;
                InvalidateStrip();
            }
        }

        private bool _showNewTabButton = true;

        /// <summary>Whether tabs may be torn off into their own window or dropped onto other windows. Defaults to true.</summary>
        public bool AllowTabDetach { get; set; } = true;

        /// <summary>
        /// Splits the strip down the middle into two parallel tab regions. Tabs are laid out in
        /// the region given by <see cref="FluentTab.Group" /> (0 = inline start, 1 = inline end),
        /// and each region gets its own tab-list toggle and "+" button (gestures report the group).
        /// Each region marks its own active tab via <see cref="FluentTab.Highlighted" />.
        /// Defaults to false.
        /// </summary>
        public bool SplitStrip
        {
            get { return _splitStrip; }
            set
            {
                _splitStrip = value;
                InvalidateStrip();
            }
        }

        private bool _splitStrip;

        /// <summary>
        /// Where the split divider sits, as region 0's share of the client width (0.15–0.85).
        /// Setting it programmatically does not raise <see cref="SplitRatioChanged" /> — that
        /// event reports only user drags of the divider. Defaults to 0.5.
        /// </summary>
        public double SplitRatio
        {
            get { return _splitRatio; }
            set
            {
                double clamped = Math.Max(0.15, Math.Min(0.85, value));

                if (Math.Abs(clamped - _splitRatio) > 0.0001)
                {
                    _splitRatio = clamped;
                    InvalidateStrip();
                }
            }
        }

        private double _splitRatio = 0.5;

        /// <summary>Raised while the user drags the split divider (read <see cref="SplitRatio" />).</summary>
        public event EventHandler SplitRatioChanged;

        private int _dividerOverrideLeft = -1;
        private int _dividerOverrideWidth;

        /// <summary>
        /// Pins the split divider to exact client pixels, letting a host align it perfectly
        /// with a content divider below the strip (no fraction rounding). Pass a negative
        /// left to return to <see cref="SplitRatio" />-based placement. A user drag of the
        /// divider clears the pin until it is set again.
        /// </summary>
        public void SetSplitDividerPixels(int leftPx, int widthPx)
        {
            _dividerOverrideLeft = leftPx;
            _dividerOverrideWidth = Math.Max(1, widthPx);
            InvalidateStrip();
        }

        private int GroupCount
        {
            get { return _splitStrip ? 2 : 1; }
        }

        private int GroupOf(FluentTab tab)
        {
            return _splitStrip && tab.Group == 1 ? 1 : 0;
        }

        /// <summary>
        /// Whether the tab strip is shown. Hide it for fullscreen/kiosk modes: the reserved
        /// padding collapses so content fills the whole window, and strip painting, mouse
        /// handling, and caption-button hit testing are suspended until it is shown again.
        /// Defaults to true.
        /// </summary>
        public bool StripVisible
        {
            get { return _stripVisible; }
            set
            {
                if (_stripVisible == value)
                {
                    return;
                }

                _stripVisible = value;
                ClearHoverState();
                UpdateMetrics();
                Invalidate();
            }
        }

        private bool _stripVisible = true;

        /// <summary>Height of the tab strip in logical pixels (scaled for DPI). Defaults to 44 (Firefox Nova: 32px tab + 6px above and below).</summary>
        public int StripHeight
        {
            get { return _stripHeight; }
            set
            {
                _stripHeight = Math.Max(28, value);
                UpdateMetrics();
                Invalidate();
            }
        }

        private int _stripHeight = 44;

        /// <summary>Height of the tab cards in logical pixels (scaled for DPI). Defaults to 32.</summary>
        public int TabHeight
        {
            get { return _tabHeight; }
            set
            {
                _tabHeight = Math.Max(20, value);
                InvalidateStrip();
            }
        }

        private int _tabHeight = 32;

        /// <summary>
        /// How many minimum-width tabs must stay visible in the strip, enforced as a floor on how
        /// narrow the user can drag the window. Below this the strip is all buttons and slivers and
        /// the window stops being usable. Set to 0 to allow any width. Defaults to 2.
        /// </summary>
        public int MinVisibleTabs
        {
            get { return _minVisibleTabs; }
            set { _minVisibleTabs = Math.Max(0, value); }
        }

        private int _minVisibleTabs = 2;

        /// <summary>
        /// Whether the window's <see cref="Form.Icon" /> is drawn in the strip, in a slot just before
        /// the start of the tabs (the visual right in a mirrored/RTL window). Defaults to false.
        /// </summary>
        public bool ShowWindowIcon
        {
            get { return _showWindowIcon; }
            set
            {
                _showWindowIcon = value;
                InvalidateStrip();
            }
        }

        private bool _showWindowIcon;

        /// <summary>
        /// Whether a tab-list dropdown button is shown just before the start of the tabs (after the
        /// window icon when both are enabled). Clicking it opens a Fluent-styled dropdown listing
        /// every open tab (the selected one marked with an accent indicator); picking an entry
        /// selects that tab. Hosts can append extra sections via <see cref="TabListOpening" />.
        /// Defaults to false.
        /// </summary>
        public bool ShowTabListButton
        {
            get { return _showTabListButton; }
            set
            {
                _showTabListButton = value;
                InvalidateStrip();
            }
        }

        private bool _showTabListButton;

        /// <summary>Header text of the open-tabs section in the tab-list dropdown.</summary>
        public string TabListOpenTabsHeader { get; set; } = "Open tabs";

        /// <summary>
        /// Captions for the tab context menu (right-click a tab). English by default;
        /// set them to localize, as with the caption-button tooltips.
        /// </summary>
        public string CloseTabMenuText { get; set; } = "Close tab";
        public string CloseOtherTabsMenuText { get; set; } = "Close other tabs";
        public string CloseTabsToTheRightMenuText { get; set; } = "Close tabs to the right";

        /// <summary>
        /// Both directions are offered. Which list each caption labels depends on the layout:
        /// in a right-to-left strip the tabs AFTER the clicked one are the visual left, and
        /// the ones before it are the visual right.
        /// </summary>
        public string CloseTabsToTheLeftMenuText { get; set; } = "Close tabs to the left";

        /// <summary>
        /// Raised when a tab is right-clicked, after the built-in close commands have been
        /// added. Handlers may append their own sections, exactly as with
        /// <see cref="TabListOpening" />. Set <see cref="TabContextMenuOpeningEventArgs.Cancel" />
        /// to suppress the menu entirely.
        /// </summary>
        public event EventHandler<TabContextMenuOpeningEventArgs> TabContextMenuOpening;

        /// <summary>
        /// Optional accent color used for the active-tab indicator in the tab-list dropdown
        /// and the split divider's hover highlight. Falls back to the palette text color when null.
        /// </summary>
        public Color? AccentColor { get; set; }

        /// <summary>
        /// Optional color for the split divider's resting state, letting it match a host
        /// content divider exactly. Falls back to the palette separator when null.
        /// </summary>
        public Color? SplitDividerColor { get; set; }

        /// <summary>
        /// Tooltip text for the caption buttons. The buttons are custom-drawn and hit-tested
        /// as the standard caption controls, so Windows would otherwise show its own
        /// OS-language tooltips; setting these shows localized ones instead. English by default.
        /// </summary>
        public string MinimizeToolTip { get; set; } = "Minimize";
        public string MaximizeToolTip { get; set; } = "Maximize";
        public string RestoreToolTip { get; set; } = "Restore";
        public string CloseToolTip { get; set; } = "Close";

        private string CaptionButtonToolTip(int htCode)
        {
            if (htCode == NativeMethods.HTCLOSE) return CloseToolTip;
            if (htCode == NativeMethods.HTMINBUTTON) return MinimizeToolTip;
            if (htCode == NativeMethods.HTMAXBUTTON) return NativeMethods.IsZoomed(Handle) ? RestoreToolTip : MaximizeToolTip;
            return null;
        }

        /// <summary>
        /// Raised right before the tab-list dropdown opens, with the open-tabs section already
        /// populated. Handlers may append additional sections (e.g. recently closed documents).
        /// </summary>
        public event EventHandler<TabListOpeningEventArgs> TabListOpening;

        /// <summary>The active palette, for controls that must match the strip theme.</summary>
        internal Palette CurrentPalette
        {
            get { return _palette; }
        }

        /// <summary>The color theme. <see cref="FluentChromeTabsTheme.Auto" /> follows the Windows setting live.</summary>
        public FluentChromeTabsTheme Theme
        {
            get { return _theme; }
            set
            {
                _theme = value;
                ApplyTheme();
            }
        }

        /// <summary>
        /// Optional custom chrome color. When set, the tab strip takes this exact color and a full
        /// palette (tab surfaces, text, hover states) is derived from its brightness, overriding
        /// <see cref="Theme" />. Set to null to return to the Light/Dark/Auto themes.
        /// </summary>
        public Color? CustomThemeColor
        {
            get { return _customThemeColor; }
            set
            {
                _customThemeColor = value;
                ApplyTheme();
            }
        }

        private Color? _customThemeColor;

        /// <summary>True when the effective theme (after resolving <see cref="FluentChromeTabsTheme.Auto" /> or a custom color) is dark.</summary>
        public bool IsDarkTheme
        {
            get { return _isDark; }
        }

        /// <summary>Suggested background color for tab content, matching the active tab surface.</summary>
        public Color ContentBackColor
        {
            get { return _palette.TabActive; }
        }

        /// <summary>Suggested text color for tab content.</summary>
        public Color ContentForeColor
        {
            get { return _palette.TextActive; }
        }

        /// <summary>Raised after a tab is added to this window.</summary>
        public event EventHandler<FluentTabEventArgs> TabAdded;

        /// <summary>Raised before a user-initiated tab close; set Cancel to keep the tab.</summary>
        public event EventHandler<FluentTabClosingEventArgs> TabClosing;

        /// <summary>Raised after a tab is closed and its content disposed.</summary>
        public event EventHandler<FluentTabEventArgs> TabClosed;

        /// <summary>Raised when the selected tab changes.</summary>
        public event EventHandler<FluentTabEventArgs> SelectedTabChanged;

        /// <summary>
        /// Raised on every left press on a tab's body, also when it is already the selected tab
        /// (which <see cref="SelectedTabChanged" /> does not report).
        /// </summary>
        public event EventHandler<FluentTabEventArgs> TabPressed;

        /// <summary>Raised when the user requests a new tab (the "+" button or Ctrl+T).</summary>
        public event EventHandler<NewTabRequestedEventArgs> NewTabRequested;

        /// <summary>
        /// Raised when the user drags a tab across the divider into the other split-strip region
        /// (only fires when <see cref="SplitStrip" /> is on and the tab's region actually changed).
        /// </summary>
        public event EventHandler<FluentTabGroupEventArgs> TabDraggedToGroup;

        /// <summary>Raised whenever the effective theme changes (including Windows switching light/dark in Auto mode).</summary>
        public event EventHandler ThemeChanged;

        /// <summary>Adds a tab to the end of the strip and selects it.</summary>
        public FluentTab AddTab(string title, Control content)
        {
            return InsertTab(_tabs.Count, new FluentTab(title, content));
        }

        /// <summary>
        /// Adds a content-less tab (loose mode): the tab is pure metadata and your own static content —
        /// a WebView, a UserControl, anything you add to the form yourself — reacts to
        /// <see cref="SelectedTabChanged" />.
        /// </summary>
        public FluentTab AddTab(string title)
        {
            return InsertTab(_tabs.Count, new FluentTab(title));
        }

        /// <summary>Adds a tab to the end of the strip and selects it.</summary>
        public FluentTab AddTab(FluentTab tab)
        {
            return InsertTab(_tabs.Count, tab);
        }

        /// <summary>Inserts a tab at <paramref name="index" /> and selects it.</summary>
        public FluentTab InsertTab(int index, FluentTab tab)
        {
            if (tab == null)
            {
                throw new ArgumentNullException("tab");
            }

            if (tab.Owner != null)
            {
                throw new InvalidOperationException("Tab already belongs to a window. Close it there first.");
            }

            // Same slide as a close, for the same reason and so both read as one behavior:
            // the existing tabs shuffle aside to make room rather than snapping.
            Dictionary<FluentTab, int> before = CaptureTabPositions();

            AttachTab(index, tab);

            // Slid before the host is told. A TabAdded handler may close tabs or add its own,
            // and each of those runs its own capture/animate cycle; sliding after them would
            // measure against a baseline two layout generations old, sending tabs travelling
            // from positions they never occupied. Whatever the handler does then animates on
            // its own terms, which is what it would have done had it been called separately.
            AnimateReflowFrom(before);

            TabAdded?.Invoke(this, new FluentTabEventArgs(tab));
            SelectTabCore(_tabs.IndexOf(tab));

            return tab;
        }

        /// <summary>Closes <paramref name="tab" />, raising <see cref="TabClosing" /> first. Returns false if canceled.</summary>
        public bool CloseTab(FluentTab tab)
        {
            if (tab == null || !_tabs.Contains(tab))
            {
                return false;
            }

            FluentTabClosingEventArgs closing = new FluentTabClosingEventArgs(tab);
            TabClosing?.Invoke(this, closing);

            if (closing.Cancel)
            {
                return false;
            }

            // Captured before the removal so the surviving tabs can be slid from where they
            // are now to wherever the reflow puts them.
            Dictionary<FluentTab, int> before = CaptureTabPositions();

            DetachTabCore(tab);

            AnimateReflowFrom(before);

            tab.Content?.Dispose();
            TabClosed?.Invoke(this, new FluentTabEventArgs(tab));

            if (_tabs.Count == 0 && ExitOnLastTabClose)
            {
                Close();
            }

            return true;
        }

        /// <summary>
        /// Requests a new tab exactly as the "+" button does: raises <see cref="NewTabRequested" /> and adds
        /// the resulting tab (a blank one when no handler set content). <paramref name="group" /> reports
        /// which split region's "+" was pressed (always 0 when the strip is not split).
        /// </summary>
        public void RequestNewTab(int group = 0)
        {
            NewTabRequestedEventArgs args = new NewTabRequestedEventArgs { Group = group };

            if (args.Title == "New Tab" && _newTabCounter > 0)
            {
                args.Title = "New Tab " + (_newTabCounter + 1);
            }

            NewTabRequested?.Invoke(this, args);

            if (args.Cancel)
            {
                return;
            }

            _newTabCounter++;
            FluentTab added = AddTab(args.Title, args.Content);
            added.Group = group;
        }

        #endregion

        #region Tab bookkeeping

        private void AttachTab(int index, FluentTab tab)
        {
            index = Math.Max(0, Math.Min(index, _tabs.Count));

            tab.Owner = this;

            if (tab.Content != null)
            {
                tab.Content.Dock = DockStyle.Fill;
                tab.Content.Visible = false;
                Controls.Add(tab.Content);
            }

            if (index <= _selectedIndex)
            {
                _selectedIndex++;
            }

            _tabs.Insert(index, tab);
            InvalidateStrip();
        }

        // ── Close reflow animation ───────────────────────────────────────
        // Closing a tab makes every tab after it jump left by a tab's width. Without a
        // transition the strip just changes under the pointer and it is genuinely unclear
        // whether the click closed the intended tab, or landed on its neighbour.
        //
        // The layout itself is never animated: TabRect stays authoritative and the new
        // positions are live immediately, so hit-testing, dragging and scrolling all
        // behave as if the reflow were instant. Only PAINTING is offset, by a per-tab
        // delta that decays to zero — so the tabs are drawn sliding from where they were
        // to where they already are.

        /// <summary>How long the tabs take to slide into their new places.</summary>
        private const int CloseReflowMs = 130;

        /// <summary>
        /// How long a scroll takes to catch up visually. Shorter than a reflow: scrolling is
        /// often repeated (wheel notches, held chevron), and a longer glide would lag behind
        /// the input instead of following it.
        /// </summary>
        private const int ScrollGlideMs = 110;

        /// <summary>Per-group paint offsets for the scroll still catching up, in pixels.</summary>
        private readonly Dictionary<int, int> _scrollGlideByGroup = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _scrollGlideStartByGroup = new Dictionary<int, int>();

        /// <summary>
        /// Start time PER GROUP. A split strip's two regions scroll independently, and a
        /// single shared tick would have a second group's scroll restart the first group's
        /// easing from its original amplitude — springing that band backwards mid-flight and
        /// extending it for as long as the other kept scrolling.
        /// </summary>
        private readonly Dictionary<int, int> _scrollGlideTickByGroup = new Dictionary<int, int>();

        private bool ScrollGliding
        {
            get { return _scrollGlideByGroup.Count > 0; }
        }

        /// <summary>Paint-time X shift for a group's tabs while its scroll is catching up.</summary>
        private int ScrollGlidePx(int group)
        {
            int offset;
            return _scrollGlideByGroup.TryGetValue(group, out offset) ? offset : 0;
        }

        /// <summary>
        /// Starts (or extends) the visual catch-up for a group whose scroll offset just moved
        /// by <paramref name="deltaPx" />.
        /// </summary>
        private void BeginScrollGlide(int group, int deltaPx)
        {
            if (!IsHandleCreated || !StripVisible || _dragging || deltaPx == 0)
            {
                return;
            }

            // Added to whatever is still outstanding, so a second notch arriving mid-glide
            // extends the same motion instead of restarting it from the new position and
            // visibly stuttering.
            int pending = ScrollGlidePx(group) + deltaPx;

            if (pending == 0)
            {
                _scrollGlideByGroup.Remove(group);
                _scrollGlideStartByGroup.Remove(group);
                _scrollGlideTickByGroup.Remove(group);
                return;
            }

            _scrollGlideByGroup[group] = pending;
            _scrollGlideStartByGroup[group] = pending;
            _scrollGlideTickByGroup[group] = Environment.TickCount;

            if (_scrollGlideTimer == null)
            {
                _scrollGlideTimer = new Timer { Interval = 15 };
                _scrollGlideTimer.Tick += OnScrollGlideTick;
            }

            _scrollGlideTimer.Start();
            InvalidateStrip();
        }

        private Timer _scrollGlideTimer;

        private void OnScrollGlideTick(object sender, EventArgs e)
        {
            int now = Environment.TickCount;

            // Each group eases on its own clock, and finishes on its own.
            foreach (int group in new List<int>(_scrollGlideByGroup.Keys))
            {
                int start;
                int tick;

                if (!_scrollGlideStartByGroup.TryGetValue(group, out start)
                    || !_scrollGlideTickByGroup.TryGetValue(group, out tick))
                {
                    _scrollGlideByGroup.Remove(group);
                    continue;
                }

                int elapsed = now - tick;

                if (elapsed >= ScrollGlideMs)
                {
                    _scrollGlideByGroup.Remove(group);
                    _scrollGlideStartByGroup.Remove(group);
                    _scrollGlideTickByGroup.Remove(group);
                    continue;
                }

                double remaining = 1.0 - (double) elapsed / ScrollGlideMs;
                remaining = remaining * remaining * remaining;
                _scrollGlideByGroup[group] = (int) Math.Round(start * remaining);
            }

            if (_scrollGlideByGroup.Count == 0)
            {
                _scrollGlideTimer?.Stop();
            }

            InvalidateStrip();
        }

        private void StopScrollGlide()
        {
            _scrollGlideTimer?.Stop();
            _scrollGlideByGroup.Clear();
            _scrollGlideStartByGroup.Clear();
            _scrollGlideTickByGroup.Clear();
        }

        /// <summary>Paint-time X offsets by tab, still travelling back to zero.</summary>
        private readonly Dictionary<FluentTab, int> _reflowOffsetByTab = new Dictionary<FluentTab, int>();

        /// <summary>The offsets the current slide started from; each frame is eased off these
        /// rather than off the last frame, so rounding cannot accumulate into a drift.</summary>
        private readonly Dictionary<FluentTab, int> _reflowStartOffsetByTab = new Dictionary<FluentTab, int>();

        private Timer _reflowTimer;
        private int _reflowStartTick;

        /// <summary>True while tabs are still sliding, so painting should apply offsets.</summary>
        private bool ReflowAnimating
        {
            get { return _reflowOffsetByTab.Count > 0; }
        }

        /// <summary>
        /// Paint offset for a tab mid-slide. Zero for everything else, so a strip that is
        /// not animating pays nothing and draws exactly where TabRect says.
        /// </summary>
        private int ReflowOffsetPx(int index)
        {
            int offset;
            return _reflowOffsetByTab.TryGetValue(_tabs[index], out offset) ? offset : 0;
        }

        /// <summary>
        /// Records where each tab is now, to be compared with where it lands once the
        /// caller has changed the layout. Returns null when nothing would be animated.
        /// </summary>
        private Dictionary<FluentTab, int> CaptureTabPositions()
        {
            // Animating while dragging would fight the drag for the same pixels, and an
            // invisible strip has nothing to show.
            //
            // IsHandleCreated also gates the whole startup burst: a host adds its tabs in
            // its constructor, before the window is on screen, and sliding tabs that were
            // never visible in their old places just delays the first paint.
            if (!IsHandleCreated || !StripVisible || _dragging || _tabs.Count == 0)
            {
                return null;
            }

            Dictionary<FluentTab, int> before = new Dictionary<FluentTab, int>();

            for (int i = 0; i < _tabs.Count; i++)
            {
                before[_tabs[i]] = TabRect(i).Left;
            }

            return before;
        }

        /// <summary>
        /// Starts the slide: every surviving tab that moved gets an offset equal to how far
        /// it moved, which the timer then walks back to zero.
        /// </summary>
        private void AnimateReflowFrom(Dictionary<FluentTab, int> before)
        {
            // Cleared FIRST, whatever happens next: the entries key on FluentTab objects, so
            // a slide left behind by a bailed-out call holds those tabs (and their Content)
            // alive, and keeps easing toward a layout that no longer exists.
            StopReflowAnimation();

            if (before == null || !StripVisible || _dragging)
            {
                return;
            }

            bool any = false;

            for (int i = 0; i < _tabs.Count; i++)
            {
                int wasLeft;

                if (!before.TryGetValue(_tabs[i], out wasLeft))
                {
                    continue;
                }

                int shift = wasLeft - TabRect(i).Left;

                if (shift != 0)
                {
                    _reflowOffsetByTab[_tabs[i]] = shift;
                    _reflowStartOffsetByTab[_tabs[i]] = shift;
                    any = true;
                }
            }

            if (!any)
            {
                return;
            }

            // Restarted from zero on every close, so closing several tabs quickly reads as
            // one continuous motion rather than each slide fighting the last.
            _reflowStartTick = Environment.TickCount;

            if (_reflowTimer == null)
            {
                _reflowTimer = new Timer { Interval = 15 };
                _reflowTimer.Tick += OnReflowTick;
            }

            _reflowTimer.Start();
            InvalidateStrip();
        }

        private void OnReflowTick(object sender, EventArgs e)
        {
            int elapsed = Environment.TickCount - _reflowStartTick;

            if (elapsed >= CloseReflowMs)
            {
                StopReflowAnimation();
                InvalidateStrip();
                return;
            }

            // Ease-out cubic: the tabs leave quickly and settle gently, so the eye follows
            // the movement without the strip feeling sluggish.
            double t = (double) elapsed / CloseReflowMs;
            double remaining = 1.0 - t;
            remaining = remaining * remaining * remaining;

            foreach (FluentTab tab in new List<FluentTab>(_reflowOffsetByTab.Keys))
            {
                int start;

                // Looked up rather than indexed: a host handler re-entering CloseTab from a
                // TabClosed/SelectedTabChanged callback restarts the slide mid-tick, leaving
                // this snapshot holding keys the start map no longer has.
                if (_reflowStartOffsetByTab.TryGetValue(tab, out start))
                {
                    _reflowOffsetByTab[tab] = (int) Math.Round(start * remaining);
                }
            }

            InvalidateStrip();
        }

        private void StopReflowAnimation()
        {
            _reflowTimer?.Stop();
            _reflowOffsetByTab.Clear();
            _reflowStartOffsetByTab.Clear();
        }

        /// <summary>Removes a tab from this window without disposing its content (used for closing and for drag transfers).</summary>
        private void DetachTabCore(FluentTab tab)
        {
            // Drag transfers and window tear-offs come straight here, bypassing the
            // capture/animate pair in CloseTab. Any slide still running refers to a tab
            // list that is about to change under it, and its entries would strand this
            // tab in the offset maps.
            StopReflowAnimation();

            int index = _tabs.IndexOf(tab);

            _tabs.RemoveAt(index);

            if (tab.Content != null)
            {
                Controls.Remove(tab.Content);
            }

            tab.Owner = null;

            if (_tabs.Count == 0)
            {
                _selectedIndex = -1;
            }
            else
            {
                int newSelection = _selectedIndex;

                if (index < newSelection)
                {
                    newSelection--;
                }
                else if (index == newSelection)
                {
                    newSelection = Math.Min(index, _tabs.Count - 1);
                }

                _selectedIndex = -1;
                SelectTabCore(newSelection);
            }

            InvalidateStrip();
        }

        private void SelectTabCore(int index)
        {
            if (index == _selectedIndex)
            {
                return;
            }

            if (_selectedIndex >= 0 && _selectedIndex < _tabs.Count && _tabs[_selectedIndex].Content != null)
            {
                _tabs[_selectedIndex].Content.Visible = false;
            }

            int previous = _selectedIndex;
            _selectedIndex = index;

            // Selection changes which tab carries the widened size, so the content to the
            // LEFT of the viewport can grow or shrink. Left uncorrected, that slides every
            // visible tab sideways on a click that was only meant to change selection.
            // Absorbing the delta into the scroll offset keeps the strip still.
            AbsorbActiveWidthShift(previous, index);

            // Selecting a tab that is scrolled out of sight must bring it back, or the
            // strip would show no active tab at all.
            EnsureTabVisible(index);

            if (index >= 0 && index < _tabs.Count)
            {
                FluentTab tab = _tabs[index];

                if (tab.Content != null)
                {
                    tab.Content.Visible = true;
                    tab.Content.Select();
                }

                SelectedTabChanged?.Invoke(this, new FluentTabEventArgs(tab));
            }

            InvalidateStrip();
        }

        internal void InvalidateStrip()
        {
            if (IsHandleCreated)
            {
                Invalidate(new Rectangle(0, 0, ClientSize.Width, StripHeightPx));
            }
        }

        #endregion

        #region Window icon

        private Icon _windowIconSource;
        private Bitmap _windowIconBitmap;
        private int _windowIconBitmapSize;

        /// <summary>The window icon rendered at the requested size, cached until the icon or DPI changes.</summary>
        private Bitmap GetWindowIconBitmap(int size)
        {
            if (!ReferenceEquals(_windowIconSource, Icon) || _windowIconBitmapSize != size)
            {
                _windowIconBitmap?.Dispose();
                _windowIconBitmap = null;
                _windowIconSource = Icon;
                _windowIconBitmapSize = size;

                if (Icon != null)
                {
                    // new Icon(icon, w, h) picks the closest embedded frame for crisp small sizes
                    using (Icon sized = new Icon(Icon, size, size))
                    {
                        _windowIconBitmap = sized.ToBitmap();
                    }
                }
            }

            return _windowIconBitmap;
        }

        #endregion

        #region Tab list dropdown

        private TabListDropDown _tabListDropDown;
        private int _tabListClosedTick;
        private int _tabListOpenGroup = -1;

        /// <summary>True while the tab-list popup is on screen (drives the toggle's open state).</summary>
        public bool IsTabListOpen
        {
            get { return _tabListDropDown != null && !_tabListDropDown.IsDisposed && _tabListDropDown.Visible; }
        }

        /// <summary>The split region whose tab-list popup is open, or -1 when none is.</summary>
        public int TabListOpenGroup
        {
            get { return IsTabListOpen ? _tabListOpenGroup : -1; }
        }

        /// <summary>
        /// The region a tab-list popup should open against: the selected tab's own region while
        /// the strip is split (that region owns the visible active tab), and region 0 otherwise.
        /// </summary>
        public int SelectedTabGroup
        {
            get { return _splitStrip && SelectedTab != null ? GroupOf(SelectedTab) : 0; }
        }

        /// <summary>
        /// Opens the Fluent-styled popup listing the region's open tabs plus any host-provided
        /// sections; picking an entry activates it. Acts as a toggle: invoking it while
        /// the popup is open (or immediately after the opening click dismissed it via
        /// deactivation) closes instead of reopening.
        /// </summary>
        public void ShowTabListMenu(int group)
        {
            ShowTabListMenu(group, 0);
        }

        /// <summary>
        /// Opens the tab list as an Alt+Tab-style quick switcher when <paramref name="quickSwitchStep" />
        /// is non-zero: the popup stays up while Ctrl is held, further Tab / Shift+Tab presses step
        /// the highlight by ±1, and releasing Ctrl activates the highlighted tab. The step given here
        /// is applied immediately, so one Ctrl+Tab press-and-release behaves like a plain "next tab".
        /// While the switcher is already open, another call just steps it instead of toggling it shut.
        /// </summary>
        public void ShowTabListMenu(int group, int quickSwitchStep)
        {
            if (IsTabListOpen)
            {
                // Ctrl is still down and the user pressed Tab again — step, don't toggle.
                if (quickSwitchStep != 0)
                {
                    _tabListDropDown.StepQuickSwitchExternal(quickSwitchStep);
                    return;
                }

                _tabListDropDown.Close();
                return;
            }

            // Reclicking the toggle: the mousedown first deactivates the popup (closing it),
            // then reaches us — an immediate reopen would make the toggle feel stuck open.
            // The guard is for the pointer only: a Ctrl+Tab arriving inside that window is a
            // deliberate reopen, and swallowing it would make the switcher feel broken.
            if (quickSwitchStep == 0 && Environment.TickCount - _tabListClosedTick < 300)
            {
                return;
            }

            List<TabListSection> sections = new List<TabListSection>();
            TabListSection open = new TabListSection(TabListOpenTabsHeader);

            foreach (FluentTab tab in _tabs)
            {
                if (_splitStrip && GroupOf(tab) != group)
                {
                    continue;
                }

                FluentTab captured = tab;
                string listText = !string.IsNullOrEmpty(tab.ListTitle) ? tab.ListTitle
                    : string.IsNullOrEmpty(tab.Title) ? "…" : tab.Title;
                // The × is the overflow escape hatch: once there are enough tabs that the
                // strip's own close buttons have shrunk away, this list is where several can
                // be shed in one visit (the popup stays open across each close).
                open.Items.Add(new TabListItem(
                    listText,
                    tab == SelectedTab || tab.Highlighted,
                    () => SelectedTab = captured,
                    captured.CanClose ? (Func<bool>) (() => CloseTab(captured)) : null));
            }

            if (open.Items.Count > 0)
            {
                sections.Add(open);
            }

            TabListOpening?.Invoke(this, new TabListOpeningEventArgs(sections, group));

            sections.RemoveAll(s => s.Items.Count == 0);

            if (sections.Count == 0)
            {
                return;
            }

            // A header earns its row only by telling one section from another. When the open
            // tabs are the whole popup it just restates what the list obviously is, so it is
            // dropped; a host that appended its own section (recently closed, say) keeps it,
            // because then the heading is what separates the two.
            if (sections.Count == 1 && sections[0] == open)
            {
                TabListSection unheaded = new TabListSection(null);
                unheaded.Items.AddRange(open.Items);
                sections[0] = unheaded;
            }

            HideStripToolTip();

            TabListDropDown drop = new TabListDropDown(this, sections);
            _tabListDropDown = drop;
            _tabListOpenGroup = group;

            drop.FormClosed += (s, e) =>
            {
                _tabListClosedTick = Environment.TickCount;
                _tabListDropDown = null;
                _tabListOpenGroup = -1;
                InvalidateStrip();

                // Suppressed tooltips were skipped while the popup was up, and the pointer
                // may be resting on an item without moving again — re-derive from the hover
                // state that is already current, or that item stays silent.
                UpdateToolTip();
                BeginInvoke(new Action(drop.Dispose));
            };

            // Anchor below the toggle. In a mirrored (RTL) window the anchor lands on the
            // button's visual-right edge, so the popup right-aligns to it; clamp on-screen.
            Rectangle rect = TabListButtonRect(group);
            Point anchor = PointToScreen(new Point(rect.Left, StripHeightPx - Dpi(2)));
            Rectangle workArea = Screen.FromControl(this).WorkingArea;

            int x = IsMirrored ? anchor.X - drop.Width : anchor.X;
            x = Math.Max(workArea.Left, Math.Min(x, workArea.Right - drop.Width));
            int y = Math.Min(anchor.Y, workArea.Bottom - drop.Height);

            drop.Location = new Point(x, y);
            drop.Show(this);

            // Arm after Show: the popup must own OS focus before it can see the Ctrl keyup.
            if (quickSwitchStep != 0)
            {
                drop.BeginQuickSwitch(quickSwitchStep);
            }

            InvalidateStrip();
        }

        private TabListDropDown _tabContextMenu;

        private bool IsTabContextMenuOpen
        {
            get { return _tabContextMenu != null && !_tabContextMenu.IsDisposed && _tabContextMenu.Visible; }
        }

        /// <summary>
        /// Opens the right-click menu for <paramref name="tab" /> at <paramref name="clientPoint" />.
        ///
        /// Reuses <see cref="TabListDropDown" /> rather than a ContextMenuStrip: the strip is
        /// entirely owner-drawn from its palette, and a stock menu would be the one piece of
        /// chrome not following the theme (and not mirroring for RTL).
        ///
        /// The bulk commands matter more than the single close here — someone with enough tabs
        /// open that the × has vanished wants to shed several at once, not click one at a time.
        /// </summary>
        public void ShowTabContextMenu(FluentTab tab, Point clientPoint)
        {
            if (tab == null || !_tabs.Contains(tab))
            {
                return;
            }

            List<TabListSection> sections = new List<TabListSection>();
            TabListSection commands = new TabListSection(null);

            FluentTab captured = tab;

            if (tab.CanClose)
            {
                commands.Items.Add(new TabListItem(
                    CloseTabMenuText, false, () => CloseTab(captured)));
            }

            if (OtherClosableTabs(captured).Count > 0)
            {
                commands.Items.Add(new TabListItem(
                    CloseOtherTabsMenuText, false, () => CloseTabs(OtherClosableTabs(captured))));
            }

            // Both sides are offered, as browsers do. "Left" and "Right" are visual claims
            // about where the tabs sit on screen, and a mirrored strip runs the other way,
            // so each side's caption is chosen by layout rather than by list order: the
            // tabs BEFORE this one are the visual left in LTR and the visual right in RTL.
            bool mirrored = IsMirrored || RightToLeft == RightToLeft.Yes;

            if (ClosableTabsBefore(captured).Count > 0)
            {
                commands.Items.Add(new TabListItem(
                    mirrored ? CloseTabsToTheRightMenuText : CloseTabsToTheLeftMenuText,
                    false,
                    () => CloseTabs(ClosableTabsBefore(captured))));
            }

            if (ClosableTabsAfter(captured).Count > 0)
            {
                commands.Items.Add(new TabListItem(
                    mirrored ? CloseTabsToTheLeftMenuText : CloseTabsToTheRightMenuText,
                    false,
                    () => CloseTabs(ClosableTabsAfter(captured))));
            }

            if (commands.Items.Count > 0)
            {
                sections.Add(commands);
            }

            TabContextMenuOpeningEventArgs args = new TabContextMenuOpeningEventArgs(sections, tab);
            TabContextMenuOpening?.Invoke(this, args);

            if (args.Cancel)
            {
                return;
            }

            sections.RemoveAll(sec => sec.Items.Count == 0);

            if (sections.Count == 0)
            {
                return;
            }

            // The tab list may be open on the same strip; a second popup on screen would leave
            // whichever loses focus painted as still-open.
            if (IsTabListOpen)
            {
                _tabListDropDown.Close();
            }

            if (IsTabContextMenuOpen)
            {
                _tabContextMenu.Close();
            }

            // The hover tooltip for this very tab is on screen; leaving it up would float
            // over the menu that is about to open on top of it.
            HideStripToolTip();

            TabListDropDown menu = new TabListDropDown(this, sections);
            _tabContextMenu = menu;

            // Deliberately NOT stored in _tabListDropDown/_tabListOpenGroup: those drive the
            // tab-list toggle's pressed state and its reopen guard, and a context menu must
            // not make that button look held down.
            menu.FormClosed += (s, e) =>
            {
                _tabContextMenu = null;
                UpdateToolTip();
                BeginInvoke(new Action(menu.Dispose));
            };

            Point anchor = PointToScreen(clientPoint);
            Rectangle workArea = Screen.FromControl(this).WorkingArea;

            // Menus open away from the pointer along the reading direction, so a mirrored
            // window hangs the menu to the pointer's left.
            int x = IsMirrored ? anchor.X - menu.Width : anchor.X;
            x = Math.Max(workArea.Left, Math.Min(x, workArea.Right - menu.Width));
            int y = Math.Min(anchor.Y, workArea.Bottom - menu.Height);

            menu.Location = new Point(x, y);
            menu.Show(this);
        }

        /// <summary>Every closable tab in <paramref name="tab" />'s group except itself.</summary>
        private List<FluentTab> OtherClosableTabs(FluentTab tab)
        {
            List<FluentTab> result = new List<FluentTab>();

            foreach (FluentTab candidate in _tabs)
            {
                if (candidate != tab && candidate.CanClose && SameMenuScope(candidate, tab))
                {
                    result.Add(candidate);
                }
            }

            return result;
        }

        /// <summary>Closable tabs positioned before <paramref name="tab" /> in its group.</summary>
        private List<FluentTab> ClosableTabsBefore(FluentTab tab)
        {
            List<FluentTab> result = new List<FluentTab>();
            int upTo = _tabs.IndexOf(tab);

            for (int i = 0; i < upTo; i++)
            {
                if (_tabs[i].CanClose && SameMenuScope(_tabs[i], tab))
                {
                    result.Add(_tabs[i]);
                }
            }

            return result;
        }

        /// <summary>Closable tabs positioned after <paramref name="tab" /> in its group.</summary>
        private List<FluentTab> ClosableTabsAfter(FluentTab tab)
        {
            List<FluentTab> result = new List<FluentTab>();
            int from = _tabs.IndexOf(tab);

            for (int i = from + 1; i < _tabs.Count; i++)
            {
                if (_tabs[i].CanClose && SameMenuScope(_tabs[i], tab))
                {
                    result.Add(_tabs[i]);
                }
            }

            return result;
        }

        /// <summary>
        /// A split strip's two regions are separate tab strips to the user, so a bulk close
        /// started in one must not reach across and empty the other.
        /// </summary>
        private bool SameMenuScope(FluentTab a, FluentTab b)
        {
            return !_splitStrip || GroupOf(a) == GroupOf(b);
        }

        /// <summary>
        /// Closes a pre-resolved set of tabs. The list is snapshotted by the caller because
        /// <see cref="CloseTab" /> mutates <c>_tabs</c>, and a TabClosing handler may cancel
        /// any one of them — so this iterates the snapshot and lets refusals stand.
        /// </summary>
        private void CloseTabs(List<FluentTab> tabs)
        {
            foreach (FluentTab tab in tabs)
            {
                CloseTab(tab);
            }
        }

        #endregion

        #region Theme

        private void ApplyTheme()
        {
            if (_customThemeColor.HasValue)
            {
                _isDark = Palette.IsDarkColor(_customThemeColor.Value);
                _palette = Palette.FromColor(_customThemeColor.Value);
            }
            else
            {
                _isDark = _theme == FluentChromeTabsTheme.Dark || (_theme == FluentChromeTabsTheme.Auto && IsSystemDark());
                _palette = _isDark ? Palette.Dark : Palette.Light;
            }

            BackColor = _palette.Strip;

            if (IsHandleCreated)
            {
                ApplyDwmDarkMode();
                RepaintNonClient();
            }

            ThemeChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }

        private static bool IsSystemDark()
        {
            try
            {
                object value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                return value is int && (int) value == 0;
            }
            catch
            {
                return false;
            }
        }

        private void ApplyDwmDarkMode()
        {
            int dark = _isDark ? 1 : 0;

            if (NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
            {
                // Windows 10 before 20H1 used attribute 19 for the same flag
                NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));
            }
        }

        /// <summary>
        /// Forces the non-client frame to repaint after a DWM attribute change on an already-visible
        /// window: toggling WM_NCACTIVATE is the only thing that reliably does this on Windows 10.
        /// </summary>
        private void RepaintNonClient()
        {
            IntPtr active = new IntPtr(_windowActive ? 1 : 0);
            IntPtr inactive = new IntPtr(_windowActive ? 0 : 1);

            NativeMethods.SendMessage(Handle, NativeMethods.WM_NCACTIVATE, inactive, IntPtr.Zero);
            NativeMethods.SendMessage(Handle, NativeMethods.WM_NCACTIVATE, active, IntPtr.Zero);
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General && _theme == FluentChromeTabsTheme.Auto)
            {
                ApplyTheme();
            }
        }

        #endregion

        #region Metrics

        // Control.DeviceDpi can stay stuck at 96 in .NET Framework WinForms apps that are
        // DPI-aware at the process level but run in the legacy WinForms DPI mode (no
        // DpiAwareness app.config entry). Ask the window itself so strip metrics match
        // the monitor's real scale; cached until the handle or DPI changes.
        private int _effectiveDpi;

        internal int EffectiveDpi
        {
            get
            {
                if (_effectiveDpi > 0)
                {
                    return _effectiveDpi;
                }

                if (IsHandleCreated)
                {
                    try
                    {
                        _effectiveDpi = (int) NativeMethods.GetDpiForWindow(Handle);
                    }
                    catch (EntryPointNotFoundException)
                    {
                        // Pre-1607 Windows 10 — fall back to whatever WinForms reports
                        _effectiveDpi = DeviceDpi;
                    }

                    if (_effectiveDpi <= 0)
                    {
                        _effectiveDpi = DeviceDpi;
                    }

                    return _effectiveDpi;
                }

                return DeviceDpi;
            }
        }

        private int Dpi(int logical)
        {
            return (int) Math.Round(logical * EffectiveDpi / 96.0);
        }

        private int StripHeightPx
        {
            get { return Dpi(_stripHeight); }
        }

        private int TabHeightPx
        {
            get { return Dpi(Math.Min(_tabHeight, _stripHeight - 8)); }
        }

        /// <summary>
        /// Gap between the bottom of the floating tab pills and the strip edge. Firefox Nova
        /// centers the tabs (equal --tab-margin-block above and below), so this is half the
        /// room the strip leaves around them: 6px at the 44/32 default, 4px at 36/28 compact.
        /// </summary>
        private int TabBottomMarginPx
        {
            get { return (StripHeightPx - TabHeightPx) / 2; }
        }

        /// <summary>Firefox Nova: each tab carries 2px inline padding (--tab-margin-overflow-clip), so 4px between pills.</summary>
        private int TabGapPx
        {
            get { return Dpi(4); }
        }

        /// <summary>
        /// Matches the host app's title-bar corners rather than Firefox Nova's pills, so the
        /// strip shares the contours of the app below it.
        /// </summary>
        private int TabRadiusPx
        {
            get { return Dpi(6); }
        }

        /// <summary>
        /// Narrowest a tab may shrink to. Sized so the favicon still fits whole:
        /// Dpi(8) left padding + Dpi(16) icon + Dpi(12) trailing room.
        /// </summary>
        private int MinTabWidthPx
        {
            get { return Dpi(36); }
        }

        /// <summary>
        /// Narrowest a tab may be and still draw its close button alongside the favicon:
        /// Dpi(8) padding + Dpi(16) icon + Dpi(6) gap + Dpi(20) close + Dpi(4) trailing = 54, rounded up.
        /// Below this the two would overlap, so the close is dropped.
        /// </summary>
        private int MinTabWidthWithClosePx
        {
            get { return Dpi(56); }
        }

        private int CaptionButtonWidthPx
        {
            get { return Dpi(46); }
        }

        private int CaptionButtonHeightPx
        {
            get { return Dpi(32); }
        }

        private bool HasWindowIcon
        {
            get { return _showWindowIcon && Icon != null; }
        }

        private int WindowIconSizePx
        {
            get { return Dpi(16); }
        }

        /// <summary>Slot for the window icon, just before the start of the tabs.</summary>
        private Rectangle WindowIconRect()
        {
            int size = WindowIconSizePx;
            int tabTop = StripHeightPx - TabHeightPx - TabBottomMarginPx;
            return new Rectangle(Dpi(10), tabTop + (TabHeightPx - size) / 2, size, size);
        }

        private int TabListButtonSizePx
        {
            get { return Dpi(24); }
        }

        private int NewTabButtonSizePx
        {
            get { return Dpi(28); }
        }

        /// <summary>
        /// Narrowest the window may be dragged: everything the strip always shows, plus room for
        /// <see cref="MinVisibleTabs" /> tabs at their minimum width. Without this floor the strip
        /// squeezes until the tabs are unreachable slivers behind the chevrons and buttons.
        ///
        /// A split strip needs the furniture twice over — each region has its own buttons and its
        /// own tabs to keep visible — and the caption buttons are counted once for the window.
        /// </summary>
        private int MinClientWidthPx
        {
            get
            {
                // One of the visible tabs is the active one, which is held at the wider
                // icon-plus-× size; the rest sit at the plain floor.
                int tabsRoom = _minVisibleTabs > 0
                    ? (MinTabWidthWithClosePx + TabGapPx) + (_minVisibleTabs - 1) * (MinTabWidthPx + TabGapPx)
                    : 0;

                int perRegion = (_showTabListButton ? TabListButtonSizePx + Dpi(6) : 0)
                    + (ShowNewTabButton ? NewTabButtonSizePx + Dpi(8) : 0)
                    + tabsRoom
                    + 2 * ScrollChevronInsetPx
                    + Dpi(8);

                int regions = _splitStrip ? 2 : 1;
                int chrome = 3 * CaptionButtonWidthPx + Dpi(8)
                    + (HasWindowIcon ? WindowIconSizePx + Dpi(10) : 0);

                return regions * perRegion + chrome;
            }
        }

        /// <summary>X coordinate of the split divider (pixel pin when set, else <see cref="SplitRatio" />).</summary>
        private int DividerXPx
        {
            get
            {
                return _dividerOverrideLeft >= 0
                    ? _dividerOverrideLeft + _dividerOverrideWidth / 2
                    : (int) Math.Round(ClientSize.Width * _splitRatio);
            }
        }

        /// <summary>Divider grab area — wider than the painted line, like the Vue divider's hit zone.</summary>
        private Rectangle DividerHitRect
        {
            get { return new Rectangle(DividerXPx - Dpi(10), 0, Dpi(20), StripHeightPx); }
        }

        /// <summary>Inline-start edge of a strip region (icon slot only precedes region 0).</summary>
        private int RegionLeftPx(int group)
        {
            return group == 1
                ? DividerXPx + Dpi(8)
                : Dpi(8) + (HasWindowIcon ? WindowIconSizePx + Dpi(10) : 0);
        }

        /// <summary>Inline-end edge of a strip region (caption buttons bound the last region).</summary>
        private int RegionRightPx(int group)
        {
            return _splitStrip && group == 0
                ? DividerXPx - Dpi(8)
                : ClientSize.Width - (3 * CaptionButtonWidthPx + Dpi(8));
        }

        /// <summary>Slot for a region's tab-list dropdown button, before its tabs.</summary>
        private Rectangle TabListButtonRect(int group)
        {
            int size = TabListButtonSizePx;
            int tabTop = StripHeightPx - TabHeightPx - TabBottomMarginPx;
            return new Rectangle(RegionLeftPx(group), tabTop + (TabHeightPx - size) / 2, size, size);
        }

        private int GroupTabsLeftPx(int group)
        {
            return RegionLeftPx(group) + (_showTabListButton ? TabListButtonSizePx + Dpi(6) : 0);
        }

        private int GroupTabsRightPx(int group)
        {
            return RegionRightPx(group) - (ShowNewTabButton ? NewTabButtonSizePx + Dpi(8) : 0);
        }

        /// <summary>
        /// The band the tabs are actually painted and clipped to. It insets by a chevron
        /// on each side while the group overflows, so scrolled tabs slide underneath the
        /// buttons instead of colliding with them.
        ///
        /// This is deliberately NOT folded into GroupTabsLeft/RightPx: those define how
        /// much room the tabs have to share, and insetting them would change the computed
        /// tab width, which feeds back into whether the group overflows at all.
        /// </summary>
        private int ScrollChevronInsetPx
        {
            get { return ScrollChevronButtonWidthPx; }
        }

        /// <summary>
        /// Width of the chevron's opaque pill. The pill is the full width of the inset, so
        /// the clip band starts exactly where the pill ends: a tab scrolling out slides
        /// behind the pill and is swallowed by it, rather than being cut off in mid-air a
        /// few pixels away from a floating glyph.
        /// </summary>
        private int ScrollChevronButtonWidthPx
        {
            get { return Dpi(28); }
        }

        private Rectangle GroupTabsClipRect(int group)
        {
            int left = GroupTabsLeftPx(group);
            int right = GroupTabsRightPx(group);
            if (GroupOverflows(group))
            {
                left += ScrollChevronInsetPx;
                right -= ScrollChevronInsetPx;
            }
            int tabTop = StripHeightPx - TabHeightPx - TabBottomMarginPx;
            return new Rectangle(left, tabTop, Math.Max(0, right - left), TabHeightPx);
        }

        private int GroupTabCount(int group)
        {
            int count = 0;

            for (int i = 0; i < _tabs.Count; i++)
            {
                if (GroupOf(_tabs[i]) == group)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Position of the tab at <paramref name="index" /> within its own region.</summary>
        private int GroupPosition(int index)
        {
            int group = GroupOf(_tabs[index]);
            int position = 0;

            for (int i = 0; i < index; i++)
            {
                if (GroupOf(_tabs[i]) == group)
                {
                    position++;
                }
            }

            return position;
        }

        /// <summary>Global index of the region member at position <paramref name="position" />.</summary>
        private int GlobalIndexOfGroupPosition(int group, int position)
        {
            int seen = 0;

            for (int i = 0; i < _tabs.Count; i++)
            {
                if (GroupOf(_tabs[i]) == group)
                {
                    if (seen == position)
                    {
                        return i;
                    }

                    seen++;
                }
            }

            return _tabs.Count - 1;
        }

        private int TabWidthPx(int group)
        {
            int count = GroupTabCount(group);

            if (count == 0)
            {
                return Dpi(225);
            }

            int available = Math.Max(0, GroupTabsRightPx(group) - GroupTabsLeftPx(group));
            int width = (available - (count - 1) * TabGapPx) / count;

            // Shrink tabs to share the available width evenly, down to a floor that keeps
            // the favicon whole: it is drawn Dpi(8) from the left edge at Dpi(16) square,
            // so a narrower tab clips the icon against the rounded right edge and leaves
            // an unreadable sliver. Dpi(36) = 8 + 16 + 12 of trailing room (the pill's
            // round end eats into it).
            //
            // Past the floor the tabs stop shrinking and the strip SCROLLS instead (see
            // ScrollOffsetPx and the edge chevrons) — squeezing further would just make
            // every tab equally unidentifiable.
            return Math.Max(MinTabWidthPx, Math.Min(Dpi(225), width));
        }

        /// <summary>
        /// Width of one tab: the group's shared width, except that the active tab is widened
        /// to <see cref="MinTabWidthWithClosePx" /> when the shared width is too narrow to
        /// fit its favicon and its × side by side.
        ///
        /// With many tabs open every tab sits at <see cref="MinTabWidthPx" />, where the two
        /// would overlap. Rather than drop either one, the active tab gets the few extra
        /// pixels it needs — it is the one tab whose icon AND close button both have to stay
        /// reachable, and widening only it costs the strip a single tab's worth of room.
        /// </summary>
        private int TabWidthPx(int index, int group)
        {
            int width = TabWidthPx(group);

            return IsActiveTab(index) && _tabs[index].CanClose && width < MinTabWidthWithClosePx
                ? MinTabWidthWithClosePx
                : width;
        }

        /// <summary>
        /// Keeps the visible tabs still when the widened active tab changes. Only a width
        /// change BEFORE the scroll viewport moves what the user is looking at, so the
        /// offset is shifted by exactly that much — a change at or after the viewport
        /// simply re-lays out to the right of it and needs no correction.
        /// </summary>
        private void AbsorbActiveWidthShift(int previous, int current)
        {
            if (previous < 0 || previous >= _tabs.Count || current < 0 || current >= _tabs.Count)
            {
                return;
            }

            int group = GroupOf(_tabs[current]);

            if (GroupOf(_tabs[previous]) != group || !GroupOverflows(group))
            {
                return;
            }

            // Measured with _selectedIndex already moved, so TabOffsetPx reports the NEW
            // layout; comparing the two tabs' offsets against the offset they had before
            // would need the old layout, so the delta is derived from the widening instead.
            int bonus = MinTabWidthWithClosePx - TabWidthPx(group);

            if (bonus <= 0)
            {
                return;
            }

            int offset = ScrollOffsetPx(group);
            int shift = 0;

            // The previously active tab shrank: content before the viewport lost its bonus.
            if (_tabs[previous].CanClose && TabOffsetPx(previous, group) < offset)
            {
                shift -= bonus;
            }

            // The newly active tab grew: content before the viewport gained one.
            if (_tabs[current].CanClose && TabOffsetPx(current, group) < offset)
            {
                shift += bonus;
            }

            if (shift != 0)
            {
                SetScrollOffsetPx(group, offset + shift);
            }
        }

        /// <summary>
        /// Group position a tab dropped at <paramref name="xPx" /> should take: the first
        /// tab whose midpoint the point has passed. Walked rather than divided by a slot
        /// size, because the active tab is wider than the rest, so a uniform division
        /// drifts by that difference for every position after it.
        /// </summary>
        /// <summary>Where a tab dropped at <paramref name="screenPoint" /> would land among this strip's tabs.</summary>
        public int DropIndexAtScreenPoint(Point screenPoint)
        {
            return DropIndexAtPx(0, PointToClient(screenPoint).X);
        }

        private int DropIndexAtPx(int group, int xPx)
        {
            int left = GroupTabsClipRect(group).Left - ScrollOffsetPx(group);
            int position = 0;
            int cursor = left;

            for (int i = 0; i < _tabs.Count; i++)
            {
                if (GroupOf(_tabs[i]) != group)
                {
                    continue;
                }

                int width = TabWidthPx(i, group);

                if (xPx < cursor + width / 2)
                {
                    return position;
                }

                cursor += width + TabGapPx;
                position++;
            }

            return position;
        }

        /// <summary>The tab the strip is showing as current — selected, or a split region's own.</summary>
        private bool IsActiveTab(int index)
        {
            return index == _selectedIndex || _tabs[index].Highlighted;
        }

        /// <summary>
        /// Distance from the group's first tab to the left edge of <paramref name="index" />.
        /// Summed rather than multiplied because the active tab may be wider than the rest.
        /// </summary>
        private int TabOffsetPx(int index, int group)
        {
            int position = GroupPosition(index);
            int offset = 0;
            int seen = 0;

            for (int i = 0; i < _tabs.Count && seen < position; i++)
            {
                if (GroupOf(_tabs[i]) != group)
                {
                    continue;
                }

                offset += TabWidthPx(i, group) + TabGapPx;
                seen++;
            }

            return offset;
        }

        // ── Overflow scrolling ────────────────────────────────────────────────────
        // Once tabs hit MinTabWidthPx they stop shrinking, so a group's tabs can be
        // wider than its region. Rather than let them run under the caption buttons,
        // the group scrolls: one horizontal offset shifts TabRect, and because every
        // consumer (hit-testing, dragging, tooltips, painting) goes through TabRect,
        // they all follow without knowing scrolling exists.

        private readonly Dictionary<int, int> _scrollOffsetByGroup = new Dictionary<int, int>();

        /// <summary>Total width the group's tabs want, ignoring the region's limit.</summary>
        private int GroupContentWidthPx(int group)
        {
            int count = GroupTabCount(group);
            if (count == 0)
            {
                return 0;
            }

            // Summed per tab: the active one may be wider than the shared width.
            int total = 0;

            for (int i = 0; i < _tabs.Count; i++)
            {
                if (GroupOf(_tabs[i]) == group)
                {
                    total += TabWidthPx(i, group) + TabGapPx;
                }
            }

            return total - TabGapPx;
        }

        /// <summary>How far the group can scroll before its last tab is flush right; 0 when it fits.</summary>
        private int MaxScrollOffsetPx(int group)
        {
            // Measured against the band the tabs actually occupy once the chevrons have
            // taken their inset — using the full region would stop short of the last tab.
            int full = Math.Max(0, GroupTabsRightPx(group) - GroupTabsLeftPx(group));
            int content = GroupContentWidthPx(group);
            if (content <= full)
            {
                return 0;
            }
            int banded = Math.Max(0, full - 2 * ScrollChevronInsetPx);
            return Math.Max(0, content - banded);
        }

        /// <summary>True when the group has more tabs than fit, so the chevrons are shown.</summary>
        private bool GroupOverflows(int group)
        {
            return MaxScrollOffsetPx(group) > 0;
        }

        private int ScrollOffsetPx(int group)
        {
            int offset;
            if (!_scrollOffsetByGroup.TryGetValue(group, out offset))
            {
                return 0;
            }
            // Clamp on read: the max shifts as tabs open, close, or the window resizes,
            // and a stale offset would leave a gap at the right edge.
            return Math.Max(0, Math.Min(offset, MaxScrollOffsetPx(group)));
        }

        private void SetScrollOffsetPx(int group, int value)
        {
            // Declined while the strip has no width (minimized, or before the handle is
            // first sized): the clamp below measures live geometry, so such a write would
            // collapse to 0 and persist, discarding where the user had scrolled to.
            if (GroupTabsClipRect(group).Width <= 0)
            {
                return;
            }

            int clamped = Math.Max(0, Math.Min(value, MaxScrollOffsetPx(group)));
            int current = ScrollOffsetPx(group);

            if (current == clamped)
            {
                return;
            }

            // Deliberately NOT glided here. Scrolls that happen as part of another change
            // — a close pulling the active tab back into view, a resize re-clamping — are
            // already accounted for by that change's own animation, and gliding them too
            // would move the tabs twice for one event. Only the scroll gestures below
            // (ScrollGroupByTab) glide, because there the scroll IS the event.
            _pendingScrollGlidePx = current - clamped;
            _scrollOffsetByGroup[group] = clamped;
            InvalidateStrip();
        }

        /// <summary>Scrolls a group by one tab's worth, used by the edge chevrons.</summary>
        /// <summary>
        /// Scrolls a group by one tab's worth, used by the edge chevrons and the wheel. This
        /// is the gesture where the scroll itself is what the user asked for, so it is the
        /// one that glides.
        /// </summary>
        private void ScrollGroupByTab(int group, int direction)
        {
            _pendingScrollGlidePx = 0;

            SetScrollOffsetPx(group, ScrollOffsetPx(group) + direction * (TabWidthPx(group) + TabGapPx));

            BeginScrollGlide(group, _pendingScrollGlidePx);
            _pendingScrollGlidePx = 0;
        }

        /// <summary>
        /// How far the last <see cref="SetScrollOffsetPx" /> moved the band, for the caller to
        /// glide if that movement was a scroll gesture in its own right.
        /// </summary>
        private int _pendingScrollGlidePx;

        /// <summary>Brings a tab fully into view — called when the selection changes.</summary>
        private void EnsureTabVisible(int index)
        {
            if (index < 0 || index >= _tabs.Count)
            {
                return;
            }

            int group = GroupOf(_tabs[index]);
            if (!GroupOverflows(group))
            {
                return;
            }

            int width = TabWidthPx(index, group);
            int tabLeft = TabOffsetPx(index, group);
            int offset = ScrollOffsetPx(group);

            // The band the tabs actually occupy, not the whole region: while the group
            // overflows, a chevron covers each end. Measuring against the region would
            // scroll the tab flush to that edge, parking it underneath the chevron —
            // technically "visible" by the arithmetic, hidden on screen. MaxScrollOffsetPx
            // already measures the same inset band.
            int visible = GroupTabsClipRect(group).Width;

            if (tabLeft < offset)
            {
                SetScrollOffsetPx(group, tabLeft);
            }
            else if (tabLeft + width > offset + visible)
            {
                SetScrollOffsetPx(group, tabLeft + width - visible);
            }
        }

        // The strip's tab area (client coordinates): where its tabs sit and the empty space after them,
        // not its buttons; down to half a strip below it (a window in hand is hard to aim).
        private bool TabsRowContains(Point local)
        {
            if (local.Y < 0 || local.Y >= StripHeightPx * 3 / 2) return false;
            return local.X >= GroupTabsLeftPx(0) && local.X < GroupTabsRightPx(0);
        }

        private Rectangle TabRect(int index)
        {
            int group = GroupOf(_tabs[index]);
            int width = TabWidthPx(index, group);

            // Tabs start at the clip band's left edge, which is inset by a chevron while
            // the group overflows — otherwise the first tab would sit underneath the
            // leading chevron and the last would run past the trailing one.
            return new Rectangle(
                GroupTabsClipRect(group).Left + TabOffsetPx(index, group) - ScrollOffsetPx(group),
                StripHeightPx - TabHeightPx - TabBottomMarginPx,
                width,
                TabHeightPx);
        }

        /// <summary>
        /// The left/right chevron buttons, drawn at the edges of the tab band while the
        /// group overflows. Tab-card height and full inset width, so each reads as an
        /// opaque wall the tabs pass behind — and so the whole gap is clickable, not just
        /// the glyph.
        ///
        /// Both are pinned to the band that GroupTabsClipRect insets, NOT to the region
        /// edge: an overflowing group's tabs run the full width of that band, so a chevron
        /// placed outside it would leave tabs spilling past the button on that side.
        /// </summary>
        private Rectangle ScrollChevronRect(int group, bool leading)
        {
            int width = ScrollChevronButtonWidthPx;
            int tabTop = StripHeightPx - TabHeightPx - TabBottomMarginPx;
            return leading
                ? new Rectangle(GroupTabsLeftPx(group), tabTop, width, TabHeightPx)
                : new Rectangle(GroupTabsRightPx(group) - width, tabTop, width, TabHeightPx);
        }

        private Rectangle TabCloseRect(Rectangle tabRect)
        {
            // Firefox Nova: a 20px button around a 12px glyph, pulled half the 8px tab
            // padding toward the edge (margin-inline-end: calc(var(--tab-padding-inline) / -2)).
            int size = Dpi(20);
            return new Rectangle(tabRect.Right - size - Dpi(4), tabRect.Top + (tabRect.Height - size) / 2, size, size);
        }

        private bool TabShowsClose(int index)
        {
            if (!_tabs[index].CanClose)
            {
                return false;
            }

            // The active tab always shows its ×: TabWidthPx widens it far enough that the
            // × and the favicon both fit, so neither has to give way. That guarantees at
            // least one close button on the strip no matter how many tabs are open —
            // without it, everything sits at MinTabWidthPx with no × anywhere, leaving only
            // Ctrl+W and middle-click, which a user reaching for an × will not find.
            if (IsActiveTab(index))
            {
                return true;
            }

            // An inactive tab keeps its icon instead: the two would overlap at this width,
            // and the icon is what identifies a tab the user has not selected yet.
            int width = TabWidthPx(index, GroupOf(_tabs[index]));

            if (_tabs[index].Icon != null && width < MinTabWidthWithClosePx)
            {
                return false;
            }

            return width >= Dpi(72);
        }

        /// <summary>
        /// Whether the tab draws its favicon. Always, when it has one: the active tab is
        /// widened to fit the icon alongside its ×, and inactive tabs drop the × instead
        /// (see <see cref="TabShowsClose" />), so the two never have to compete.
        /// </summary>
        private bool TabShowsIcon(int index)
        {
            return _tabs[index].Icon != null;
        }

        private Rectangle NewTabButtonRect(int group)
        {
            int count = GroupTabCount(group);
            int last = count > 0
                ? GroupTabsLeftPx(group) + GroupContentWidthPx(group)
                : GroupTabsLeftPx(group);

            // The "+" trails the last tab, but an overflowing group's tabs extend well past
            // the visible band, so the untethered position would put it off-screen behind
            // the caption buttons. GroupTabsRightPx already reserves its slot at the region
            // edge for exactly this case — clamp to it so the button stops there and the
            // trailing chevron stays the rightmost thing in the band.
            int size = NewTabButtonSizePx;
            int clamped = Math.Min(last + Dpi(8), GroupTabsRightPx(group) + Dpi(8));
            int tabTop = StripHeightPx - TabHeightPx - TabBottomMarginPx;
            return new Rectangle(clamped, tabTop + (TabHeightPx - size) / 2, size, size);
        }

        /// <summary>True when the point hits any region's "+", tab-list toggle or scroll chevron.</summary>
        private bool HitsStripButton(Point p)
        {
            for (int group = 0; group < GroupCount; group++)
            {
                if (ShowNewTabButton && NewTabButtonRect(group).Contains(p))
                {
                    return true;
                }

                if (ShowTabListButton && TabListButtonRect(group).Contains(p))
                {
                    return true;
                }

                // Without this the chevrons hit-test as HTCAPTION, so the OS swallows the
                // mouse into a window drag and WinForms never sees the move or the click —
                // no hover highlight, and pressing one drags the window instead of scrolling.
                if (GroupOverflows(group)
                    && (ScrollChevronRect(group, true).Contains(p) || ScrollChevronRect(group, false).Contains(p)))
                {
                    return true;
                }
            }

            return false;
        }

        private Rectangle CaptionButtonRect(int htCode)
        {
            int slotFromRight = htCode == NativeMethods.HTCLOSE ? 1 : htCode == NativeMethods.HTMAXBUTTON ? 2 : 3;
            return new Rectangle(ClientSize.Width - slotFromRight * CaptionButtonWidthPx, 0, CaptionButtonWidthPx, CaptionButtonHeightPx);
        }

        /// <summary>Left edge of the min/max/close cluster — the right bound for all strip content.</summary>
        private int CaptionButtonsLeftPx
        {
            get { return ClientSize.Width - 3 * CaptionButtonWidthPx; }
        }

        /// <summary>Right bound for painted tab text — kept clear of the caption buttons, since
        /// TextRenderer.DrawText ignores the Graphics clip region.</summary>
        private int StripContentRightPx
        {
            get { return CaptionButtonsLeftPx - Dpi(2); }
        }

        private int HitTab(Point p)
        {
            if (p.Y >= StripHeightPx)
            {
                return -1;
            }

            for (int i = 0; i < _tabs.Count; i++)
            {
                // Match what is painted: a scrolled tab is clipped to its group's band,
                // so the part hidden under a chevron must not be clickable either.
                if (TabRect(i).Contains(p) && GroupTabsClipRect(GroupOf(_tabs[i])).Contains(p))
                {
                    return i;
                }
            }

            return -1;
        }

        private void UpdateMetrics()
        {
            Padding = _stripVisible ? new Padding(0, StripHeightPx, 0, 0) : Padding.Empty;
        }

        #endregion

        #region Lifecycle

        /// <summary>Number of <see cref="FluentChromeTabsForm" /> windows currently open in the process.</summary>
        public static int OpenWindowCount
        {
            get { return OpenTabForms.Count; }
        }

        /// <summary>How many strip heights below the strip still count as dropping onto it.</summary>
        public const int StripDropSlackFactor = 2;

        /// <summary>
        /// Marks the strip as a drop target: something dragged over it (another window) will
        /// land here as a tab when released. The host turns it on and off as the drag moves.
        /// </summary>
        public bool DropHintVisible
        {
            get { return _dropHintVisible; }
            set
            {
                if (_dropHintVisible == value) return;
                _dropHintVisible = value;
                InvalidateStrip();
            }
        }

        private bool _dropHintVisible;

        /// <summary>
        /// The window whose tab strip is visible at <paramref name="screenPoint" />, looking
        /// past <paramref name="ignore" /> (the window being dragged, which is itself under the
        /// cursor): the topmost window there, so a strip covered by another window is not
        /// found. Null when there is none. For a host that lets windows be dropped onto a strip.
        /// The target reaches <see cref="StripDropSlackFactor" /> strip heights below the strip:
        /// the strip alone is a thin line to hit with a whole window in hand, and on a maximized
        /// window it touches the screen's top edge, where Windows starts its own Snap.
        /// </summary>
        public static FluentChromeTabsForm FormWithStripAt(Point screenPoint, IntPtr ignore)
            => FormWithStripAt(screenPoint, ignore, tabsOnly: false);

        /// <summary>
        /// Like <see cref="FormWithStripAt(Point, IntPtr)" />, but only over the strip's tab area,
        /// with little slack below it and never its caption buttons. For a whole window
        /// moved by its title bar, which passes other strips on its way and must not be caught.
        /// </summary>
        public static FluentChromeTabsForm FormWithTabsAt(Point screenPoint, IntPtr ignore)
            => FormWithStripAt(screenPoint, ignore, tabsOnly: true);

        private static FluentChromeTabsForm FormWithStripAt(Point screenPoint, IntPtr ignore, bool tabsOnly)
        {
            for (IntPtr h = NativeMethods.GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = NativeMethods.GetWindow(h, NativeMethods.GW_HWNDNEXT))
            {
                if (h == ignore || !NativeMethods.IsWindowVisible(h)) continue;
                // A window the mouse passes through (a host's drop preview, an overlay) hides nothing.
                if ((NativeMethods.GetWindowLongInt(h, NativeMethods.GWL_EXSTYLE_INDEX) & NativeMethods.WS_EX_TRANSPARENT) != 0) continue;
                NativeMethods.RECT r;
                if (!NativeMethods.GetWindowRect(h, out r)) continue;
                if (screenPoint.X < r.left || screenPoint.X >= r.right || screenPoint.Y < r.top || screenPoint.Y >= r.bottom) continue;

                // The topmost window at the point: ours with its strip there, or nothing.
                foreach (FluentChromeTabsForm form in OpenTabForms)
                {
                    if (form.IsDisposed || !form.IsHandleCreated || form.Handle != h || form.WindowState == FormWindowState.Minimized) continue;
                    Point local = form.PointToClient(screenPoint);
                    if (tabsOnly) return form.TabsRowContains(local) ? form : null;
                    int reach = form.StripHeightPx * (1 + StripDropSlackFactor);
                    return local.Y >= 0 && local.Y < reach && local.X >= 0 && local.X < form.ClientSize.Width ? form : null;
                }
                return null;
            }

            return null;
        }

        /// <summary>Raised after any <see cref="FluentChromeTabsForm" /> in the process closes. Used by <see cref="FluentChromeTabsApplicationContext" />.</summary>
        public static event EventHandler WindowClosed;

        /// <summary>Client width the strip last laid out for, so a resize that did not change
        /// the width (restoring from minimized, a height-only drag) is ignored.</summary>
        private int _lastStripLayoutWidth = -1;

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (ScreenDockingAttached) ScreenDocking.OnDockedWindowResized(this);

            // Narrowing the window shrinks the tabs and shortens the scrollable band, which
            // can leave the active tab parked outside it — the user drags the edge in and
            // the tab they are working in slides away behind a chevron. Pull it back.
            //
            // Every active tab, not just the selected one: a split strip shows one per
            // region, and the unselected region's tab drifts out of view just the same.
            //
            // Skipped whenever the size on show is not one the user chose:
            //  - minimizing raises OnResize with a 0x0 client area, where the visible band
            //    measures 0, every tab reads as out of view, and the offset is rewritten to
            //    0 — silently discarding where the user had scrolled to;
            //  - restoring passes through a stray intermediate WM_SIZE before the bounds
            //    correction in WndProc puts the window back (see _restoreFixPending), and
            //    acting on that transient geometry rewrites the offset just the same.
            if (!StripVisible
                || WindowState == FormWindowState.Minimized
                || ClientSize.Width <= 0
                || _restoreFixPending)
            {
                return;
            }

            // Only a WIDTH change can push a tab out of the band. Restoring from minimized
            // replays the old width through a transient layout, and reacting to that would
            // re-scroll the strip to wherever the active tab happens to be — discarding the
            // position the user had scrolled to for a size they never actually changed.
            if (ClientSize.Width == _lastStripLayoutWidth)
            {
                return;
            }

            _lastStripLayoutWidth = ClientSize.Width;

            for (int i = 0; i < _tabs.Count; i++)
            {
                if (IsActiveTab(i))
                {
                    EnsureTabVisible(i);
                }
            }
        }

        /// <summary>
        /// The narrowest this window can be made (device pixels, window coordinates): what the
        /// strip needs, or the system's minimum. For a host placing windows side by side.
        /// </summary>
        public int MinimumWindowWidthPx
        {
            get
            {
                int system = SystemInformation.MinimumWindowSize.Width;
                if (!StripVisible || _minVisibleTabs <= 0 || !IsHandleCreated || ClientSize.Width <= 0) return system;
                return Math.Max(system, MinClientWidthPx + Math.Max(0, Width - ClientSize.Width));
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // Re-resolve the monitor DPI for the new handle before any metrics are computed
            _effectiveDpi = 0;

            ApplyDwmDarkMode();

            // A 1px top extension keeps DWM's subtle frame line above the strip
            NativeMethods.MARGINS margins = new NativeMethods.MARGINS { top = 1 };
            NativeMethods.DwmExtendFrameIntoClientArea(Handle, ref margins);

            // Re-run WM_NCCALCSIZE now that the caption is being reclaimed
            NativeMethods.SetWindowPos(
                Handle, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);

            UpdateMetrics();

            if (!OpenTabForms.Contains(this))
            {
                OpenTabForms.Add(this);
            }

            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

            // Close() does not always dispose straight away (a tear-off defers it through
            // BeginInvoke), so the timers would keep ticking against a torn-down form.
            StopReflowAnimation();
            StopScrollGlide();
            // A tooltip waiting for its delay must not show for a window that is gone.
            _toolTipDelay?.Stop();

            base.OnHandleDestroyed(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            OpenTabForms.Remove(this);
            base.OnFormClosed(e);

            EventHandler handler = WindowClosed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);

            _effectiveDpi = 0;
            UpdateMetrics();
            Invalidate();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            _windowActive = true;
            InvalidateStrip();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            _windowActive = false;
            InvalidateStrip();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _toolTip?.Dispose();
                _windowIconBitmap?.Dispose();
                _reflowTimer?.Dispose();
                _scrollGlideTimer?.Dispose();
                _toolTipDelay?.Dispose();
            }

            base.Dispose(disposing);
        }

        #endregion

        #region Keyboard

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.T))
            {
                RequestNewTab();
                return true;
            }

            if (keyData == (Keys.Control | Keys.W) || keyData == (Keys.Control | Keys.F4))
            {
                if (SelectedTab != null && SelectedTab.CanClose)
                {
                    CloseTab(SelectedTab);
                }

                return true;
            }

            // Ctrl+Tab opens the tab list as a hold-to-scroll switcher rather than hopping
            // one tab per press. ProcessCmdKey runs before the focused child control sees
            // the key, so this is also the path taken when a hosted control (e.g. a
            // WebView) has focus and would otherwise handle Ctrl+Tab itself — routing both
            // here keeps the gesture identical wherever focus happens to be.
            if (keyData == (Keys.Control | Keys.Tab))
            {
                ShowTabListMenu(SelectedTabGroup, 1);
                return true;
            }

            if (keyData == (Keys.Control | Keys.Shift | Keys.Tab))
            {
                ShowTabListMenu(SelectedTabGroup, -1);
                return true;
            }

            if ((keyData & (Keys.Control | Keys.Alt | Keys.Shift)) == Keys.Control)
            {
                Keys key = keyData & Keys.KeyCode;

                if (key >= Keys.D1 && key <= Keys.D9)
                {
                    int number = key - Keys.D1;
                    SelectedIndex = key == Keys.D9 ? _tabs.Count - 1 : Math.Min(number, _tabs.Count - 1);
                    return true;
                }
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void CycleTab(int direction)
        {
            if (_tabs.Count > 1)
            {
                SelectedIndex = (_selectedIndex + direction + _tabs.Count) % _tabs.Count;
            }
        }

        #endregion
    }
}
