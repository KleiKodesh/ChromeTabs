using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FluentChromeTabs
{
    // The docked group's own entry on the app's taskbar button - like a Windows Snap group's:
    // hovering the button shows a preview of the whole group (its windows as they lie on the
    // screen), and clicking it brings every window of the group to the front, minimized ones too.
    //
    // Windows draws a preview for each top-level window, and lets a window hand it its own
    // picture (an "iconic" thumbnail). So each group has a stand-in window: always minimized,
    // never on screen, whose preview is the group's picture; when the reader picks it (taskbar
    // or Alt+Tab), Windows restores it, and it raises the group instead and goes back.
    //
    // Self-contained and switchable: GroupTaskbarEntry = false (or dropping this file and its
    // one call in LayoutGroup) removes it.
    public static partial class ScreenDocking
    {
        /// <summary>A docked group of two or more windows gets its own taskbar entry. Default on.</summary>
        public static bool GroupTaskbarEntry { get; set; } = true;

        // The stand-in of each group (by screen).
        private static readonly Dictionary<string, GroupStandIn> StandIns = new Dictionary<string, GroupStandIn>();

        // The windows of a group: docked now, or minimized from it (they come back to it).
        private static List<Form> MembersOf(SnapGroup group)
        {
            var members = new List<Form>();
            foreach (SnapSlot slot in group.Slots)
            {
                if (slot.A != null && !slot.A.IsDisposed) members.Add(slot.A);
                if (slot.B != null && !slot.B.IsDisposed) members.Add(slot.B);
            }
            foreach (var kv in MinimizedPlaces)
                if (!kv.Key.IsDisposed && kv.Value.Key.DeviceName == group.ScreenName && !members.Contains(kv.Key)) members.Add(kv.Key);
            return members;
        }

        /// <summary>Every group of two or more windows has its entry; one that broke up loses it. Called after each layout.</summary>
        private static void UpdateGroupTaskbarEntries()
        {
            var wanted = new HashSet<string>();
            if (GroupTaskbarEntry)
            {
                foreach (SnapGroup group in SnapGroups)
                {
                    List<Form> members = MembersOf(group);
                    if (members.Count < 2) continue;
                    wanted.Add(group.ScreenName);
                    if (!StandIns.TryGetValue(group.ScreenName, out GroupStandIn standIn) || standIn.IsDisposed)
                    {
                        standIn = new GroupStandIn(group.ScreenName);
                        StandIns[group.ScreenName] = standIn;
                        standIn.ShowMinimized();
                    }
                    standIn.Refresh(members);
                }
            }
            foreach (string screen in StandIns.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                if (!StandIns[screen].IsDisposed) StandIns[screen].Close();
                StandIns.Remove(screen);
            }
        }

        /// <summary>
        /// Where the pictures of minimized docked windows are kept (small PNGs, one per window,
        /// deleted with it) - set by the host to a folder of its own. Null: kept small in memory.
        /// Setting it clears what an earlier run left there.
        /// </summary>
        public static string PictureFolder
        {
            get { return _pictureFolder; }
            set
            {
                _pictureFolder = value;
                if (string.IsNullOrEmpty(value)) return;
                try
                {
                    System.IO.Directory.CreateDirectory(value);
                    foreach (string old in System.IO.Directory.GetFiles(value, "*.png")) System.IO.File.Delete(old);
                }
                catch (Exception) { /* read-only app folder: pictures stay in memory */ }
            }
        }

        private static string _pictureFolder;

        // The largest side a kept picture has: a taskbar preview is a few hundred pixels.
        private const int KeptPictureMaxSide = 640;

        // Pictures kept in memory when there is no folder (small: KeptPictureMaxSide).
        private static readonly Dictionary<Form, Bitmap> KeptPictures = new Dictionary<Form, Bitmap>();
        private static readonly HashSet<Form> PictureCleanupHooked = new HashSet<Form>();

        // A window about to be minimized: its picture is kept (it has none to take once minimized),
        // and the preview shows it where it will come back.
        private static void RememberPicture(Form window)
        {
            if (!GroupTaskbarEntry || window.IsDisposed || !window.IsHandleCreated || window.WindowState == FormWindowState.Minimized) return;
            if (!IsDocked(window)) return;
            Bitmap small;
            using (Bitmap shot = GroupStandIn.CaptureWindow(window))
            {
                if (shot == null) return;
                double scale = Math.Min(1.0, KeptPictureMaxSide / (double)Math.Max(shot.Width, shot.Height));
                small = new Bitmap(shot, Math.Max(1, (int)(shot.Width * scale)), Math.Max(1, (int)(shot.Height * scale)));
            }
            if (PictureCleanupHooked.Add(window)) window.FormClosed += (s, e) => ForgetPicture(window);
            string file = PictureFile(window);
            if (file != null)
            {
                try { small.Save(file, System.Drawing.Imaging.ImageFormat.Png); small.Dispose(); return; }
                catch (Exception) { /* fall back to memory */ }
            }
            if (KeptPictures.TryGetValue(window, out Bitmap old)) old.Dispose();
            KeptPictures[window] = small;
        }

        // The kept picture of a window, as a new bitmap the caller disposes; null if none.
        private static Bitmap LoadPicture(Form window)
        {
            if (KeptPictures.TryGetValue(window, out Bitmap kept)) return new Bitmap(kept);
            string file = PictureFile(window);
            if (file == null || !System.IO.File.Exists(file)) return null;
            try
            {
                // Read through a copy so the file is not held open.
                using (var stream = new System.IO.MemoryStream(System.IO.File.ReadAllBytes(file)))
                using (var image = Image.FromStream(stream))
                    return new Bitmap(image);
            }
            catch (Exception) { return null; }
        }

        private static void ForgetPicture(Form window)
        {
            PictureCleanupHooked.Remove(window);
            if (KeptPictures.TryGetValue(window, out Bitmap kept)) { kept.Dispose(); KeptPictures.Remove(window); }
            string file = PictureFile(window);
            if (file != null) try { System.IO.File.Delete(file); } catch (Exception) { }
        }

        private static string PictureFile(Form window)
            => string.IsNullOrEmpty(_pictureFolder) || !window.IsHandleCreated ? null
               : System.IO.Path.Combine(_pictureFolder, window.Handle.ToInt64().ToString("x") + ".png");

        // Where each window of the group will be once the group is brought back: the current
        // layout with the minimized windows put back in their cells (worked out on a copy).
        private static List<KeyValuePair<Form, Rectangle>> FullLayout(SnapGroup group)
        {
            var copy = new SnapGroup { ScreenName = group.ScreenName, Rows = group.Rows, Split = group.Split };
            for (int i = 0; i < 2; i++)
            {
                copy.Slots[i].A = group.Slots[i].A;
                copy.Slots[i].B = group.Slots[i].B;
                copy.Slots[i].Split = group.Slots[i].Split;
            }
            foreach (var kv in MinimizedPlaces)
            {
                if (kv.Key.IsDisposed || kv.Value.Key.DeviceName != group.ScreenName) continue;
                SnapCell cell = kv.Value.Value;
                if (IsHalf(cell) && copy.Rows != IsRowsHalf(cell)) continue; // the group changed its shape since
                Locate(cell, copy.Rows, out int slotIndex, out int part);
                SnapSlot slot = copy.Slots[slotIndex];
                if (part < 0) { if (slot.IsEmpty) slot.A = kv.Key; continue; }
                if (slot.IsEmpty) slot.A = kv.Key;
                else if (slot.B == null) { if (part == 0) { slot.B = slot.A; slot.A = kv.Key; } else slot.B = kv.Key; }
            }
            var places = new List<KeyValuePair<Form, Rectangle>>();
            for (int i = 0; i < 2; i++)
            {
                SnapSlot slot = copy.Slots[i];
                if (slot.A != null) places.Add(new KeyValuePair<Form, Rectangle>(slot.A, RectOf(copy, i, 0)));
                if (slot.B != null) places.Add(new KeyValuePair<Form, Rectangle>(slot.B, RectOf(copy, i, 1)));
            }
            return places;
        }

        // The group's windows to the front, minimized ones restored (they take their place back),
        // and the one the reader used last on top.
        private static void RaiseGroup(string screenName)
        {
            SnapGroup group = SnapGroups.Find(g => g.ScreenName == screenName);
            if (group == null) return;
            List<Form> members = MembersOf(group);
            foreach (Form w in members.Where(w => w.WindowState == FormWindowState.Minimized).ToList())
                w.WindowState = FormWindowState.Normal;
            Form last = members.FirstOrDefault(w => w == Form.ActiveForm) ?? members.FirstOrDefault();
            foreach (Form w in members) if (w != last && !w.IsDisposed) w.BringToFront();
            if (last != null && !last.IsDisposed) last.Activate();
        }

        /// <summary>The stand-in window of a group: only its taskbar preview is ever seen.</summary>
        private sealed class GroupStandIn : Form
        {
            private readonly string _screenName;
            private List<Form> _members = new List<Form>();

            public GroupStandIn(string screenName)
            {
                _screenName = screenName;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = true;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-32000, -32000);
                Size = new Size(1, 1);
            }

            public void ShowMinimized()
            {
                WindowState = FormWindowState.Minimized;
                Show();
            }

            /// <summary>The group's windows changed (or moved): new title, icon and picture.</summary>
            public void Refresh(List<Form> members)
            {
                _members = members;
                Form first = members.FirstOrDefault();
                if (first != null)
                {
                    Text = first.Text;
                    if (first.Icon != null) Icon = first.Icon;
                }
                if (IsHandleCreated) DwmInvalidateIconicBitmaps(Handle);
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                int on = 1;
                DwmSetWindowAttribute(Handle, DWMWA_FORCE_ICONIC_REPRESENTATION, ref on, sizeof(int));
                DwmSetWindowAttribute(Handle, DWMWA_HAS_ICONIC_BITMAP, ref on, sizeof(int));
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                // Picked from the taskbar or Alt+Tab: Windows restores it - it raises the group and
                // goes back to being only a preview.
                if (WindowState != FormWindowState.Minimized && Visible)
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed) return;
                        WindowState = FormWindowState.Minimized;
                        RaiseGroup(_screenName);
                    }));
                }
            }

            protected override void WndProc(ref Message m)
            {
                const int WM_DWMSENDICONICTHUMBNAIL = 0x0323, WM_DWMSENDICONICLIVEPREVIEWBITMAP = 0x0326;
                if (m.Msg == WM_DWMSENDICONICTHUMBNAIL)
                {
                    long lp = m.LParam.ToInt64();
                    SendPicture(new Size((int)((lp >> 16) & 0xFFFF), (int)(lp & 0xFFFF)), live: false);
                    return;
                }
                if (m.Msg == WM_DWMSENDICONICLIVEPREVIEWBITMAP)
                {
                    SendPicture(Size.Empty, live: true);
                    return;
                }
                base.WndProc(ref m);
            }

            // The group as it lies on the screen: each window's picture at its place, scaled to fit
            // the preview (or full size for the hover peek).
            private void SendPicture(Size max, bool live)
            {
                SnapGroup group = SnapGroups.Find(g => g.ScreenName == _screenName);
                if (group == null) return;
                Rectangle area = group.Area;
                double scale = live ? 1.0 : Math.Min(max.Width / (double)area.Width, max.Height / (double)area.Height);
                int width = Math.Max(1, (int)(area.Width * scale)), height = Math.Max(1, (int)(area.Height * scale));
                using (var picture = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(picture))
                    {
                        g.Clear(Color.Transparent);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        // What picking it gives: the whole group, minimized windows back in their places.
                        foreach (var place in FullLayout(group))
                        {
                            Form w = place.Key;
                            if (w.IsDisposed || !w.IsHandleCreated) continue;
                            var dest = new Rectangle(
                                (int)((place.Value.Left - area.Left) * scale), (int)((place.Value.Top - area.Top) * scale),
                                (int)(place.Value.Width * scale), (int)(place.Value.Height * scale));
                            // On screen: pictured now, used once. Minimized: the picture kept when it went.
                            using (Bitmap shot = w.WindowState != FormWindowState.Minimized ? CaptureWindow(w) : LoadPicture(w))
                            {
                                if (shot != null) g.DrawImage(shot, dest);
                                else using (var brush = new SolidBrush(w.BackColor)) g.FillRectangle(brush, dest);
                            }
                        }
                    }
                    IntPtr hbitmap = picture.GetHbitmap(Color.FromArgb(0));
                    try
                    {
                        if (live) DwmSetIconicLivePreviewBitmap(Handle, hbitmap, IntPtr.Zero, 0);
                        else DwmSetIconicThumbnail(Handle, hbitmap, 0);
                    }
                    finally { DeleteObject(hbitmap); }
                }
            }

            // A window's picture as drawn on screen, web content included (PW_RENDERFULLCONTENT).
            public static Bitmap CaptureWindow(Form window)
            {
                Rectangle outer = window.Bounds, seen = VisibleBounds(window);
                if (seen.Width <= 0 || seen.Height <= 0) return null;
                var full = new Bitmap(Math.Max(1, outer.Width), Math.Max(1, outer.Height), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(full))
                {
                    IntPtr hdc = g.GetHdc();
                    try { PrintWindow(window.Handle, hdc, PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(hdc); }
                }
                // Only what the eye sees (without the invisible resize borders).
                var cropped = full.Clone(new Rectangle(seen.Left - outer.Left, seen.Top - outer.Top, seen.Width, seen.Height), full.PixelFormat);
                full.Dispose();
                return cropped;
            }

            // Never in the way: no activation when shown, never on screen.
            protected override bool ShowWithoutActivation => true;
        }

        private const int DWMWA_FORCE_ICONIC_REPRESENTATION = 7, DWMWA_HAS_ICONIC_BITMAP = 10;
        private const uint PW_RENDERFULLCONTENT = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetIconicThumbnail(IntPtr hwnd, IntPtr hbmp, uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetIconicLivePreviewBitmap(IntPtr hwnd, IntPtr hbmp, IntPtr ptClient, uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmInvalidateIconicBitmaps(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);
    }
}
