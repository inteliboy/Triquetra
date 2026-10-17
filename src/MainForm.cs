using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Triquetra
{
    class MainForm : Form
    {
        static readonly string AppVersion =
            Assembly.GetExecutingAssembly().GetName().Version.ToString();

        bool         _busy;
        string       _baseUrl      = "";
        List<string> _builds       = new List<string>();
        string       _localVer;
        int[]        _localParts   = new int[0];
        string       _selectedBuild = "";

        Label       _lblLocal, _lblArch, _lblDisplay, _lblSelBuild, _lblProgress;
        ListBox     _buildList;
        FlatProgressBar _dlBar;
        Button      _btnRefresh, _btnInstall, _btnEP, _btnDism, _btnReboot, _btnCleanup;
        Button      _btnTheme;
        TabControl  _tabs;
        TabPage     _tabUpdate, _tabSettings, _tabLog;
        TextBox     _tbUrl, _tbUser, _tbPass, _tbMirrorUrl, _tbHedgePercent;
        CheckBox    _cbDisableSelfUpdate, _cbDryRun, _cbYes, _cbDismApi, _cbAccelDownloads;
        Panel       _mirrorUrlRow, _hedgePercentRow;
        RichTextBox _logBox;
        Label       _statusLabel;
        FlatProgressBar _spinBar;

        // Sibling overlay panels covering native TabControl chrome that
        // ignores BackColor under visual styles - ported from
        // LenovoRepoBuilder\src\MainForm.cs (itself generalized from
        // UUPDumpGUI's hardcoded-2-tab version to an arbitrary tab count).
        Panel   _tabRowFill, _tabFrameTop, _tabFrameBottom, _tabFrameLeft, _tabFrameRight, _tabGroupTop;
        Panel[] _tabInterGapFills;
        Action  _layoutTabOverlays;

        // True once the handle is created and BeginInvoke is safe to call.
        volatile bool _handleReady;

        public MainForm()
        {
            // AppConfig.Load() and Theme.IsDark's initial value are resolved
            // in Program.Main, before Application.EnableVisualStyles() -
            // native controls need SetPreferredAppMode called before that,
            // so it can't be deferred to here.
            SuspendLayout();
            Text          = "Triquetra Updater v" + AppVersion;
            MinimumSize   = new Size(800, 560);
            Size          = new Size(960, 660);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor     = Theme.Bg;
            Font          = Ctrl.SegoUI();
            try
            {
                Icon = Icon.ExtractAssociatedIcon(
                    Process.GetCurrentProcess().MainModule.FileName);
            }
            catch { }

            BuildUI();
            // The layout is authored at 96 DPI; fonts are in points and already
            // follow DPI, so scale every size/location/padding to match. The
            // tab overlays are derived from native (device-pixel) tab rects,
            // so they are re-laid-out afterwards instead of being scaled.
            Ctrl.ScaleForDpi(this);
            if (_layoutTabOverlays != null) _layoutTabOverlays();
            ResumeLayout(false);

            // Subscribe to log events. Guard with _handleReady to avoid
            // calling BeginInvoke before the window handle exists.
            Logger.MessageLogged += delegate(string msg)
            {
                if (_handleReady && !IsDisposed)
                {
                    try { BeginInvoke(new Action<string>(AppendLog), msg); }
                    catch { /* form may have closed */ }
                }
            };
        }

        // Accessing .Handle below forces each native control to be created
        // now rather than lazily - safe here since OnHandleCreated only runs
        // once this form's own handle already exists.
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.SetDarkTitleBar(Handle, Theme.IsDark);
            NativeMethods.SetBorderAndCaptionColor(Handle,
                Theme.IsDark ? Theme.Border : Color.Empty,
                Theme.IsDark ? Theme.Surface : Color.Empty);
            ApplyScrollBarThemes();

            _handleReady = true;
            // Now safe to kick off background work.
            ThreadPool.QueueUserWorkItem(delegate(object state) { StartupTasks(); });
        }

        // Must be re-applied on every theme toggle, not just once at handle
        // creation - a scrollbar themed dark at startup stays a black track
        // forever if this is only ever called once, regardless of a later
        // toggle to light.
        void ApplyScrollBarThemes()
        {
            NativeMethods.SetScrollBarTheme(_buildList.Handle, Theme.IsDark);
            NativeMethods.SetScrollBarTheme(_logBox.Handle, Theme.IsDark);
        }

        // ── Build UI ─────────────────────────────────────────────────────────
        void BuildUI()
        {
            // Top bar
            Panel topBar      = new Panel();
            topBar.Dock       = DockStyle.Top;
            topBar.Height     = 48;
            topBar.BackColor  = Theme.Surface;
            Ctrl.TagThemeSurface(topBar);

            Label lblTitle    = Ctrl.MakeLabel("Triquetra", false, 14);
            lblTitle.Font     = Ctrl.SegoUI(14, FontStyle.Bold);
            lblTitle.Location = new Point(16, 12);

            Label lblVer      = Ctrl.MakeLabel(" v" + AppVersion, true, 9);
            lblVer.Location   = new Point(lblTitle.Right, 18);

            // Owner-drawn sun/moon icon rather than an emoji glyph in
            // Button.Text - a color emoji font renders taller than its
            // regular-text metrics suggest and clips at the bottom of a
            // tightly-sized button. Drawing it ourselves guarantees it fits.
            _btnTheme          = new Button();
            _btnTheme.Text     = "";
            _btnTheme.Width    = 36; _btnTheme.Height = 28;
            _btnTheme.FlatStyle = FlatStyle.Flat;
            _btnTheme.BackColor = Theme.Surface; _btnTheme.ForeColor = Theme.Fg;
            _btnTheme.Cursor    = Cursors.Hand;
            _btnTheme.Anchor   = AnchorStyles.Right | AnchorStyles.Top;
            _btnTheme.FlatAppearance.BorderSize = 0;
            _btnTheme.Paint    += DrawThemeToggleIcon;
            _btnTheme.Click    += delegate(object s, EventArgs e)
            {
                Theme.IsDark      = !Theme.IsDark;
                AppConfig.Theme   = Theme.IsDark ? "dark" : "light";
                AppConfig.Save();
                ApplyTheme();
            };
            topBar.Resize    += delegate(object s, EventArgs e)
            {
                _btnTheme.Location = new Point(
                    topBar.Width - _btnTheme.Width - 12,
                    (topBar.Height - _btnTheme.Height) / 2);
            };
            topBar.Controls.Add(lblTitle);
            topBar.Controls.Add(lblVer);
            topBar.Controls.Add(_btnTheme);

            Panel sep = new Panel();
            sep.Dock = DockStyle.Top; sep.Height = 1; sep.BackColor = Theme.Border;

            _tabs = new TabControl();
            _tabs.Dock    = DockStyle.Fill;
            _tabs.Padding = new Point(18, 7);
            _tabs.Font    = Ctrl.SegoUI();
            // Native TabControl always paints its tab strip via visual
            // styles (light, regardless of BackColor) - owner-draw + the
            // overlay panels built below cover this.
            _tabs.Appearance = TabAppearance.FlatButtons;
            _tabs.DrawMode   = TabDrawMode.OwnerDrawFixed;
            _tabs.DrawItem  += DrawTab;

            _tabUpdate   = new TabPage("  Update  ");
            _tabSettings = new TabPage("  Settings  ");
            _tabLog      = new TabPage("  Log  ");
            _tabs.TabPages.Add(_tabUpdate);
            _tabs.TabPages.Add(_tabSettings);
            _tabs.TabPages.Add(_tabLog);

            // Fixed, equal width for every tab, measured from the widest
            // real label rather than a hand-picked constant.
            int  tabTextW = 0;
            Size tabTextH = Size.Empty;
            foreach (TabPage tp in _tabs.TabPages)
            {
                Size sz = TextRenderer.MeasureText(tp.Text, _tabs.Font);
                if (sz.Width > tabTextW) tabTextW = sz.Width;
                tabTextH = sz;
            }
            _tabs.SizeMode = TabSizeMode.Fixed;
            _tabs.ItemSize = new Size(tabTextW + _tabs.Padding.X * 2, tabTextH.Height + _tabs.Padding.Y * 2);

            BuildTabOverlays();

            Panel statusBar = new Panel();
            statusBar.Dock = DockStyle.Bottom; statusBar.Height = 30;
            statusBar.BackColor = Theme.Surface;
            Ctrl.TagThemeSurface(statusBar);

            _statusLabel           = new Label();
            _statusLabel.AutoSize  = false; _statusLabel.Dock = DockStyle.Fill;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _statusLabel.Font      = Ctrl.SegoUI(8.25f);
            _statusLabel.ForeColor = Theme.FgDim; _statusLabel.BackColor = Color.Transparent;
            _statusLabel.Padding   = new Padding(12, 0, 0, 0);
            _statusLabel.Text      = "Initialising ...";

            _spinBar         = new FlatProgressBar();
            _spinBar.Dock    = DockStyle.Right; _spinBar.Width = 150; _spinBar.Height = 5;
            _spinBar.Visible = false;

            statusBar.Controls.Add(_statusLabel);
            statusBar.Controls.Add(_spinBar);

            Controls.Add(_tabs);
            Controls.Add(sep);
            Controls.Add(topBar);
            Controls.Add(statusBar);

            BuildUpdateTab();
            BuildSettingsTab();
            BuildLogTab();
        }

        // Sibling overlay panels covering native TabControl chrome that
        // ignores BackColor under visual styles: the tab-row strip right of
        // the last tab, the light "notebook frame" border FlatButtons still
        // leaves around the page area, and the gaps between adjacent tab
        // buttons. Generalized over however many tabs exist (3 here).
        void BuildTabOverlays()
        {
            _tabRowFill = new Panel(); _tabRowFill.BackColor = Theme.Bg;
            Controls.Add(_tabRowFill); _tabRowFill.BringToFront();

            _tabFrameTop = new Panel(); _tabFrameTop.BackColor = Theme.Bg;
            Controls.Add(_tabFrameTop); _tabFrameTop.BringToFront();

            _tabFrameBottom = new Panel(); _tabFrameBottom.BackColor = Theme.Bg;
            Controls.Add(_tabFrameBottom); _tabFrameBottom.BringToFront();

            _tabFrameLeft = new Panel(); _tabFrameLeft.BackColor = Theme.Bg;
            Controls.Add(_tabFrameLeft); _tabFrameLeft.BringToFront();

            _tabFrameRight = new Panel(); _tabFrameRight.BackColor = Theme.Bg;
            Controls.Add(_tabFrameRight); _tabFrameRight.BringToFront();

            _tabGroupTop = new Panel(); _tabGroupTop.BackColor = Theme.Bg;
            Controls.Add(_tabGroupTop); _tabGroupTop.BringToFront();

            int gaps = Math.Max(0, _tabs.TabCount - 1);
            _tabInterGapFills = new Panel[gaps];
            for (int i = 0; i < gaps; i++)
            {
                Panel gap = new Panel(); gap.BackColor = Theme.Bg;
                Controls.Add(gap); gap.BringToFront();
                _tabInterGapFills[i] = gap;
            }

            Action layout = delegate()
            {
                if (_tabs.TabCount == 0) return;
                int e2 = Ctrl.S(2), e3 = Ctrl.S(3), e4 = Ctrl.S(4), e6 = Ctrl.S(6), e7 = Ctrl.S(7);
                Rectangle lastTabRect = _tabs.GetTabRect(_tabs.TabCount - 1);

                _tabRowFill.Location = new Point(_tabs.Left + lastTabRect.Right - e3, _tabs.Top);
                _tabRowFill.Size = new Size(Math.Max(0, _tabs.Width - lastTabRect.Right + e3), lastTabRect.Bottom);

                for (int i = 0; i < gaps; i++)
                {
                    Rectangle a = _tabs.GetTabRect(i);
                    Rectangle b = _tabs.GetTabRect(i + 1);
                    _tabInterGapFills[i].Location = new Point(_tabs.Left + a.Right - e2, _tabs.Top);
                    _tabInterGapFills[i].Size = new Size(Math.Max(0, b.Left - a.Right + e4), lastTabRect.Bottom);
                }

                _tabFrameTop.Location = new Point(_tabs.Left, _tabs.Top + lastTabRect.Bottom - e3);
                _tabFrameTop.Size = new Size(_tabs.Width, e7);

                _tabGroupTop.Location = new Point(_tabs.Left, _tabs.Top);
                _tabGroupTop.Size = new Size(_tabs.Width, e4);

                _tabFrameBottom.Location = new Point(_tabs.Left, _tabs.Bottom - e6);
                _tabFrameBottom.Size = new Size(_tabs.Width, e6);

                _tabFrameLeft.Location = new Point(_tabs.Left, _tabs.Top);
                _tabFrameLeft.Size = new Size(e4, _tabs.Height);

                _tabFrameRight.Location = new Point(_tabs.Right - e4, _tabs.Top);
                _tabFrameRight.Size = new Size(e4, _tabs.Height);
            };
            _layoutTabOverlays = layout;
            layout();
            _tabs.Resize += delegate(object s, EventArgs e) { layout(); };
        }

        // Owner-draws one tab's fill/text/accent underline - the native
        // TabControl header always paints itself via visual styles (light,
        // regardless of BackColor) otherwise.
        void DrawTab(object sender, DrawItemEventArgs e)
        {
            TabControl tc = (TabControl)sender;
            TabPage page = tc.TabPages[e.Index];
            bool selected = e.Index == tc.SelectedIndex;
            Rectangle bounds = tc.GetTabRect(e.Index);

            using (SolidBrush bg = new SolidBrush(selected ? Theme.Surface : Theme.Bg))
                e.Graphics.FillRectangle(bg, bounds);
            if (selected)
                using (Pen p = new Pen(Theme.Accent, 2))
                    e.Graphics.DrawLine(p, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);

            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                using (SolidBrush fg = new SolidBrush(selected ? Theme.Fg : Theme.FgDim))
                    e.Graphics.DrawString(page.Text.Trim(), tc.Font, fg, bounds, sf);
            }
        }

        void DrawThemeToggleIcon(object sender, PaintEventArgs e)
        {
            Control btn = (Control)sender;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int cx = btn.Width / 2, cy = btn.Height / 2;

            if (Theme.IsDark)
            {
                int r = 5;
                using (SolidBrush b = new SolidBrush(Theme.Fg))
                    e.Graphics.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
                using (Pen p = new Pen(Theme.Fg, 1.5f))
                {
                    int rayInner = r + 3, rayOuter = r + 6;
                    for (int i = 0; i < 8; i++)
                    {
                        double ang = i * Math.PI / 4;
                        int x1 = cx + (int)(rayInner * Math.Cos(ang));
                        int y1 = cy + (int)(rayInner * Math.Sin(ang));
                        int x2 = cx + (int)(rayOuter * Math.Cos(ang));
                        int y2 = cy + (int)(rayOuter * Math.Sin(ang));
                        e.Graphics.DrawLine(p, x1, y1, x2, y2);
                    }
                }
            }
            else
            {
                int r = 7;
                using (SolidBrush b = new SolidBrush(Theme.Fg))
                    e.Graphics.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
                // Cut a smaller offset circle out of the disc using the
                // button's own background color to leave a crescent shape.
                using (SolidBrush bg = new SolidBrush(btn.BackColor))
                    e.Graphics.FillEllipse(bg, cx - r + 4, cy - r - 2, r * 2, r * 2);
            }
        }

        // ── Update tab ───────────────────────────────────────────────────────
        void BuildUpdateTab()
        {
            _tabUpdate.BackColor = Theme.Bg;

            TableLayoutPanel left = new TableLayoutPanel();
            left.Dock = DockStyle.Fill; left.ColumnCount = 1;
            left.BackColor = Color.Transparent;
            left.Padding   = new Padding(16, 14, 8, 14);
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            TableLayoutPanel infoGrid = new TableLayoutPanel();
            infoGrid.AutoSize = true; infoGrid.ColumnCount = 2; infoGrid.RowCount = 3;
            infoGrid.BackColor = Color.Transparent;
            infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _lblLocal   = AddInfoRow(infoGrid, "Local build",     0);
            _lblArch    = AddInfoRow(infoGrid, "Architecture",    1);
            _lblDisplay = AddInfoRow(infoGrid, "Display version", 2);

            Label lblBuildsHdr = Ctrl.MakeLabel(
                "Available builds (your Windows branch)", true);
            lblBuildsHdr.Margin = new Padding(0, 10, 0, 4);

            _buildList                     = new ListBox();
            _buildList.Dock                = DockStyle.Fill;
            _buildList.BackColor           = Theme.Surface;
            _buildList.ForeColor           = Theme.Fg;
            _buildList.Font                = Ctrl.SegoUI();
            _buildList.SelectionMode       = SelectionMode.One;
            _buildList.HorizontalScrollbar = false;
            // Default (true) snaps the height to whole rows, leaving a strip of the
            // bordered wrapper's lighter background below the last row.
            _buildList.IntegralHeight      = false;
            _buildList.SelectedIndexChanged += delegate(object s, EventArgs e)
            {
                _selectedBuild = _buildList.SelectedItem != null
                    ? _buildList.SelectedItem.ToString() : "";
                _lblSelBuild.Text = string.IsNullOrEmpty(_selectedBuild)
                    ? "(latest)" : _selectedBuild;
            };

            // Ctrl.Bordered draws a themed 1px border around the list (a
            // native ListBox's BorderStyle can't be re-colored otherwise)
            // and is tagged so a runtime theme toggle re-colors it too.
            Panel listWrap = Ctrl.Bordered(_buildList);
            listWrap.Dock = DockStyle.Fill;

            Label lblHint = Ctrl.MakeLabel(
                "Leave blank (deselect) to install the latest build", true);
            lblHint.Margin = new Padding(0, 4, 0, 0);

            left.Controls.Add(infoGrid,    0, 0);
            left.Controls.Add(lblBuildsHdr, 0, 1);
            left.Controls.Add(listWrap,    0, 2);
            left.Controls.Add(lblHint,     0, 3);

            // Right action panel
            Panel right = new Panel();
            right.Dock = DockStyle.Right; right.Width = 252;
            right.BackColor = Color.Transparent;
            right.Padding   = new Padding(8, 14, 16, 14);

            int y = 14;
            Action<Control> RAdd = null;
            RAdd = delegate(Control c)
            {
                c.Left = 8; c.Top = y; c.Width = 228;
                right.Controls.Add(c);
                y += c.Height + 6;
            };
            Action RSep = delegate()
            {
                Panel p = new Panel();
                p.Height = 1; p.BackColor = Theme.Border;
                RAdd(p); y += 4;
            };

            RAdd(Ctrl.MakeLabel("Selected build", true));

            _lblSelBuild           = new Label();
            _lblSelBuild.Text      = "(latest)";
            _lblSelBuild.Font      = Ctrl.SegoUI(9, FontStyle.Bold);
            _lblSelBuild.ForeColor = Theme.Accent;
            _lblSelBuild.BackColor = Color.Transparent;
            _lblSelBuild.AutoSize  = false; _lblSelBuild.Height = 18;
            RAdd(_lblSelBuild);

            _lblProgress           = new Label();
            _lblProgress.Text      = "";
            _lblProgress.Font      = Ctrl.SegoUI(8.25f);
            _lblProgress.ForeColor = Theme.FgDim;
            _lblProgress.BackColor = Color.Transparent;
            _lblProgress.AutoSize  = false; _lblProgress.Height = 14;
            RAdd(_lblProgress);

            _dlBar        = new FlatProgressBar();
            _dlBar.Height = 6;
            RAdd(_dlBar);

            y += 6;

            _btnRefresh         = Ctrl.NormalButton("Refresh builds");
            _btnRefresh.Click  += delegate(object s, EventArgs e) { OnRefresh(); };
            RAdd(_btnRefresh);

            _btnInstall         = Ctrl.AccentButton("Install update");
            _btnInstall.Click  += delegate(object s, EventArgs e) { OnInstall(); };
            _btnInstall.Enabled = false;
            RAdd(_btnInstall);

            _btnEP              = Ctrl.NormalButton("Install Enablement Package");
            _btnEP.Click       += delegate(object s, EventArgs e) { OnEP(); };
            _btnEP.Enabled      = false;
            RAdd(_btnEP);

            RSep();

            _btnDism        = Ctrl.DangerButton("DISM ResetBase");
            _btnDism.Click += delegate(object s, EventArgs e) { OnDismResetBase(); };
            RAdd(_btnDism);

            RSep();

            _btnReboot        = Ctrl.NormalButton("Reboot now");
            _btnReboot.Click += delegate(object s, EventArgs e) { OnReboot(); };
            RAdd(_btnReboot);

            _btnCleanup        = Ctrl.NormalButton("Clean up CAB / ESD files");
            _btnCleanup.Click += delegate(object s, EventArgs e) { OnCleanup(); };
            RAdd(_btnCleanup);

            _tabUpdate.Controls.Add(left);
            _tabUpdate.Controls.Add(right);
        }

        Label AddInfoRow(TableLayoutPanel grid, string label, int row)
        {
            Label lbl = Ctrl.MakeLabel(label, true);
            Label val = new Label();
            val.Text = "-"; val.Font = Ctrl.SegoUI(9, FontStyle.Bold);
            val.ForeColor = Theme.Accent; val.BackColor = Color.Transparent;
            val.AutoSize  = true; val.Margin = new Padding(14, 0, 0, 4);
            grid.Controls.Add(lbl, 0, row);
            grid.Controls.Add(val, 1, row);
            return val;
        }

        // ── Settings tab ─────────────────────────────────────────────────────
        void BuildSettingsTab()
        {
            _tabSettings.BackColor = Theme.Bg;

            TableLayoutPanel tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill; tl.ColumnCount = 2;
            tl.Padding = new Padding(28); tl.BackColor = Color.Transparent;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _tbUrl  = Ctrl.MakeEntry(); _tbUrl.Text  = AppConfig.BaseUrl;
            _tbUser = Ctrl.MakeEntry(true); _tbUser.Text = AppConfig.User;
            _tbPass = Ctrl.MakeEntry(true); _tbPass.Text = AppConfig.Password;

            AddSettingRow(tl, "Base URL",  _tbUrl,  0);
            AddSettingRow(tl, "Username",  _tbUser, 1);
            AddSettingRow(tl, "Password",  _tbPass, 2);

            Panel sep1 = new Panel();
            sep1.Height = 1; sep1.BackColor = Theme.Border;
            sep1.Margin = new Padding(0, 10, 0, 10);
            tl.SetColumnSpan(sep1, 2); tl.Controls.Add(sep1, 0, 3);

            _cbDisableSelfUpdate = Ctrl.MakeCheck(
                "Disable self-update");
            _cbDryRun = Ctrl.MakeCheck(
                "Dry run  (download only, do not install packages)");
            _cbYes = Ctrl.MakeCheck(
                "Unattended  (auto-confirm all prompts)");
            _cbDismApi = Ctrl.MakeCheck(
                "Use DISM API instead of dism.exe  (experimental, falls back automatically)");
            _cbAccelDownloads = Ctrl.MakeCheck(
                "Enable accelerated download  (hedged, splits across a mirror server)");
            _cbDisableSelfUpdate.Checked = AppConfig.DisableSelfUpdate;
            _cbDryRun.Checked   = AppConfig.DryRun;
            _cbYes.Checked      = AppConfig.Yes;
            _cbDismApi.Checked  = AppConfig.UseDismApi;
            _cbAccelDownloads.Checked = AppConfig.AccelDownloads;

            FlowLayoutPanel checks = new FlowLayoutPanel();
            checks.FlowDirection = FlowDirection.TopDown;
            checks.WrapContents  = false; checks.AutoSize = true;
            checks.BackColor     = Color.Transparent;
            checks.Controls.Add(_cbDisableSelfUpdate);
            checks.Controls.Add(_cbDryRun);
            checks.Controls.Add(_cbYes);
            checks.Controls.Add(_cbDismApi);
            checks.Controls.Add(_cbAccelDownloads);
            tl.SetColumnSpan(checks, 2); tl.Controls.Add(checks, 0, 4);

            // Unlocked only while "Enable accelerated download" is checked -
            // AcceleratedDownload.cs itself also refuses to run without a
            // configured mirror, this is just the UI reflecting that.
            _tbMirrorUrl = Ctrl.MakeEntry(); _tbMirrorUrl.Text = AppConfig.MirrorUrl;
            _tbHedgePercent = Ctrl.MakeEntry(); _tbHedgePercent.Text = AppConfig.HedgeBudgetPercent.ToString();
            _tbHedgePercent.MaxLength = 3;
            _tbHedgePercent.KeyPress += delegate(object s, KeyPressEventArgs e)
            {
                if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
            };
            AddSettingRow(tl, "Mirror server",     _tbMirrorUrl,    5);
            AddSettingRow(tl, "Hedge budget %",    _tbHedgePercent, 6);
            _mirrorUrlRow      = (Panel)_tbMirrorUrl.Parent;
            _hedgePercentRow   = (Panel)_tbHedgePercent.Parent;
            _mirrorUrlRow.Enabled    = AppConfig.AccelDownloads;
            _hedgePercentRow.Enabled = AppConfig.AccelDownloads;

            Panel sep2 = new Panel();
            sep2.Height = 1; sep2.BackColor = Theme.Border;
            sep2.Margin = new Padding(0, 10, 0, 10);
            tl.SetColumnSpan(sep2, 2); tl.Controls.Add(sep2, 0, 7);

            tl.Controls.Add(Ctrl.MakeLabel("Config file", true), 0, 8);
            tl.Controls.Add(Ctrl.MakeLabel(AppConfig.ConfFilePath, true), 1, 8);
            tl.Controls.Add(Ctrl.MakeLabel("Log file", true), 0, 9);
            tl.Controls.Add(Ctrl.MakeLabel(Logger.LogFilePath, true), 1, 9);

            Button btnFolder = Ctrl.NormalButton("Open data folder");
            btnFolder.AutoSize = true;
            btnFolder.Click += delegate(object s, EventArgs e)
                { Process.Start("explorer.exe", AppConfig.ProgramDataDir); };
            tl.Controls.Add(new Label { Height = 0 }, 0, 10);
            tl.Controls.Add(btnFolder, 1, 10);

            _cbAccelDownloads.CheckedChanged += delegate(object s, EventArgs e)
            {
                _mirrorUrlRow.Enabled    = _cbAccelDownloads.Checked;
                _hedgePercentRow.Enabled = _cbAccelDownloads.Checked;
            };

            EventHandler saveHandler = delegate(object s, EventArgs e)
            {
                AppConfig.BaseUrl  = _tbUrl.Text.Trim();
                AppConfig.User     = _tbUser.Text;
                AppConfig.Password = _tbPass.Text;
                AppConfig.DisableSelfUpdate = _cbDisableSelfUpdate.Checked;
                AppConfig.DryRun   = _cbDryRun.Checked;
                AppConfig.Yes      = _cbYes.Checked;
                AppConfig.UseDismApi = _cbDismApi.Checked;
                AppConfig.AccelDownloads = _cbAccelDownloads.Checked;
                AppConfig.MirrorUrl = _tbMirrorUrl.Text.Trim();
                int pct;
                AppConfig.HedgeBudgetPercent = int.TryParse(_tbHedgePercent.Text, out pct)
                    ? Math.Max(0, Math.Min(100, pct)) : 10;
                AppConfig.Save();
            };
            _tbUrl.TextChanged         += saveHandler;
            _tbUser.TextChanged        += saveHandler;
            _tbPass.TextChanged        += saveHandler;
            _cbDisableSelfUpdate.CheckedChanged += saveHandler;
            _cbDryRun.CheckedChanged   += saveHandler;
            _cbYes.CheckedChanged      += saveHandler;
            _cbDismApi.CheckedChanged  += saveHandler;
            _cbAccelDownloads.CheckedChanged += saveHandler;
            _tbMirrorUrl.TextChanged   += saveHandler;
            _tbHedgePercent.TextChanged += saveHandler;

            _tabSettings.Controls.Add(tl);
        }

        void AddSettingRow(TableLayoutPanel tl, string label, TextBox tb, int row)
        {
            tl.Controls.Add(Ctrl.MakeLabel(label), 0, row);
            // Ctrl.Bordered draws a themed 1px border around the entry (a
            // native TextBox's FixedSingle border can't be re-colored
            // otherwise) and is tagged so a runtime theme toggle re-colors
            // it too.
            Panel wrap = Ctrl.Bordered(tb);
            wrap.Dock = DockStyle.Fill; wrap.Margin = new Padding(18, 4, 0, 4);
            // A bare Panel's default Height (100) has no relation to the
            // TextBox it wraps - without pinning it to the entry's own
            // PreferredHeight, the TableLayoutPanel's AutoSize row balloons
            // to 100px since Dock.Fill only fills whatever height the row
            // already decided on, it doesn't derive one from the content.
            wrap.Height = Ctrl.Unscale(tb.PreferredHeight) + 2;
            tl.Controls.Add(wrap, 1, row);
        }

        // ── Log tab ──────────────────────────────────────────────────────────
        void BuildLogTab()
        {
            _tabLog.BackColor = Theme.Bg;

            FlowLayoutPanel toolbar = new FlowLayoutPanel();
            toolbar.Dock = DockStyle.Top; toolbar.Height = 40;
            // Its buttons AutoSize from the font (real pixels), so a fixed 96-DPI
            // height clips them once scaled - let the bar follow its content.
            toolbar.AutoSize = true; toolbar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            toolbar.Padding = new Padding(8, 6, 8, 6);
            toolbar.BackColor = Color.Transparent;
            toolbar.FlowDirection = FlowDirection.LeftToRight;

            Button bCW = Ctrl.NormalButton("Clear window");
            Button bCF = Ctrl.NormalButton("Clear log file");
            Button bCA = Ctrl.NormalButton("Copy all");
            bCW.AutoSize = true; bCW.Margin = new Padding(0, 0, 6, 0);
            bCF.AutoSize = true; bCF.Margin = new Padding(0, 0, 6, 0);
            // bCA never had its Margin zeroed like its siblings above, so it
            // kept Control's default Padding(3,3,3,3) - its 3px top margin
            // against bCW/bCF's explicit 0 rendered it 3px lower than the
            // other two in the FlowLayoutPanel row (confirmed via a pixel
            // scan: bCW/bCF's fill starts at y=121, bCA's at y=124).
            bCA.AutoSize = true; bCA.Margin = new Padding(0, 0, 0, 0);
            bCW.Click += delegate(object s, EventArgs e) { ClearLog(); };
            bCF.Click += delegate(object s, EventArgs e) { ClearLogFile(); };
            bCA.Click += delegate(object s, EventArgs e) { CopyLog(); };
            toolbar.Controls.Add(bCW); toolbar.Controls.Add(bCF); toolbar.Controls.Add(bCA);

            _logBox             = new RichTextBox();
            _logBox.Dock        = DockStyle.Fill; _logBox.ReadOnly = true;
            _logBox.BackColor   = Theme.LogBg; _logBox.ForeColor = Theme.LogFg;
            _logBox.Font        = new Font("Consolas", 8.25f);
            _logBox.WordWrap    = true;
            _logBox.ScrollBars  = RichTextBoxScrollBars.ForcedVertical;
            _logBox.DetectUrls  = false;

            // Ctrl.Bordered draws a themed 1px border (a RichTextBox's
            // border can't be re-colored otherwise) and is tagged so a
            // runtime theme toggle re-colors it too.
            Panel logWrap = Ctrl.Bordered(_logBox);
            logWrap.Dock = DockStyle.Fill;

            _tabLog.Controls.Add(logWrap);
            _tabLog.Controls.Add(toolbar);
        }

        // Applies the current Theme.IsDark to every already-built control.
        // Called once at startup (covers a user whose OS theme differs from
        // the AppConfig override) and again on every toggle of the
        // title-bar button.
        void ApplyTheme()
        {
            BackColor = Theme.Bg;
            if (_btnTheme != null)
            {
                _btnTheme.BackColor = Theme.Surface;
                _btnTheme.ForeColor = Theme.Fg;
            }
            if (_tabs != null)
            {
                _tabs.BackColor = Theme.Bg;
                foreach (TabPage tp in _tabs.TabPages) tp.BackColor = Theme.Bg;
            }
            if (_tabRowFill != null) _tabRowFill.BackColor = Theme.Bg;
            if (_tabFrameTop != null) _tabFrameTop.BackColor = Theme.Bg;
            if (_tabFrameBottom != null) _tabFrameBottom.BackColor = Theme.Bg;
            if (_tabFrameLeft != null) _tabFrameLeft.BackColor = Theme.Bg;
            if (_tabFrameRight != null) _tabFrameRight.BackColor = Theme.Bg;
            if (_tabGroupTop != null) _tabGroupTop.BackColor = Theme.Bg;
            if (_tabInterGapFills != null)
                foreach (Panel p in _tabInterGapFills) p.BackColor = Theme.Bg;

            if (IsHandleCreated)
            {
                NativeMethods.SetDarkTitleBar(Handle, Theme.IsDark);
                NativeMethods.SetBorderAndCaptionColor(Handle,
                    Theme.IsDark ? Theme.Border : Color.Empty,
                    Theme.IsDark ? Theme.Surface : Color.Empty);
                ApplyScrollBarThemes();
            }

            if (_buildList != null)
            {
                _buildList.BackColor = Theme.Surface;
                _buildList.ForeColor = Theme.Fg;
                if (_buildList.Parent != null) _buildList.Parent.BackColor = Theme.Border;
            }
            if (_logBox != null)
            {
                _logBox.BackColor = Theme.LogBg; _logBox.ForeColor = Theme.LogFg;
                if (_logBox.Parent != null) _logBox.Parent.BackColor = Theme.Border;
                RecolorLog(_logBox);
            }
            if (_statusLabel != null) _statusLabel.ForeColor = Theme.FgDim;

            RestyleEntries(this);
            _btnTheme.Invalidate();
            Invalidate(true);
        }

        // Recursively walks every control and re-applies theme colors to
        // the field types whose color is not baked in at construction time.
        void RestyleEntries(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                string tag = c.Tag as string;
                if (tag == "theme-bg") c.BackColor = Theme.Bg;
                else if (tag == "theme-surface") c.BackColor = Theme.Surface;
                else if (tag == "theme-border-wrap") c.BackColor = Theme.Border;
                else if (tag == "theme-normal-btn")
                {
                    Button nb = (Button)c;
                    nb.BackColor = Theme.Surface;
                    nb.ForeColor = Theme.Fg;
                    nb.FlatAppearance.BorderColor = Theme.Border;
                    nb.FlatAppearance.MouseOverBackColor = Theme.SelBg;
                }

                TextBox tb = c as TextBox;
                if (tb != null) Ctrl.ThemeEntry(tb);

                // ThemedCheckBox reads Theme.* live in its own OnPaint - the
                // Invalidate(true) at the end of ApplyTheme is all it needs.

                Label lbl = c as Label;
                if (lbl != null && lbl != _statusLabel && lbl.ForeColor != Theme.Accent)
                    lbl.ForeColor = (lbl.Tag as string) == "dim" ? Theme.FgDim : Theme.Fg;

                if (c.HasChildren) RestyleEntries(c);
            }
        }

        // ── Log helpers ──────────────────────────────────────────────────────
        void AppendLog(string msg)
        {
            if (_logBox == null || _logBox.IsDisposed) return;
            _logBox.SelectionStart  = _logBox.TextLength;
            _logBox.SelectionLength = 0;
            _logBox.SelectionColor  = MsgColor(msg);
            _logBox.AppendText(msg + "\n");
            _logBox.ScrollToCaret();
        }

        static Color MsgColor(string msg)
        {
            string low = msg.ToLower();
            if      (low.Contains("error")   || low.Contains("failed")   || low.Contains("abort"))
                return Theme.Err;
            else if (low.Contains("warn")    || low.Contains("retry")    || low.Contains("skip"))
                return Theme.Warn;
            else if (low.Contains("ok")      || low.Contains("success")  || low.Contains("finished")
                  || low.Contains("complete")|| low.Contains("up-to-date"))
                return Theme.Ok;
            return Theme.LogFg;
        }

        // Splits the log text back into lines and recolors each one by
        // content using the CURRENT theme's colors, so a toggle re-tints
        // already-appended lines instead of leaving them in the old
        // theme's palette.
        void RecolorLog(RichTextBox box)
        {
            if (box == null || box.IsDisposed || box.TextLength == 0) return;
            int savedStart  = box.SelectionStart;
            int savedLength = box.SelectionLength;
            box.SuspendLayout();
            string[] lines = box.Text.Split('\n');
            int pos = 0;
            foreach (string line in lines)
            {
                if (line.Length > 0)
                {
                    box.Select(pos, line.Length);
                    box.SelectionColor = MsgColor(line);
                }
                pos += line.Length + 1;
            }
            box.Select(savedStart, savedLength);
            box.ResumeLayout();
        }

        void ClearLog()     { _logBox.Clear(); }
        void CopyLog()      { Clipboard.SetText(_logBox.Text); }
        void ClearLogFile()
        {
            if (ThemedMessageBox.Show(this,
                    "Permanently erase:\n" + Logger.LogFilePath + "\n\nCannot be undone.",
                    "Clear log file", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                    != DialogResult.Yes)
                return;
            try { Logger.ClearFile(); ClearLog(); Logger.Log("Log file cleared."); }
            catch (Exception ex)
            {
                ThemedMessageBox.Show(this, "Could not clear log file:\n" + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ── Status / busy ────────────────────────────────────────────────────
        void SetStatus(string msg)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(SetStatus), msg); return; }
            _statusLabel.Text = msg;
        }

        void SetBusy(bool busy)
        {
            if (InvokeRequired) { BeginInvoke(new Action<bool>(SetBusy), busy); return; }
            _busy = busy;
            _spinBar.Visible = busy;
            _spinBar.Marquee = busy;
            Button[] btns = new Button[]
                { _btnRefresh, _btnInstall, _btnEP, _btnDism, _btnReboot, _btnCleanup };
            foreach (Button btn in btns) btn.Enabled = !busy;
            if (!busy) RefreshButtonStates();
        }

        void RefreshButtonStates()
        {
            _btnInstall.Enabled = _builds.Count > 0 && !_busy;
            int lm = _localParts.Length > 0 ? _localParts[0] : 0;
            _btnEP.Enabled = (lm == 22621 || lm == 26100) && !_busy;
        }

        void UpdateDlProgress(long done, long total, double speed)
        {
            if (InvokeRequired)
            { BeginInvoke(new Action<long, long, double>(UpdateDlProgress), done, total, speed); return; }
            _dlBar.Value = total > 0 ? (int)Math.Min(done * 100 / total, 100) : 0;
            _lblProgress.Text = total > 0
                ? string.Format("{0:F1} / {1:F1} MB  -  {2:F1} MB/s",
                    done / 1048576.0, total / 1048576.0, speed)
                : string.Format("{0:F1} MB  -  {1:F1} MB/s",
                    done / 1048576.0, speed);
        }

        // ── Startup (runs on threadpool AFTER handle created) ─────────────────
        void StartupTasks()
        {
            try
            {
                SetBusy(true);
                Logger.Log(new string('=', 60));
                Logger.Log("Triquetra Updater " + AppVersion);
                Logger.Log("Config: " + AppConfig.ConfFilePath);

                string fv = RegistryHelper.GetLocalBuildVersion();
                if (fv != null)
                {
                    VersionInfo vi = RegistryHelper.NormalizeVersion(fv);
                    _localVer   = vi.Short;
                    _localParts = vi.Parts;
                }
                string arch = RegistryHelper.GetArch();
                string disp = RegistryHelper.GetDisplayVersion();

                string lv    = _localVer ?? "unknown";
                string ldisp = disp      ?? "-";

                BeginInvoke(new Action(delegate()
                {
                    _lblLocal.Text   = lv;
                    _lblArch.Text    = arch;
                    _lblDisplay.Text = ldisp;
                }));

                if (!AppConfig.DisableSelfUpdate && !string.IsNullOrEmpty(AppConfig.BaseUrl))
                {
                    try
                    {
                        SetStatus("Checking for updater update ...");
                        SelfUpdater.TryUpdate(AppConfig.BaseUrl + "triquetra.exe");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("Self-update check failed (continuing): " + ex.Message);
                    }
                }

                // Did the last install actually change the build after its
                // reboot? (MUMX v2 updates can be reverted at boot even though
                // dism reported success.)
                string pendingWarn = PendingInstall.Check(_localParts);
                if (pendingWarn != null)
                    BeginInvoke(new Action(delegate()
                    {
                        ThemedMessageBox.Show(this, pendingWarn, "Update did not apply",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }));

                SetStatus("Ready");
            }
            catch (Exception ex)
            {
                Logger.Log("Startup error: " + ex.Message);
                SetStatus("Startup error – see Log tab.");
            }
            finally
            {
                SetBusy(false);
            }
            FetchBuildsInner();
        }

        // ── Refresh ──────────────────────────────────────────────────────────
        void OnRefresh()
        {
            if (_busy) return;
            ThreadPool.QueueUserWorkItem(delegate(object state) { FetchBuildsInner(); });
        }

        void FetchBuildsInner()
        {
            SetBusy(true);
            try
            {
                if (string.IsNullOrEmpty(AppConfig.BaseUrl))
                    throw new Exception("No base URL configured - set one in the Settings tab.");
                string baseUrl = AppConfig.BaseUrl;

                SetStatus("Fetching build index ...");
                List<string> folders = HtmlParser.ParseFolders(Net.FetchText(baseUrl));

                int localMajor = _localParts.Length > 0 ? _localParts[0] : 0;

                List<string> sameBranch = new List<string>();
                foreach (string b in folders)
                    if (localMajor == 0 || int.Parse(b.Split('.')[0]) == localMajor)
                        sameBranch.Add(b);

                List<string> complete = new List<string>();
                foreach (string folder in sameBranch)
                {
                    if (Net.RemoteFileExists(baseUrl + folder + "/non_complete"))
                    { Logger.Log("Skipping " + folder + ": upload in progress."); continue; }
                    complete.Add(folder);
                }

                _builds  = complete;
                _baseUrl = baseUrl;

                int n = complete.Count;
                BeginInvoke(new Action(delegate()
                {
                    _buildList.Items.Clear();
                    for (int i = complete.Count - 1; i >= 0; i--)
                        _buildList.Items.Add(complete[i]);
                    SetStatus(string.Format("Found {0} build{1} for branch {2}.",
                        n, n != 1 ? "s" : "", localMajor));
                }));
            }
            catch (Exception ex)
            {
                Logger.Log("Failed to fetch builds: " + ex.Message);
                SetStatus("Error: " + ex.Message);
            }
            finally { SetBusy(false); }
        }

        // ── Install ──────────────────────────────────────────────────────────
        void OnInstall()
        {
            if (_busy) return;
            string chosen = (_selectedBuild ?? "").Trim();
            if (string.IsNullOrEmpty(chosen)) chosen = null;
            bool dryRun = AppConfig.DryRun;

            string msg = chosen != null
                ? "Install build " + chosen + "?"
                : "Install the latest available build?";
            if (dryRun)
                msg += "\n\n(Dry run – files will be downloaded but NOT installed.)";
            if (ThemedMessageBox.Show(this, msg, "Confirm update",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            int[]  localParts  = (int[])_localParts.Clone();
            string chosenBuild = chosen;
            ThreadPool.QueueUserWorkItem(delegate(object state)
                { InstallWorker(chosenBuild, localParts, dryRun); });
        }

        void InstallWorker(string chosenBuild, int[] localParts, bool dryRun)
        {
            SetBusy(true);
            string arch       = RegistryHelper.GetArch();
            string baseUrl    = _baseUrl.Length > 0 ? _baseUrl : AppConfig.BaseUrl;
            int    localMajor = localParts.Length > 0 ? localParts[0] : 0;

            try
            {
                List<string> sameBranch = new List<string>();
                foreach (string b in _builds)
                    if (int.Parse(b.Split('.')[0]) == localMajor)
                        sameBranch.Add(b);
                if (sameBranch.Count == 0)
                    throw new Exception("No complete builds for branch " + localMajor);

                string best;
                int[]  bestParts;

                if (chosenBuild != null)
                {
                    if (!sameBranch.Contains(chosenBuild))
                        throw new Exception(
                            "Build " + chosenBuild + " not available on server.");
                    best = chosenBuild;
                    bestParts = VersionCompare.Parse(best);
                }
                else
                {
                    best = null;
                    if (localMajor == 26100)
                    {
                        string baseline = "26100.1742";
                        int[]  bparts   = VersionCompare.Parse(baseline);
                        if (localParts.Length > 1 && localParts[1] < bparts[1])
                        {
                            if (!sameBranch.Contains(baseline))
                                throw new Exception(
                                    "Required baseline " + baseline + " not on server.");
                            best = baseline; bestParts = bparts;
                            Logger.Log("Forcing baseline build " + baseline);
                        }
                    }
                    if (best == null)
                    {
                        best = sameBranch[0];
                        foreach (string b in sameBranch)
                            if (VersionCompare.Compare(
                                    VersionCompare.Parse(b),
                                    VersionCompare.Parse(best)) > 0)
                                best = b;
                        bestParts = VersionCompare.Parse(best);
                    }
                    else bestParts = VersionCompare.Parse(best);
                }

                Logger.Log("Target build: " + best);
                SetStatus("Preparing " + best + " ...");

                int cmp = VersionCompare.Compare(bestParts, localParts);
                if (cmp < 0 && chosenBuild == null)
                {
                    Logger.Log("Local build is newer – nothing to do.");
                    Invoke(new Action(delegate()
                    {
                        ThemedMessageBox.Show(this,
                            "Your local build is already newer than the server.",
                            "Up to date", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                    return;
                }
                if (cmp == 0)
                {
                    bool reinstall = false;
                    Invoke(new Action(delegate()
                    {
                        reinstall = ThemedMessageBox.Show(this,
                            "Local build equals selected build. Reinstall anyway?",
                            "Reinstall?",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                            == DialogResult.Yes;
                    }));
                    if (!reinstall) return;
                }

                string folderUrl = baseUrl + best + "/" + arch + "/";
                Logger.Log("File list: " + folderUrl);
                SetStatus("Fetching file list ...");
                List<string> files = HtmlParser.ParseFiles(Net.FetchText(folderUrl));

                string selMsu = null, selNdp = null, selCab = null, selEsd = null;
                foreach (string f in files)
                {
                    if (selMsu == null && Regex.IsMatch(f, @"(?i)\.msu$"))
                        selMsu = f;
                    if (selNdp == null && Regex.IsMatch(f, @"(?i)NDP.*\.cab$|.*-NDP.*\.cab$"))
                        selNdp = f;
                    if (selCab == null && Regex.IsMatch(f, @"(?i)\bssu.*\.cab$"))
                        selCab = f;
                    if (selEsd == null && Regex.IsMatch(f, @"(?i)\b(?:windows|kb).*\.esd$"))
                        selEsd = f;
                }

                Directory.CreateDirectory(AppConfig.ProgramDataDir);

                Func<string, string> Dl = delegate(string fname)
                {
                    string url = folderUrl + Uri.EscapeDataString(fname);
                    SetStatus("Downloading " + fname + " ...");
                    BeginInvoke(new Action(delegate() { _dlBar.Value = 0; }));
                    return Net.DownloadFile(url, AppConfig.ProgramDataDir,
                        delegate(long d, long t, double sp)
                            { UpdateDlProgress(d, t, sp); });
                };

                bool[] rebootNeeded = new bool[] { false };

                Func<string, int> InstallPkg = delegate(string path)
                {
                    if (dryRun) { Logger.Log("[DRY RUN] Would install: " + path); return 0; }
                    string pkg = Path.GetFileName(path);
                    SetStatus("Installing " + pkg + " ...");
                    int rc = DismHelper.AddPackage(path,
                        delegate(string pct)
                            { SetStatus("Installing " + pkg + ": " + pct); });
                    if (DismRc.NeedsReboot(rc)) rebootNeeded[0] = true;
                    return rc;
                };

                if (selMsu != null)
                {
                    // The folder also carries the standalone SSU cab; install it
                    // first (same order as the ESD path) so the servicing stack
                    // is current before the MSU is applied.
                    string ssuPath = selCab != null ? Dl(selCab) : null;
                    string msuPath = Dl(selMsu);
                    string ndpPath = selNdp != null ? Dl(selNdp) : null;
                    if (ssuPath != null)
                    {
                        int rcS = InstallPkg(ssuPath);
                        if (!DismRc.IsSuccess(rcS)) throw new Exception("SSU install failed (rc=" + rcS + ")");
                        if (!dryRun)
                        {
                            SetStatus("SSU installed – waiting for servicing stack to settle ...");
                            Logger.Log("Waiting 5s for servicing stack to settle ...");
                            Thread.Sleep(5000);
                            SetStatus("Refreshing DISM package state ...");
                            DismHelper.RefreshPackageState(delegate(string s2) { SetStatus(s2); });
                        }
                    }
                    else Logger.Log("No standalone SSU cab in this folder - installing the MSU alone.");
                    Logger.Log("Installing MSU (dism expands the package first; this can take a long time) ...");
                    int rc = InstallPkg(msuPath);
                    if (!DismRc.IsSuccess(rc)) throw new Exception("MSU install failed (rc=" + rc + ")");
                    if (ndpPath != null) InstallPkg(ndpPath);
                    // A cumulative update from an MSU only finishes applying
                    // during the next boot, whatever exit code dism returned.
                    rebootNeeded[0] = true;
                }
                else
                {
                    if (selCab == null || selEsd == null)
                        throw new Exception("Missing CAB or ESD on server.");
                    string cabPath = Dl(selCab);
                    string esdPath = Dl(selEsd);
                    string ndpPath = selNdp != null ? Dl(selNdp) : null;

                    int rc1 = InstallPkg(cabPath);
                    if (!DismRc.IsSuccess(rc1)) throw new Exception("CAB install failed (rc=" + rc1 + ")");
                    if (!dryRun)
                    {
                        // Brief pause then trigger a DISM package enumeration so
                        // the servicing stack registers the new SSU before we feed
                        // it the ESD. Show status so the user knows we are not frozen.
                        SetStatus("SSU installed – waiting for servicing stack to settle ...");
                        Logger.Log("Waiting 5s for servicing stack to settle ...");
                        Thread.Sleep(5000);
                        SetStatus("Refreshing DISM package state ...");
                        DismHelper.RefreshPackageState(delegate(string s) { SetStatus(s); });
                    }
                    Logger.Log("Starting ESD package installation (this may take 10-30 minutes) ...");
                    SetStatus("Installing ESD – this may take 10-30 minutes, please wait ...");
                    int rc2 = InstallPkg(esdPath);
                    if (!DismRc.IsSuccess(rc2)) throw new Exception("ESD install failed (rc=" + rc2 + ")");
                    if (ndpPath != null) InstallPkg(ndpPath);
                }

                // Verified on the next start (see PendingInstall).
                if (!dryRun) PendingInstall.Record(best);

                string completionMsg = rebootNeeded[0]
                    ? "Update installed successfully. A reboot is required."
                    : "Update installed successfully.";
                Logger.Log(completionMsg);
                SetStatus(rebootNeeded[0]
                    ? "Update complete – reboot required."
                    : "Update complete.");
                Invoke(new Action(delegate()
                {
                    ThemedMessageBox.Show(this,
                        rebootNeeded[0]
                            ? "Update installed successfully.\nPlease reboot when ready."
                            : "Update installed successfully.",
                        "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }));

                int lm2 = localMajor;
                EnablementPackage.CheckAndOffer(
                    lm2, localParts, arch, dryRun,
                    delegate(string q)
                    {
                        bool ans = false;
                        Invoke(new Action(delegate()
                        {
                            ans = ThemedMessageBox.Show(this, q, "Enablement Package",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                                == DialogResult.Yes;
                        }));
                        return ans;
                    },
                    null);
            }
            catch (Exception ex)
            {
                Logger.Log("Install error: " + ex.Message);
                string err = ex.Message;
                Invoke(new Action(delegate()
                {
                    ThemedMessageBox.Show(this, err, "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }));
                SetStatus("Error: " + err);
            }
            finally { SetBusy(false); }
        }

        // ── Enablement Package ────────────────────────────────────────────────
        void OnEP()
        {
            if (_busy) return;
            int    lm         = _localParts.Length > 0 ? _localParts[0] : 0;
            int[]  localParts = (int[])_localParts.Clone();
            string arch       = RegistryHelper.GetArch();
            bool   dryRun     = AppConfig.DryRun;

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                SetBusy(true);
                try
                {
                    EnablementPackage.CheckAndOffer(lm, localParts, arch, dryRun,
                        delegate(string q)
                        {
                            // ThemedMessageBox creates real WinForms controls,
                            // unlike the native MessageBox this replaced -
                            // must be marshaled to the UI thread (this runs
                            // on a ThreadPool worker thread).
                            bool ans = false;
                            Invoke(new Action(delegate()
                            {
                                ans = ThemedMessageBox.Show(this, q, "Enablement Package",
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                                    == DialogResult.Yes;
                            }));
                            return ans;
                        },
                        delegate(string name)
                        {
                            Invoke(new Action(delegate()
                            {
                                ThemedMessageBox.Show(this,
                                    "Already on " + name +
                                    " – Enablement Package is already installed.",
                                    "Enablement Package",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }));
                        });
                }
                finally { SetBusy(false); }
            });
        }

        // ── DISM ResetBase ────────────────────────────────────────────────────
        void OnDismResetBase()
        {
            if (_busy) return;
            if (ThemedMessageBox.Show(this,
                    "Run:\n  dism /online /cleanup-image\n" +
                    "       /startcomponentcleanup /resetbase\n\n" +
                    "This removes superseded components and cannot be undone.\n" +
                    "The operation may take several minutes.\n\nProceed?",
                    "DISM ResetBase",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                SetBusy(true);
                SetStatus("Running DISM ResetBase ...");
                BeginInvoke(new Action(delegate() { _tabs.SelectedTab = _tabLog; }));
                try
                {
                    int rc = DismHelper.ResetBase(
                        delegate(string line)
                        {
                            SetStatus(line.Length > 80 ? line.Substring(0, 80) : line);
                        },
                        delegate(string pct) { SetStatus("DISM ResetBase: " + pct); });

                    SetStatus("DISM finished.");
                    if (rc == 0)
                    {
                        Logger.Log("DISM ResetBase completed successfully.");
                        Invoke(new Action(delegate()
                        {
                            ThemedMessageBox.Show(this,
                                "Component store cleanup completed successfully.",
                                "DISM ResetBase",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }));
                    }
                    else
                    {
                        Logger.Log("DISM ResetBase exit code: " + rc);
                        Invoke(new Action(delegate()
                        {
                            ThemedMessageBox.Show(this,
                                "DISM exited with code " + rc +
                                ".\nCheck the Log tab for details.",
                                "DISM ResetBase",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("DISM ResetBase error: " + ex.Message);
                    string err = ex.Message;
                    Invoke(new Action(delegate()
                    {
                        ThemedMessageBox.Show(this, err, "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
                finally { SetStatus("Ready"); SetBusy(false); }
            });
        }

        // ── Reboot ────────────────────────────────────────────────────────────
        void OnReboot()
        {
            if (ThemedMessageBox.Show(this,
                    "Reboot now?\n\nYou have 5 seconds to cancel with:\n  shutdown /a",
                    "Reboot",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            Logger.Log("Initiating reboot ...");
            ProcessStartInfo psi = new ProcessStartInfo("shutdown", "/r /t 5");
            psi.CreateNoWindow = true; psi.UseShellExecute = false;
            Process.Start(psi);
        }

        // ── Cleanup ──────────────────────────────────────────────────────────
        void OnCleanup()
        {
            HashSet<string> pkgExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pkgExts.Add(".cab"); pkgExts.Add(".esd"); pkgExts.Add(".msu");

            List<string> candidates = new List<string>();
            foreach (string f in Directory.GetFiles(AppConfig.ProgramDataDir))
                if (pkgExts.Contains(Path.GetExtension(f)))
                    candidates.Add(Path.GetFileName(f));

            if (candidates.Count == 0)
            {
                ThemedMessageBox.Show(this, "No CAB / ESD / MSU files found.", "Clean up",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            StringBuilder sb = new StringBuilder();
            foreach (string c in candidates) sb.AppendLine("  " + c);

            if (ThemedMessageBox.Show(this,
                    "Remove these files from " + AppConfig.ProgramDataDir +
                    "?\n\n" + sb,
                    "Clean up CAB / ESD files",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            int removed = 0, errors = 0;
            foreach (string fname in candidates)
            {
                string path = Path.Combine(AppConfig.ProgramDataDir, fname);
                try { File.Delete(path); Logger.Log("Removed: " + path); removed++; }
                catch (Exception ex)
                { Logger.Log("Failed to remove " + path + ": " + ex.Message); errors++; }
            }
            ThemedMessageBox.Show(this,
                "Removed " + removed + " file(s)." +
                (errors > 0 ? "\n" + errors + " error(s) – see Log tab." : ""),
                "Clean up complete",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
