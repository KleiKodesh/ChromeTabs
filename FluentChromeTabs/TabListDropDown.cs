using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FluentChromeTabs
{
    /// <summary>One row in the tab-list dropdown.</summary>
    public class TabListItem
    {
        public TabListItem(string text, bool active, Action activate)
            : this(text, active, activate, null)
        {
        }

        public TabListItem(string text, bool active, Action activate, Func<bool> close)
        {
            Text = text ?? string.Empty;
            Active = active;
            Activate = activate;
            Close = close;
        }

        /// <summary>Display text.</summary>
        public string Text { get; }

        /// <summary>Whether this row represents the currently selected tab (gets the accent indicator).</summary>
        public bool Active { get; }

        /// <summary>Invoked when the row is clicked, right after the dropdown closes.</summary>
        public Action Activate { get; }

        /// <summary>
        /// Optional close action. When set the row draws a × on its inline-end edge, and
        /// clicking that × runs this instead of <see cref="Activate" />. Rows without one
        /// (a "recently closed" entry, say) draw no button.
        ///
        /// Returns whether the close actually happened: a TabClosing handler may cancel it
        /// (an unsaved document, say), and the row must stay in the list when it does.
        /// </summary>
        public Func<bool> Close { get; }
    }

    /// <summary>A headed group of rows in the tab-list dropdown.</summary>
    public class TabListSection
    {
        public TabListSection(string header)
        {
            Header = header ?? string.Empty;
        }

        public string Header { get; }

        public List<TabListItem> Items { get; } = new List<TabListItem>();
    }

    /// <summary>
    /// Raised right before the tab-list dropdown opens. The open-tabs section is pre-populated;
    /// handlers may append additional sections (e.g. recently closed documents).
    /// </summary>
    public class TabListOpeningEventArgs : EventArgs
    {
        public TabListOpeningEventArgs(List<TabListSection> sections, int group)
        {
            Sections = sections;
            Group = group;
        }

        public List<TabListSection> Sections { get; }

        /// <summary>Which split region's toggle opened the popup (always 0 when the strip is not split).</summary>
        public int Group { get; }
    }

    /// <summary>
    /// Raised when a tab is right-clicked, before its context menu opens. The built-in close
    /// commands are pre-populated; handlers may append sections or cancel the menu outright.
    /// </summary>
    public class TabContextMenuOpeningEventArgs : EventArgs
    {
        public TabContextMenuOpeningEventArgs(List<TabListSection> sections, FluentTab tab)
        {
            Sections = sections;
            Tab = tab;
        }

        public List<TabListSection> Sections { get; }

        /// <summary>The tab that was right-clicked.</summary>
        public FluentTab Tab { get; }

        /// <summary>Set to suppress the menu.</summary>
        public bool Cancel { get; set; }
    }

    /// <summary>
    /// Fluent-styled popup listing tabs in headed sections. Fully owner-drawn from the strip
    /// palette: hover/focus rows, an accent indicator pill on the active tab, section headers,
    /// truncation tooltips, and manual RTL layout (the popup itself is never mirrored —
    /// alignment is computed explicitly). Item rows match the strip's tab height so mouse and
    /// touch targets feel identical to the tabs themselves; taps work through Windows'
    /// touch-to-mouse promotion (activation on mouse-up, no hover dependency), and a
    /// vertical finger drag pans an overflowing list.
    ///
    /// Keyboard: Up/Down/Home/End move the focus row, Enter activates, Esc closes.
    /// Implemented as an owned borderless form so click/tap-outside reliably dismisses it
    /// (via Deactivate), which a ToolStripDropDown hosting a focusable control does not do.
    ///
    /// <para>
    /// Quick-switch mode (<see cref="BeginQuickSwitch" />) turns the popup into an Alt+Tab-style
    /// switcher: the caller opens it on Ctrl+Tab, each further Tab steps the focus row, and
    /// releasing Ctrl activates whatever is focused. Nothing is activated until release, so
    /// stepping past five tabs costs one activation instead of five.
    /// </para>
    /// </summary>
    internal sealed class TabListDropDown : Form
    {
        private const int RowKindHeader = 0;
        private const int RowKindItem = 1;
        private const int RowKindSeparator = 2;

        private const int CS_DROPSHADOW = 0x00020000;

        private struct Row
        {
            public int Kind;
            public string Text;
            public TabListItem Item;
        }

        private readonly FluentChromeTabsForm _ownerForm;
        private readonly List<Row> _rows = new List<Row>();
        private readonly Palette _palette;
        private readonly bool _rtl;
        private readonly float _scale;
        private ThemedToolTip _toolTip;
        private Font _headerFont;
        private int _hoverRow = -1;
        // Row whose × the pointer is over (-1 = none); the button only paints its
        // hover plate when actually aimed at, as on the tabs themselves.
        private int _hoverClose = -1;
        private int _focusRow = -1;
        private int _scrollY;
        private string _toolTipShownFor;

        // Touch panning: a finger press on an overflowing list arms a pan; once it moves
        // past the jitter threshold the list follows the finger and the release is not a tap.
        private bool _panEligible;
        private bool _panning;
        private int _panStartY;
        private int _panStartScroll;

        // Quick-switch (Ctrl held) state: while armed the popup activates nothing until
        // Ctrl comes back up. The opening keypress steps the highlight immediately, so a
        // press-and-release with no further Tab still switches by one, like Alt+Tab.
        private bool _quickSwitch;
        private Timer _ctrlWatchdog;

        public TabListDropDown(FluentChromeTabsForm owner, List<TabListSection> sections)
        {
            _ownerForm = owner;
            _palette = owner.CurrentPalette;
            _rtl = owner.RightToLeft == RightToLeft.Yes;
            _scale = owner.EffectiveDpi / 96f;

            SetStyle(
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw,
                true);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Font = owner.Font;
            _headerFont = new Font(owner.Font.FontFamily, owner.Font.Size * 0.85f);
            BackColor = _palette.TabActive;

            bool first = true;

            foreach (TabListSection section in sections)
            {
                if (section.Items.Count == 0)
                {
                    continue;
                }

                if (!first)
                {
                    _rows.Add(new Row { Kind = RowKindSeparator });
                }

                first = false;

                if (!string.IsNullOrEmpty(section.Header))
                {
                    _rows.Add(new Row { Kind = RowKindHeader, Text = section.Header });
                }

                foreach (TabListItem item in section.Items)
                {
                    _rows.Add(new Row { Kind = RowKindItem, Text = item.Text, Item = item });

                    if (item.Active && _focusRow < 0)
                    {
                        _focusRow = _rows.Count - 1;
                    }
                }
            }

            ClientSize = MeasureContent();

            if (_focusRow >= 0)
            {
                EnsureRowVisible(_focusRow);
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            // Touch presses must arrive immediately as promoted mouse messages so a finger
            // can pan the list live and taps land without the press-and-hold delay; nothing
            // in the popup uses the press-and-hold right-click gesture.
            if (m.Msg == NativeMethods.WM_TABLET_QUERYSYSTEMGESTURESTATUS)
            {
                m.Result = new IntPtr(NativeMethods.TABLET_DISABLE_PRESSANDHOLD | NativeMethods.TABLET_DISABLE_FLICKS);
                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // Windows 11 rounded popup corners; harmless no-op on Windows 10
            int preference = NativeMethods.DWMWCP_ROUNDSMALL;
            NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }

        // Click/tap-outside dismissal: interacting with anything else (including the owner
        // window) deactivates the popup, which closes it.
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);

            // Losing activation mid-switch means the user went somewhere else (Alt+Tab to
            // another app) rather than picking a tab, so cancel like Esc does. Committing
            // here would switch their tab behind their back while they were away — and the
            // Ctrl keyup that should have committed goes to the other app, never to us.
            _quickSwitch = false;
            Close();
        }

        // ── Quick switch (Ctrl+Tab) ─────────────────────────────────────────────────

        /// <summary>
        /// Arms Alt+Tab-style behaviour: the popup stays up while Ctrl is held, Tab /
        /// Shift+Tab step the focus row, and releasing Ctrl activates the focused row.
        /// <paramref name="step" /> applies the opening keypress's own move.
        /// </summary>
        public void BeginQuickSwitch(int step)
        {
            _quickSwitch = true;
            StepQuickSwitch(step);

            // A keyup can be missed outright — the user can Alt+Tab away mid-hold, or
            // release over another window — which would strand the popup on screen with
            // no way to dismiss it. Poll the real key state as a backstop. Stopped from
            // StopCtrlWatchdog on every close path, not just its own tick.
            _ctrlWatchdog = new Timer { Interval = 100 };
            _ctrlWatchdog.Tick += (s, e) =>
            {
                if (IsDisposed || !_quickSwitch)
                {
                    StopCtrlWatchdog();
                    return;
                }

                if (!IsControlDown)
                {
                    StopCtrlWatchdog();
                    CommitQuickSwitch();
                }
            };
            _ctrlWatchdog.Start();
        }

        private void StopCtrlWatchdog()
        {
            if (_ctrlWatchdog == null)
            {
                return;
            }

            _ctrlWatchdog.Stop();
            _ctrlWatchdog.Dispose();
            _ctrlWatchdog = null;
        }

        private static bool IsControlDown
        {
            get { return (NativeMethods.GetKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0; }
        }

        /// <summary>
        /// Steps the highlight from outside the popup — the path taken when the host forwards a
        /// repeat Ctrl+Tab that the popup did not receive itself (it may not hold focus yet, or
        /// the keystroke may have arrived through the host's own bridge).
        /// </summary>
        public void StepQuickSwitchExternal(int step)
        {
            if (!_quickSwitch)
            {
                return;
            }

            StepQuickSwitch(step);
        }

        /// <summary>Moves the highlight without activating anything.</summary>
        private void StepQuickSwitch(int step)
        {
            MoveFocus(step >= 0 ? 1 : -1);
        }

        /// <summary>Ctrl came up — activate the highlighted row and close.</summary>
        private void CommitQuickSwitch()
        {
            if (!_quickSwitch)
            {
                return;
            }

            // Cleared before activating: ActivateRow closes the popup, and the resulting
            // OnDeactivate must not re-enter this and activate a second time.
            _quickSwitch = false;

            if (IsFocusRowActivatable)
            {
                ActivateRow(_focusRow);
            }
            else
            {
                Close();
            }
        }

        // Ctrl is released while this popup holds focus, so its own KeyUp is the primary
        // signal; the watchdog above only covers the cases where it never arrives.
        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);

            if (_quickSwitch && e.KeyCode == Keys.ControlKey)
            {
                CommitQuickSwitch();
            }
        }

        // ── Keyboard navigation ─────────────────────────────────────────────────────

        protected override bool ProcessDialogKey(Keys keyData)
        {
            // While Ctrl is held, Tab / Shift+Tab keep stepping the highlight. WinForms
            // would otherwise treat Tab as focus traversal and never reach the switch.
            // The live key state is checked too, not just _quickSwitch: the watchdog only
            // notices a missed Ctrl keyup on its next tick, and a plain Tab pressed inside
            // that window must not step the highlight.
            if (_quickSwitch && IsControlDown && (keyData & Keys.KeyCode) == Keys.Tab)
            {
                StepQuickSwitch((keyData & Keys.Shift) != 0 ? -1 : 1);
                return true;
            }

            switch (keyData)
            {
                case Keys.Escape:
                    // Abandon the switch: close without activating the highlighted row.
                    _quickSwitch = false;
                    Close();
                    return true;

                case Keys.Down:
                    MoveFocus(1);
                    return true;

                case Keys.Up:
                    MoveFocus(-1);
                    return true;

                case Keys.Home:
                    FocusEdge(first: true);
                    return true;

                case Keys.End:
                    FocusEdge(first: false);
                    return true;

                case Keys.Enter:
                case Keys.Space:
                    if (IsFocusRowActivatable)
                    {
                        ActivateRow(_focusRow);
                    }

                    return true;
            }

            return base.ProcessDialogKey(keyData);
        }

        /// <summary>Moves the focus row to the next/previous item row, wrapping around.</summary>
        private void MoveFocus(int direction)
        {
            if (_rows.Count == 0)
            {
                return;
            }

            int start = _focusRow >= 0 ? _focusRow : (direction > 0 ? -1 : _rows.Count);

            for (int step = 1; step <= _rows.Count; step++)
            {
                int i = ((start + direction * step) % _rows.Count + _rows.Count) % _rows.Count;

                if (_rows[i].Kind == RowKindItem)
                {
                    SetFocusRow(i);
                    return;
                }
            }
        }

        private void FocusEdge(bool first)
        {
            for (int step = 0; step < _rows.Count; step++)
            {
                int i = first ? step : _rows.Count - 1 - step;

                if (_rows[i].Kind == RowKindItem)
                {
                    SetFocusRow(i);
                    return;
                }
            }
        }

        private void SetFocusRow(int index)
        {
            _focusRow = index;
            EnsureRowVisible(index);
            Invalidate();
        }

        // ── Layout ──────────────────────────────────────────────────────────────────

        private int S(int logical)
        {
            return (int) Math.Round(logical * _scale);
        }

        private int PadX
        {
            get { return S(6); }
        }

        private int PadY
        {
            get { return S(6); }
        }

        private int RowHeight(int kind)
        {
            // Item rows match the strip's tab height so the popup's touch targets
            // feel identical to the tabs themselves
            return kind == RowKindHeader ? S(24) : kind == RowKindSeparator ? S(9) : S(_ownerForm.TabHeight);
        }

        private int ContentHeight
        {
            get
            {
                int height = PadY * 2;

                foreach (Row row in _rows)
                {
                    height += RowHeight(row.Kind);
                }

                return height;
            }
        }

        private Size MeasureContent()
        {
            int width = S(230);

            foreach (Row row in _rows)
            {
                if (row.Text != null)
                {
                    Font font = row.Kind == RowKindHeader ? _headerFont : Font;
                    // Rows carrying a × need its slot on top of the usual padding, or adding
                    // the button would truncate titles that used to fit.
                    int extra = row.Item != null && row.Item.Close != null ? CloseSizePx + S(6) : 0;
                    width = Math.Max(width, TextRenderer.MeasureText(row.Text, font).Width + S(56) + extra);
                }
            }

            // Wide enough for breadcrumb-style list titles (title · path segments);
            // anything longer truncates with the ellipsis + hover tooltip.
            width = Math.Min(width, S(500));
            int height = Math.Min(ContentHeight, Screen.FromControl(_ownerForm).WorkingArea.Height * 3 / 4);

            return new Size(width, height);
        }

        private Rectangle RowRect(int index)
        {
            int y = PadY - _scrollY;

            for (int i = 0; i < index; i++)
            {
                y += RowHeight(_rows[i].Kind);
            }

            return new Rectangle(PadX, y, ClientSize.Width - PadX * 2, RowHeight(_rows[index].Kind));
        }

        private int HitRow(Point p)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Kind == RowKindItem && RowRect(i).Contains(p))
                {
                    return i;
                }
            }

            return -1;
        }

        // ── Scrolling ───────────────────────────────────────────────────────────────

        private int MaxScroll
        {
            get { return Math.Max(0, ContentHeight - ClientSize.Height); }
        }

        private void ScrollTo(int y)
        {
            int clamped = Math.Max(0, Math.Min(y, MaxScroll));

            if (clamped != _scrollY)
            {
                _scrollY = clamped;
                Invalidate();
            }
        }

        private void EnsureRowVisible(int index)
        {
            Rectangle rect = RowRect(index);

            if (rect.Top < PadY)
            {
                ScrollTo(_scrollY + rect.Top - PadY);
            }
            else if (rect.Bottom > ClientSize.Height - PadY)
            {
                ScrollTo(_scrollY + rect.Bottom - (ClientSize.Height - PadY));
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollTo(_scrollY - Math.Sign(e.Delta) * RowHeight(RowKindItem) * 2);
            OnMouseMove(e);
        }

        // ── Painting ────────────────────────────────────────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (SolidBrush bg = new SolidBrush(_palette.TabActive))
            {
                g.FillRectangle(bg, ClientRectangle);
            }

            TextFormatFlags flags =
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                (_rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left);

            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];
                Rectangle rect = RowRect(i);

                if (rect.Bottom < 0 || rect.Top > ClientSize.Height)
                {
                    continue;
                }

                if (row.Kind == RowKindSeparator)
                {
                    using (Pen pen = new Pen(_palette.Separator))
                    {
                        int y = rect.Top + rect.Height / 2;
                        g.DrawLine(pen, rect.Left + S(4), y, rect.Right - S(4), y);
                    }

                    continue;
                }

                if (row.Kind == RowKindHeader)
                {
                    Rectangle headerRect = InsetInline(rect, S(10), S(10));
                    TextRenderer.DrawText(g, row.Text, _headerFont, headerRect, _palette.TextInactive, flags);
                    continue;
                }

                bool hover = i == _hoverRow;
                bool focused = i == _focusRow;
                bool active = row.Item != null && row.Item.Active;

                if (hover || focused || active)
                {
                    double amount = hover || focused ? 0.08 : 0.05;

                    using (SolidBrush brush = new SolidBrush(Palette.Blend(_palette.TabActive, _palette.TextActive, amount)))
                    using (GraphicsPath path = RoundedRect(rect, S(4)))
                    {
                        g.FillPath(brush, path);
                    }
                }

                if (active)
                {
                    // Fluent selection indicator: a small rounded accent pill on the inline-start edge
                    Color accent = _ownerForm.AccentColor ?? _palette.TextActive;
                    int pillH = S(16);
                    int pillW = S(3);
                    int pillX = _rtl ? rect.Right - S(5) - pillW : rect.Left + S(5);
                    Rectangle pill = new Rectangle(pillX, rect.Top + (rect.Height - pillH) / 2, pillW, pillH);

                    using (SolidBrush brush = new SolidBrush(accent))
                    using (GraphicsPath path = RoundedRect(pill, pillW / 2 + 1))
                    {
                        g.FillPath(brush, path);
                    }
                }

                TextRenderer.DrawText(g, row.Text, Font, TextRectFor(i), _palette.TextActive, flags);

                if (RowShowsClose(i))
                {
                    DrawRowClose(g, i);
                }
            }

            // Hairline border so the popup reads as a surface over same-colored content
            using (Pen pen = new Pen(_palette.TabBorder))
            {
                g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            }
        }

        /// <summary>
        /// The row's ×. Drawn as vector strokes with the same geometry as the tab close
        /// button, so the two read as the same control in two places.
        /// </summary>
        private void DrawRowClose(Graphics g, int row)
        {
            Rectangle close = CloseRectFor(row);

            if (row == _hoverClose)
            {
                using (SolidBrush brush = new SolidBrush(Palette.Blend(_palette.TabActive, _palette.TextActive, 0.12)))
                using (GraphicsPath path = RoundedRect(close, S(4)))
                {
                    g.FillPath(brush, path);
                }
            }

            float cx = close.Left + close.Width / 2f;
            float cy = close.Top + close.Height / 2f;
            float arm = 3.5f * _scale;

            // Muted at rest, full strength under the pointer. At text weight a column of
            // these reads as a second list competing with the titles, when closing is the
            // secondary action here — picking a tab is why the list is open.
            Color glyph = row == _hoverClose
                ? _palette.TextActive
                : MutedGlyphColor(_palette.TextActive, _palette.TabActive);

            using (Pen pen = new Pen(glyph, Math.Max(1f, 1.1f * _scale)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, cx - arm, cy - arm, cx + arm, cy + arm);
                g.DrawLine(pen, cx - arm, cy + arm, cx + arm, cy - arm);
            }
        }

        /// <summary>
        /// A dimmed version of <paramref name="text" /> that stays legible on
        /// <paramref name="surface" />. Muting is backed off as far as it takes to clear a
        /// 3:1 contrast ratio, because a custom chrome color (see Palette.FromColor) can put
        /// the surface close enough to the text that ANY fixed blend disappears — a mid-grey
        /// chrome leaves the full-strength text itself at only ~3:1. Never returns anything
        /// weaker than <paramref name="text" />, so the glyph can only ever be as hard to see
        /// as the row's own title.
        /// </summary>
        private static Color MutedGlyphColor(Color text, Color surface)
        {
            const double MinContrast = 3.0;

            for (double amount = 0.35; amount > 0.001; amount -= 0.05)
            {
                Color candidate = Palette.Blend(text, surface, amount);

                if (Palette.ContrastRatio(candidate, surface) >= MinContrast)
                {
                    return candidate;
                }
            }

            return text;
        }

        private Rectangle TextRectFor(Rectangle rowRect)
        {
            return InsetInline(rowRect, S(16), S(12));
        }

        /// <summary>Text rect for a row, less the × slot when that row shows one.</summary>
        private Rectangle TextRectFor(int row)
        {
            Rectangle rect = TextRectFor(RowRect(row));

            return RowShowsClose(row)
                ? InsetInline(rect, 0, CloseSizePx + S(6))
                : rect;
        }

        private int CloseSizePx
        {
            get { return S(16); }
        }

        private bool RowShowsClose(int row)
        {
            return row >= 0
                && row < _rows.Count
                && _rows[row].Kind == RowKindItem
                && _rows[row].Item != null
                && _rows[row].Item.Close != null;
        }

        /// <summary>The × slot on a row's inline-end edge (visual left in RTL).</summary>
        private Rectangle CloseRectFor(int row)
        {
            Rectangle rect = RowRect(row);
            int size = CloseSizePx;
            int x = _rtl ? rect.Left + S(8) : rect.Right - size - S(8);

            return new Rectangle(x, rect.Top + (rect.Height - size) / 2, size, size);
        }

        /// <summary>
        /// Whether <see cref="_focusRow" /> still names an activatable row. The upper bound
        /// matters: closing a row prunes headers and separators too, so the focus index can
        /// end up past the end of a list that got shorter than expected.
        /// </summary>
        private bool IsFocusRowActivatable
        {
            get { return _focusRow >= 0 && _focusRow < _rows.Count && _rows[_focusRow].Kind == RowKindItem; }
        }

        /// <summary>Row whose × is under <paramref name="p" />, or -1.</summary>
        private int HitClose(Point p)
        {
            int row = HitRow(p);

            return RowShowsClose(row) && CloseRectFor(row).Contains(p) ? row : -1;
        }

        /// <summary>Insets a rectangle by start/end amounts along the reading direction.</summary>
        private Rectangle InsetInline(Rectangle rect, int start, int end)
        {
            return _rtl
                ? Rectangle.FromLTRB(rect.Left + end, rect.Top, rect.Right - start, rect.Bottom)
                : Rectangle.FromLTRB(rect.Left + start, rect.Top, rect.Right - end, rect.Bottom);
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, radius * 2, radius * 2, 180, 90);
            path.AddArc(r.Right - radius * 2, r.Top, radius * 2, radius * 2, 270, 90);
            path.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
            path.AddArc(r.Left, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ── Mouse / touch ───────────────────────────────────────────────────────────

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button == MouseButtons.Left)
            {
                // Mouse users keep the wheel; only a finger press arms panning, so a sloppy
                // mouse click can never scroll the list out from under its own release
                _panEligible = NativeMethods.IsPromotedTouchMessage() && MaxScroll > 0;
                _panStartY = e.Y;
                _panStartScroll = _scrollY;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_panEligible && (_panning || Math.Abs(e.Y - _panStartY) > S(8)))
            {
                if (!_panning)
                {
                    // The finger is panning, not aiming — hover highlight and tooltip come off
                    _panning = true;
                    _hoverRow = -1;
                    _hoverClose = -1;
                    UpdateToolTip(-1);
                    Invalidate();
                }

                ScrollTo(_panStartScroll - (e.Y - _panStartY));
                return;
            }

            int row = HitRow(e.Location);
            int close = HitClose(e.Location);

            if (row != _hoverRow || close != _hoverClose)
            {
                _hoverRow = row;
                _hoverClose = close;
                Cursor = row >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
                UpdateToolTip(row);
            }
        }

        /// <summary>Shows the full title as a tooltip when it is truncated in its row.</summary>
        private void UpdateToolTip(int row)
        {
            string tip = null;

            if (row >= 0)
            {
                string text = _rows[row].Text;

                // Measured against the close-aware rect: on a row with a × the text box is
                // narrower, so a title can be truncated there that would have fit without it.
                if (TextRenderer.MeasureText(text, Font).Width > TextRectFor(row).Width)
                {
                    tip = text;
                }
            }

            if (tip != _toolTipShownFor)
            {
                _toolTipShownFor = tip;

                if (tip != null)
                {
                    if (_toolTip == null || _toolTip.IsDisposed)
                    {
                        _toolTip = new ThemedToolTip(Font, _scale);
                    }

                    Rectangle textRect = TextRectFor(row);
                    Point start = PointToScreen(new Point(_rtl ? textRect.Right : textRect.Left, RowRect(row).Bottom + S(2)));
                    _toolTip.ShowText(tip, _palette, _rtl, start.X, start.Y);
                }
                else
                {
                    _toolTip?.HideTip();
                }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);

            if (_hoverRow != -1 || _hoverClose != -1)
            {
                _hoverRow = -1;
                _hoverClose = -1;
                Invalidate();
                UpdateToolTip(-1);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            // Only the left-button up ends the pan — a synthesized non-left up mid-pan
            // must not hand the eventual left-up back to row activation
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            bool panned = _panning;
            _panning = false;
            _panEligible = false;

            if (panned)
            {
                return;
            }

            int closeRow = HitClose(e.Location);

            if (closeRow >= 0)
            {
                CloseRow(closeRow);
                return;
            }

            int row = HitRow(e.Location);

            if (row >= 0)
            {
                ActivateRow(row);
            }
        }

        /// <summary>
        /// Runs a row's close action and drops the row, leaving the popup open — shedding
        /// several tabs is the reason to be in this list, and closing after each one would
        /// mean reopening the popup for every tab.
        /// </summary>
        private void CloseRow(int row)
        {
            Func<bool> close = _rows[row].Item.Close;

            // Run the close FIRST and drop the row only if it actually happened: a TabClosing
            // handler may cancel (an unsaved document), and a row removed ahead of that would
            // vanish from the list while its tab lived on.
            if (close != null && !close())
            {
                return;
            }

            // The host's TabClosed handler may have closed this popup, or rebuilt it; once
            // that has happened the row list below is no longer ours to touch.
            if (IsDisposed || Disposing || !Visible)
            {
                return;
            }

            // Re-find the row rather than trusting the index: the callback above ran arbitrary
            // host code, which may have closed further tabs and renumbered the list.
            if (row >= _rows.Count || _rows[row].Item == null || _rows[row].Item.Close != close)
            {
                return;
            }

            _rows.RemoveAt(row);
            DropOrphanedChrome();

            // Clamped AFTER the pruning above, which removes further rows: clamping first
            // leaves _focusRow past the end, and the Enter/Space handler indexes it unguarded.
            _hoverRow = -1;
            _hoverClose = -1;
            _focusRow = Math.Min(_focusRow, _rows.Count - 1);
            _toolTipShownFor = null;
            _toolTip?.HideTip();

            if (_rows.Count == 0)
            {
                Close();
                return;
            }

            // The list got shorter, so the popup shrinks with it and the scroll offset may
            // now point past the end.
            ClientSize = MeasureContent();
            ScrollTo(_scrollY);
            Invalidate();
        }

        /// <summary>
        /// Removes chrome left dangling by a closed row: a header whose section has emptied,
        /// and separators that no longer sit between two runs of real content.
        ///
        /// Headers are dropped FIRST — an emptied section's leftover header would otherwise
        /// still count as content and keep its separator alive.
        /// </summary>
        private void DropOrphanedChrome()
        {
            // A header owns the rows up to the next header or separator. Checking only the
            // immediately-following row would keep a header whose own section emptied, and it
            // would then silently relabel the NEXT section's items.
            for (int i = _rows.Count - 1; i >= 0; i--)
            {
                if (_rows[i].Kind != RowKindHeader)
                {
                    continue;
                }

                bool hasItems = false;

                for (int j = i + 1;
                     j < _rows.Count && _rows[j].Kind != RowKindHeader && _rows[j].Kind != RowKindSeparator;
                     j++)
                {
                    hasItems |= _rows[j].Kind == RowKindItem;
                }

                if (!hasItems)
                {
                    _rows.RemoveAt(i);
                }
            }

            // A separator is only earned between two runs of content. Testing the NEAREST
            // neighbours (rather than "any item anywhere on that side") is what stops two
            // adjacent separators from each seeing content past the other and both surviving
            // as one double-height gap.
            for (int i = _rows.Count - 1; i >= 0; i--)
            {
                if (_rows[i].Kind != RowKindSeparator)
                {
                    continue;
                }

                bool contentBefore = i > 0 && _rows[i - 1].Kind != RowKindSeparator;
                bool contentAfter = i + 1 < _rows.Count && _rows[i + 1].Kind != RowKindSeparator;

                if (!contentBefore || !contentAfter)
                {
                    _rows.RemoveAt(i);
                }
            }
        }

        private void ActivateRow(int row)
        {
            TabListItem item = _rows[row].Item;

            // Any activation ends a quick switch, whoever started it. Clicking a row while
            // Ctrl is still held would otherwise close the popup, and the resulting
            // OnDeactivate would commit the HIGHLIGHTED row on top of the CLICKED one —
            // two activations, landing on the wrong tab.
            _quickSwitch = false;

            Close();
            item.Activate?.Invoke();
        }

        // Every close path funnels through here — Ctrl release, Esc, a click on a row, a
        // click outside — so it is the one place the watchdog is guaranteed to be stopped.
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _quickSwitch = false;
            StopCtrlWatchdog();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopCtrlWatchdog();
                _toolTip?.Dispose();
                _headerFont?.Dispose();
                _headerFont = null;
            }

            base.Dispose(disposing);
        }
    }
}
