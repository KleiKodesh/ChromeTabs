using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FluentChromeTabs
{
    /// <summary>Painting of the tab strip: tabs, new-tab button, and caption buttons.</summary>
    public partial class FluentChromeTabsForm
    {
        private int _hoverTab = -1;
        private bool _hoverTabClose;
        private int _hoverNewTabGroup = -1;
        private int _hoverTabListGroup = -1;
        // Hovered scroll chevron encoded as group*2 (+1 for the trailing one); -1 = none.
        private int _hoverScrollChevron = -1;
        private bool _hoverDivider;
        // Identity of whatever the tooltip currently describes (a FluentTab, or a boxed
        // caption hit-code) so repeated same-target mouse moves don't re-show/flicker, while
        // moving to a different target — even one with an identical title — repositions it.
        private object _toolTipKey;

        /// <summary>Device-scale factor for glyph geometry (1.0 at 96 DPI).</summary>
        private float GlyphScale
        {
            get { return EffectiveDpi / 96f; }
        }

        /// <summary>
        /// Thin round-capped pen matching the stroke weight of Segoe Fluent Icons. Glyphs are drawn as
        /// vector paths rather than font glyphs so the Fluent look works on any Windows version.
        /// </summary>
        private Pen GlyphPen(Color color)
        {
            Pen pen = new Pen(color, Math.Max(1f, 1.1f * GlyphScale));
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            return pen;
        }

        /// <summary>
        /// Draws an image so it stays readable in a mirrored (WS_EX_LAYOUTRTL) window: GDI flips all
        /// output in RTL layout, so the image is pre-flipped locally to cancel that out.
        /// </summary>
        private void DrawUnmirroredImage(Graphics g, Image image, Rectangle rect)
        {
            // GDI+ defaults to Bilinear with a half-pixel origin offset, which makes an
            // icon look soft and slightly misaligned whenever its bitmap size does not
            // exactly match the destination — the normal case once DPI scaling is in play.
            InterpolationMode savedInterpolation = g.InterpolationMode;
            PixelOffsetMode savedPixelOffset = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            try
            {
                if (IsMirrored)
                {
                    GraphicsState state = g.Save();
                    g.TranslateTransform(rect.Left * 2 + rect.Width, 0);
                    g.ScaleTransform(-1f, 1f);
                    g.DrawImage(image, rect);
                    g.Restore(state);
                }
                else
                {
                    g.DrawImage(image, rect);
                }
            }
            finally
            {
                g.InterpolationMode = savedInterpolation;
                g.PixelOffsetMode = savedPixelOffset;
            }
        }

        private static GraphicsPath RoundedRectPathF(float x, float y, float w, float h, float r)
        {
            GraphicsPath path = new GraphicsPath();
            path.AddArc(x, y, r * 2, r * 2, 180, 90);
            path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
            path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (!StripVisible)
            {
                // Fullscreen/kiosk: no strip — any uncovered client area shows the content surface
                using (SolidBrush content = new SolidBrush(_palette.TabActive))
                {
                    g.FillRectangle(content, 0, 0, ClientSize.Width, ClientSize.Height);
                }

                return;
            }

            using (SolidBrush strip = new SolidBrush(_palette.Strip))
            {
                g.FillRectangle(strip, 0, 0, ClientSize.Width, StripHeightPx);
            }

            // Below the strip, paint the active-tab surface so content-less (loose mode) tabs and
            // any uncovered client area blend with the selected tab instead of showing strip color
            using (SolidBrush content = new SolidBrush(_palette.TabActive))
            {
                g.FillRectangle(content, 0, StripHeightPx, ClientSize.Width, Math.Max(0, ClientSize.Height - StripHeightPx));
            }

            // Keep all strip content (icon, tabs, +/list buttons, divider) left of the caption
            // buttons so nothing renders under min/max/close when the window is too narrow.
            // GDI-clip the background/glyph drawing here; TextRenderer.DrawText ignores this
            // clip, so tab titles are clamped separately in DrawTab (see StripContentRightPx).
            GraphicsState stripClip = g.Save();
            g.SetClip(new Rectangle(0, 0, Math.Max(0, CaptionButtonsLeftPx - Dpi(2)), StripHeightPx));

            if (HasWindowIcon)
            {
                Bitmap iconBitmap = GetWindowIconBitmap(WindowIconSizePx);

                if (iconBitmap != null)
                {
                    DrawUnmirroredImage(g, iconBitmap, WindowIconRect());
                }
            }

            // Tabs are clipped per group so a scrolled-out tab disappears cleanly under
            // the chevrons instead of overrunning them and the caption buttons.
            for (int group = 0; group < GroupCount; group++)
            {
                // Save/Restore rather than swapping g.Clip: a strip-wide clip is already in
                // effect (see stripClip above), and assigning a Region replaces it outright
                // instead of nesting — which drops that clip and lets later drawing leak
                // past the caption buttons.
                GraphicsState groupClip = g.Save();
                g.SetClip(GroupTabsClipRect(group), CombineMode.Intersect);

                int scrollGlide = ScrollGliding ? ScrollGlidePx(group) : 0;

                for (int i = 0; i < _tabs.Count; i++)
                {
                    if (GroupOf(_tabs[i]) != group)
                    {
                        continue;
                    }
                    if (!_dragging || i != _dragIndex)
                    {
                        // Offset only the drawing: TabRect already reports the settled
                        // position, so everything else treats the reflow as instant.
                        Rectangle rect = TabRect(i);

                        if (ReflowAnimating)
                        {
                            rect.X += ReflowOffsetPx(i);
                        }

                        // The whole band lags together while a scroll catches up, so the tabs
                        // keep their spacing and the group slides as one.
                        rect.X += scrollGlide;

                        DrawTab(g, i, rect);
                    }
                }

                // The dragged tab floats above its neighbors at the cursor position
                if (_dragging && _dragIndex >= 0 && GroupOf(_tabs[_dragIndex]) == group)
                {
                    Rectangle rect = TabRect(_dragIndex);
                    rect.X = _dragVisualX;
                    DrawTab(g, _dragIndex, rect);
                }

                g.Restore(groupClip);
            }

            for (int group = 0; group < GroupCount; group++)
            {
                if (GroupOverflows(group))
                {
                    DrawScrollChevrons(g, group);
                }

                if (ShowNewTabButton)
                {
                    DrawNewTabButton(g, group);
                }

                if (ShowTabListButton)
                {
                    DrawTabListButton(g, group);
                }
            }

            // Divider between the two split regions — a seamless extension of the content
            // split divider below it: same hairline thickness, border color, and 50%
            // accent highlight while hovered or dragged.
            if (SplitStrip)
            {
                Color color = _hoverDivider || _dividerDragging
                    ? Palette.Blend(_palette.Strip, AccentColor ?? _palette.TextActive, 0.5)
                    : SplitDividerColor ?? _palette.Separator;

                // Crisp axis-aligned bar — no antialias bleed. When the host pinned exact
                // pixels, paint precisely those so the bar continues the content divider.
                SmoothingMode saved = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.None;

                using (SolidBrush brush = new SolidBrush(color))
                {
                    // Unpinned fallback is a 1px hairline, matching what a host content
                    // divider draws. It used to be Dpi(4), which stopped matching when the
                    // hosts went thin - so an unpinned strip drew a bar four times the
                    // width of the divider it is supposed to continue.
                    int width = _dividerOverrideLeft >= 0 ? _dividerOverrideWidth : Dpi(1);
                    int left = _dividerOverrideLeft >= 0 ? _dividerOverrideLeft : DividerXPx - width / 2;
                    g.FillRectangle(brush, left, Dpi(6), width, StripHeightPx - Dpi(6));
                }

                g.SmoothingMode = saved;
            }

            // Lift the clip so the caption buttons paint in full.
            g.Restore(stripClip);

            DrawCaptionButton(g, NativeMethods.HTMINBUTTON);
            DrawCaptionButton(g, NativeMethods.HTMAXBUTTON);
            DrawCaptionButton(g, NativeMethods.HTCLOSE);
        }

        private Color StripTextColor(bool activeTab)
        {
            Color color = activeTab ? _palette.TextActive : _palette.TextInactive;
            return _windowActive ? color : Palette.Blend(color, _palette.Strip, 0.4);
        }

        private void DrawTab(Graphics g, int index, Rectangle rect)
        {
            // Highlighted marks a split region's own active tab (each region shows one)
            bool active = index == _selectedIndex || _tabs[index].Highlighted;
            bool hover = index == _hoverTab;
            FluentTab tab = _tabs[index];

            if (active || hover)
            {
                // The tab is a floating rounded card; the active one gets a subtle outline
                // in light themes only.
                using (GraphicsPath path = RoundedRectPath(rect, TabRadiusPx))
                {
                    using (SolidBrush brush = new SolidBrush(active ? _palette.TabActive : _palette.TabHover))
                    {
                        g.FillPath(brush, path);
                    }

                    if (active && !_isDark)
                    {
                        using (Pen pen = new Pen(_palette.TabBorder))
                        {
                            g.DrawPath(pen, path);
                        }
                    }
                }
            }

            // Firefox Nova: --tab-padding-inline 8px, 16px favicon, --tab-icon-margin-inline-end 5.5px
            int textLeft = rect.Left + Dpi(8);

            if (TabShowsIcon(index))
            {
                int iconSize = Dpi(16);
                Rectangle iconRect = new Rectangle(textLeft, rect.Top + (rect.Height - iconSize) / 2, iconSize, iconSize);
                DrawUnmirroredImage(g, tab.Icon, iconRect);
                textLeft += iconSize + Dpi(6);
            }

            bool showClose = TabShowsClose(index);
            int textRight = showClose ? TabCloseRect(rect).Left : rect.Right - Dpi(8);

            // TextRenderer.DrawText ignores the strip clip set in OnPaint, so the text box has
            // to be clamped by hand. Clamp to the group's tab band, not just to the caption
            // buttons: on an overflowing group the band stops short of them at the trailing
            // chevron, and a title allowed to run to StripContentRightPx would paint straight
            // over that chevron and the "+" slot while its own card was correctly clipped.
            Rectangle band = GroupTabsClipRect(GroupOf(tab));
            textRight = Math.Min(textRight, Math.Min(band.Right, StripContentRightPx));

            // Same on the leading side, for a tab scrolled halfway under the left chevron.
            textLeft = Math.Max(textLeft, band.Left);

            // No EndEllipsis: on narrow tabs the "…" just eats space the text could use —
            // hard-clip instead (the tooltip still surfaces the full title).
            TextFormatFlags textFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;

            if (RightToLeft == RightToLeft.Yes && !RightToLeftLayout)
            {
                // RTL reading order for Hebrew/Arabic titles. Only when the window is NOT
                // mirrored: a WS_EX_LAYOUTRTL (RightToLeftLayout) DC already defaults to RTL
                // reading, and adding RightToLeft on top toggles it BACK to LTR — which put
                // string-edge neutrals (a trailing "*") and Latin runs at the wrong visual
                // end while pure-Hebrew titles looked fine under either base.
                // (In a mirrored window "Left" still renders at the visual right.)
                textFlags |= TextFormatFlags.RightToLeft;
            }

            Rectangle textRect = new Rectangle(textLeft, rect.Top, Math.Max(0, textRight - textLeft), rect.Height);

            TextRenderer.DrawText(g, tab.Title, Font, textRect, StripTextColor(active), textFlags);

            if (showClose)
            {
                Rectangle close = TabCloseRect(rect);

                if (_hoverTabClose && index == _hoverTab && !_dragging)
                {
                    Color surface = active ? _palette.TabActive : hover ? _palette.TabHover : _palette.Strip;

                    using (SolidBrush brush = new SolidBrush(Palette.Blend(surface, _palette.TextActive, 0.07)))
                    using (GraphicsPath path = RoundedRectPath(close, Dpi(4)))
                    {
                        g.FillPath(brush, path);
                    }
                }

                // Firefox's close-12.svg: an X spanning (2,2)-(10,10) of a 12px box, stroke ~1.25px
                using (Pen pen = GlyphPen(StripTextColor(active)))
                {
                    pen.Width = Math.Max(1f, 1.25f * GlyphScale);
                    float ccx = close.Left + close.Width / 2f;
                    float ccy = close.Top + close.Height / 2f;
                    float arm = 4f * GlyphScale;
                    g.DrawLine(pen, ccx - arm, ccy - arm, ccx + arm, ccy + arm);
                    g.DrawLine(pen, ccx - arm, ccy + arm, ccx + arm, ccy - arm);
                }
            }
        }

        /// <summary>
        /// A pill rounded on its outward side and square on the side the tabs come from,
        /// so the tab band ends against a straight edge — the mouth of the tunnel the
        /// tabs scroll into. Drawn in visual terms; GDI mirrors it for RTL along with
        /// everything else in the strip.
        /// </summary>
        private static GraphicsPath TunnelMouthPath(Rectangle r, int radius, bool leading)
        {
            // Half-pixel inset for the same reason as RoundedRectPath — and so these pills
            // start and end on exactly the same rows as the tab cards beside them. The flat
            // side keeps the full extent: it is a butt joint against the tab band, not an
            // edge that needs to read as rounded.
            float left = leading ? r.Left + 0.5f : r.Left;
            float top = r.Top + 0.5f;
            float right = leading ? r.Right : r.Right - 0.5f;
            float bottom = r.Bottom - 0.5f;
            float d = radius * 2f;

            GraphicsPath path = new GraphicsPath();

            if (leading)
            {
                // Rounded on the left, flat on the right
                path.AddArc(left, top, d, d, 180, 90);
                path.AddLine(right, top, right, bottom);
                path.AddArc(left, bottom - d, d, d, 90, 90);
            }
            else
            {
                // Flat on the left, rounded on the right
                path.AddLine(left, bottom, left, top);
                path.AddArc(right - d, top, d, d, 270, 90);
                path.AddArc(right - d, bottom - d, d, d, 0, 90);
            }

            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// Rounded rectangle inset by half a pixel on every side, so a 1px DrawPath outline
        /// falls wholly INSIDE <paramref name="r" /> instead of straddling its edges. On the
        /// exclusive Right/Bottom edges that outer half would otherwise be cut by the strip
        /// clip (or by the tab band, which a full-width tab sits flush against), leaving the
        /// card looking shaved off along one side. Insetting symmetrically keeps the shape
        /// centered, so no edge lands a pixel off from the one opposite it.
        /// </summary>
        private static GraphicsPath RoundedRectPath(Rectangle r, int radius)
        {
            float left = r.Left + 0.5f;
            float top = r.Top + 0.5f;
            float right = r.Right - 0.5f;
            float bottom = r.Bottom - 0.5f;
            float d = radius * 2f;

            GraphicsPath path = new GraphicsPath();
            path.AddArc(left, top, d, d, 180, 90);
            path.AddArc(right - d, top, d, d, 270, 90);
            path.AddArc(right - d, bottom - d, d, d, 0, 90);
            path.AddArc(left, bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void DrawNewTabButton(Graphics g, int group)
        {
            Rectangle rect = NewTabButtonRect(group);

            if (_hoverNewTabGroup == group && !_dragging)
            {
                using (SolidBrush brush = new SolidBrush(Palette.Blend(_palette.Strip, _palette.TextActive, 0.1)))
                using (GraphicsPath path = RoundedRectPath(rect, Dpi(4)))
                {
                    g.FillPath(brush, path);
                }
            }

            using (Pen pen = GlyphPen(StripTextColor(false)))
            {
                float cx = rect.Left + rect.Width / 2f;
                float cy = rect.Top + rect.Height / 2f;
                float arm = 5f * GlyphScale;
                g.DrawLine(pen, cx - arm, cy, cx + arm, cy);
                g.DrawLine(pen, cx, cy - arm, cx, cy + arm);
            }
        }

        private void DrawTabListButton(Graphics g, int group)
        {
            Rectangle rect = TabListButtonRect(group);
            bool open = TabListOpenGroup == group;

            if ((_hoverTabListGroup == group && !_dragging) || open)
            {
                double amount = open ? 0.16 : 0.12;

                using (SolidBrush brush = new SolidBrush(Palette.Blend(_palette.Strip, _palette.TextActive, amount)))
                using (GraphicsPath path = RoundedRectPath(rect, Dpi(4)))
                {
                    g.FillPath(brush, path);
                }
            }

            // Fluent chevron-down: two strokes meeting at the bottom center
            using (Pen pen = GlyphPen(StripTextColor(false)))
            {
                float cx = rect.Left + rect.Width / 2f;
                float cy = rect.Top + rect.Height / 2f;
                float arm = 3.5f * GlyphScale;
                g.DrawLine(pen, cx - arm, cy - arm / 2f, cx, cy + arm / 2f);
                g.DrawLine(pen, cx, cy + arm / 2f, cx + arm, cy - arm / 2f);
            }
        }

        /// <summary>
        /// Left/right chevrons at the edges of an overflowing group, matching the
        /// tab-list button's styling. A chevron dims once its direction is exhausted,
        /// so the strip shows how much is left to scroll.
        /// </summary>
        private void DrawScrollChevrons(Graphics g, int group)
        {
            int offset = ScrollOffsetPx(group);
            int max = MaxScrollOffsetPx(group);
            DrawScrollChevron(g, group, true, offset > 0);
            DrawScrollChevron(g, group, false, offset < max);
        }

        private void DrawScrollChevron(Graphics g, int group, bool leading, bool enabled)
        {
            Rectangle rect = ScrollChevronRect(group, leading);
            bool hot = enabled && _hoverScrollChevron == (leading ? group * 2 : group * 2 + 1) && !_dragging;

            // Always filled with the strip color, not just when hot: this pill is what a
            // tab scrolling out of view disappears behind. A transparent button would let
            // the outgoing tab show through, which reads as a tab overlapping the button
            // rather than passing behind it. Squared off on the tab-facing side so there
            // is no rounded notch for a sliver of tab to peek through.
            Color fill = hot ? Palette.Blend(_palette.Strip, _palette.TextActive, 0.12) : _palette.Strip;

            using (SolidBrush brush = new SolidBrush(fill))
            using (GraphicsPath path = TunnelMouthPath(rect, Dpi(6), leading))
            {
                g.FillPath(brush, path);
            }

            Color color = enabled
                ? StripTextColor(false)
                : Palette.Blend(_palette.Strip, StripTextColor(false), 0.35);

            // Two strokes meeting at the leading edge — the tab-list chevron rotated.
            // Drawn in visual terms (left arrow points left on screen) so the mirrored
            // RTL layout does not need a special case: GDI flips it with everything else.
            using (Pen pen = GlyphPen(color))
            {
                float cx = rect.Left + rect.Width / 2f;
                float cy = rect.Top + rect.Height / 2f;
                float arm = 3.5f * GlyphScale;
                float tipX = leading ? cx - arm / 2f : cx + arm / 2f;
                float tailX = leading ? cx + arm / 2f : cx - arm / 2f;
                g.DrawLine(pen, tailX, cy - arm, tipX, cy);
                g.DrawLine(pen, tipX, cy, tailX, cy + arm);
            }
        }

        private void DrawCaptionButton(Graphics g, int htCode)
        {
            Rectangle rect = CaptionButtonRect(htCode);
            bool hot = _hotCaptionButton == htCode;
            bool pressed = _pressedCaptionButton == htCode;
            bool isClose = htCode == NativeMethods.HTCLOSE;

            Color background = _palette.Strip;

            if (hot || pressed)
            {
                background = isClose
                    ? (pressed ? Palette.Blend(_palette.CloseButtonHover, _palette.Strip, 0.25) : _palette.CloseButtonHover)
                    : (pressed ? _palette.ButtonPressed : _palette.ButtonHover);

                using (SolidBrush brush = new SolidBrush(background))
                {
                    g.FillRectangle(brush, rect);
                }
            }

            // Fluent-style glyphs (Segoe Fluent Icons look) drawn as vector paths: thin anti-aliased
            // strokes with rounded corners, so they render on any Windows version without icon fonts
            Color glyphColor = isClose && (hot || pressed) ? Color.White : StripTextColor(true);
            float s = GlyphScale;
            float cx = rect.Left + rect.Width / 2f;
            float cy = rect.Top + rect.Height / 2f;

            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (Pen pen = GlyphPen(glyphColor))
            {
                switch (htCode)
                {
                    case NativeMethods.HTMINBUTTON:
                        g.DrawLine(pen, cx - 5f * s, cy, cx + 5f * s, cy);
                        break;

                    case NativeMethods.HTMAXBUTTON:
                        if (NativeMethods.IsZoomed(Handle))
                        {
                            // ChromeRestore: front rounded square plus the visible top-right edge of the back sheet
                            float gx = cx - 5f * s;
                            float gy = cy - 5f * s;
                            float front = 8f * s;
                            float corner = 1.6f * s;

                            using (GraphicsPath back = new GraphicsPath())
                            {
                                back.AddLine(gx + 3.5f * s, gy, gx + 10f * s - corner, gy);
                                back.AddArc(gx + 10f * s - 2f * corner, gy, 2f * corner, 2f * corner, 270, 90);
                                back.AddLine(gx + 10f * s, gy + corner, gx + 10f * s, gy + 6.5f * s);
                                g.DrawPath(pen, back);
                            }

                            using (GraphicsPath frontPath = RoundedRectPathF(gx, gy + 2f * s, front, front, corner))
                            {
                                g.DrawPath(pen, frontPath);
                            }
                        }
                        else
                        {
                            // ChromeMaximize: rounded-corner square
                            using (GraphicsPath box = RoundedRectPathF(cx - 4.5f * s, cy - 4.5f * s, 9f * s, 9f * s, 1.8f * s))
                            {
                                g.DrawPath(pen, box);
                            }
                        }

                        break;

                    case NativeMethods.HTCLOSE:
                        g.DrawLine(pen, cx - 4.5f * s, cy - 4.5f * s, cx + 4.5f * s, cy + 4.5f * s);
                        g.DrawLine(pen, cx - 4.5f * s, cy + 4.5f * s, cx + 4.5f * s, cy - 4.5f * s);
                        break;
                }
            }
        }

        private void UpdateHoverState(Point p)
        {
            if (!StripVisible)
            {
                ClearHoverState();
                return;
            }

            int tab = HitTab(p);
            bool overClose = tab >= 0 && TabShowsClose(tab) && TabCloseRect(TabRect(tab)).Contains(p);
            bool overDivider = SplitStrip && p.Y < StripHeightPx && DividerHitRect.Contains(p);
            int overNewTab = -1;
            int overTabList = -1;
            int overChevron = -1;

            if (p.Y < StripHeightPx)
            {
                for (int group = 0; group < GroupCount; group++)
                {
                    if (ShowNewTabButton && NewTabButtonRect(group).Contains(p))
                    {
                        overNewTab = group;
                    }

                    if (ShowTabListButton && TabListButtonRect(group).Contains(p))
                    {
                        overTabList = group;
                    }

                    if (GroupOverflows(group))
                    {
                        if (ScrollChevronRect(group, true).Contains(p))
                        {
                            overChevron = group * 2;
                        }
                        else if (ScrollChevronRect(group, false).Contains(p))
                        {
                            overChevron = group * 2 + 1;
                        }
                    }
                }
            }

            Cursor = overDivider ? Cursors.SizeWE : Cursors.Default;

            if (tab != _hoverTab || overClose != _hoverTabClose || overNewTab != _hoverNewTabGroup
                || overTabList != _hoverTabListGroup || overDivider != _hoverDivider
                || overChevron != _hoverScrollChevron)
            {
                _hoverTab = tab;
                _hoverTabClose = overClose;
                _hoverNewTabGroup = overNewTab;
                _hoverTabListGroup = overTabList;
                _hoverDivider = overDivider;
                _hoverScrollChevron = overChevron;
                InvalidateStrip();
                UpdateToolTip();
            }
        }

        private void ClearHoverState()
        {
            if (_hoverTab != -1 || _hoverNewTabGroup != -1 || _hoverTabClose || _hoverTabListGroup != -1
                || _hoverDivider || _hoverScrollChevron != -1)
            {
                _hoverTab = -1;
                _hoverTabClose = false;
                _hoverNewTabGroup = -1;
                _hoverTabListGroup = -1;
                _hoverDivider = false;
                _hoverScrollChevron = -1;
                InvalidateStrip();
                UpdateToolTip();
            }
        }

        /// <summary>Shows the full tab title as a tooltip whenever a tab is hovered.</summary>
        private void UpdateToolTip()
        {
            // Tabs are custom-drawn regions, not child controls, so there is no control to
            // hand to ToolTip.SetToolTip for automatic positioning — we place it ourselves.
            // Always show on hover (not only when the tab text is truncated), preferring the
            // long ListTitle (e.g. a full breadcrumb path) over the short strip caption.
            if (_hoverTab >= 0 && !_dragging)
            {
                FluentTab tab = _tabs[_hoverTab];
                string tip = string.IsNullOrEmpty(tab.ListTitle) ? tab.Title : tab.ListTitle;
                ShowStripToolTip(tab, tip, TabRect(_hoverTab));
            }
            else
            {
                ShowStripToolTip(null, null, Rectangle.Empty);
            }
        }

        /// <summary>
        /// Shows <paramref name="text" /> just below the strip, aligned to the inline-start edge of
        /// <paramref name="anchorClientRect" /> (given in logical client coordinates). <paramref name="key" />
        /// identifies the described item so repeated moves over it don't re-show, while a different
        /// item — even with the same text — repositions. Passing a null key/text hides the tooltip.
        /// Screen positioning via RectangleToScreen is RTL-correct. Used for tabs and caption buttons.
        /// </summary>
        private void ShowStripToolTip(object key, string text, Rectangle anchorClientRect)
        {
            // A popup covering the strip owns the screen there; a tooltip would sit on top
            // of the very menu the user opened over that tab.
            if (IsTabContextMenuOpen || IsTabListOpen)
            {
                HideStripToolTip();
                return;
            }

            if (Equals(key, _toolTipKey))
            {
                return;
            }

            _toolTipKey = key;

            if (key == null || string.IsNullOrEmpty(text))
            {
                _toolTip?.HideTip();
                return;
            }

            if (_toolTip == null || _toolTip.IsDisposed)
            {
                _toolTip = new ThemedToolTip(Font, EffectiveDpi / 96f);
            }

            // RectangleToScreen maps the item's logical client rect to its true physical
            // position, so the tip anchors under the visible item in both LTR and RTL.
            Rectangle itemScreen = RectangleToScreen(anchorClientRect);
            int stripBottomScreen = RectangleToScreen(new Rectangle(0, 0, 1, StripHeightPx)).Bottom;
            int startX = IsMirrored ? itemScreen.Right : itemScreen.Left;
            _toolTip.ShowText(text, _palette, IsMirrored, startX, stripBottomScreen + Dpi(4));
        }

        /// <summary>Takes the strip tooltip down and forgets what it described, so the next
        /// hover over the same item shows it again rather than being deduped away.</summary>
        private void HideStripToolTip()
        {
            _toolTipKey = null;
            _toolTip?.HideTip();
        }
    }
}
