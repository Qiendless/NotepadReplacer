using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NotepadReplacer
{
    public class Program
    {
        const string IFEO_KEY = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\notepad.exe";
        const string SEP = "  ->  ";
        static readonly RegistryView[] RegViews = { RegistryView.Registry64, RegistryView.Registry32 };

        static string ConfigFile { get { return Path.ChangeExtension(Application.ExecutablePath, ".cfg"); } }
        static string AppName { get { return Path.GetFileNameWithoutExtension(Application.ExecutablePath); } }

        static string _cachedEditor = "";
        static List<string> _cachedHistory = new List<string>();
        static bool _configLoaded;

        // --- Cached GDI resources (avoid per-paint allocation) ---
        static readonly Font FntTitle    = new Font("Segoe UI", 14, FontStyle.Bold);
        static readonly Font FntSub9     = new Font("Segoe UI", 9f);
        static readonly Font FntItemName = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        static readonly Font FntItemPath = new Font("Segoe UI", 8.5f);
        static readonly Font FntInitial  = new Font("Segoe UI", 13, FontStyle.Bold);
        static readonly Font FntTipTitle = new Font("Segoe UI", 10f, FontStyle.Bold);
        static readonly Font FntTipBody  = new Font("Segoe UI", 9f);
        static readonly StringFormat SfCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        static readonly StringFormat SfTrim   = new StringFormat { Trimming = StringTrimming.EllipsisCharacter };

        // --- Colors ---
        static readonly Color CAccent   = Color.FromArgb(0x25, 0x63, 0xEB);
        static readonly Color CAccentHi = Color.FromArgb(0x1D, 0x4E, 0xD8);
        static readonly Color CAccentBg = Color.FromArgb(0xEE, 0xF2, 0xFF);
        static readonly Color CText     = Color.FromArgb(0x11, 0x18, 0x27);
        static readonly Color CSub      = Color.FromArgb(0x6B, 0x72, 0x80);
        static readonly Color CGreen    = Color.FromArgb(0x16, 0xA3, 0x4A);
        static readonly Color CGreenBg  = Color.FromArgb(0xDC, 0xFC, 0xE7);
        static readonly Color CGrayBg   = Color.FromArgb(0xF3, 0xF4, 0xF6);
        static readonly Color CAmber    = Color.FromArgb(0xD9, 0x77, 0x06);
        static readonly Color CAmberBg  = Color.FromArgb(0xFE, 0xF3, 0xC7);
        static readonly Color CHdrEnd   = Color.FromArgb(0x4F, 0x46, 0xE5);
        static readonly Color CHdrSub   = Color.FromArgb(0xE0, 0xE7, 0xFF);
        static readonly Color CAltRow   = Color.FromArgb(0xF8, 0xFA, 0xFC);
        static readonly Color CDkGreen  = Color.FromArgb(0x14, 0x5A, 0x32);
        static readonly Color CDkAmber  = Color.FromArgb(0x92, 0x40, 0x06);
        static readonly Color CGrayText = Color.FromArgb(0x37, 0x41, 0x51);
        static readonly Color CRed      = Color.FromArgb(0xB9, 0x1C, 0x1C);
        static readonly Color CRedBg    = Color.FromArgb(0xFE, 0xE2, 0xE2);
        static readonly Color CTipBody  = Color.FromArgb(0x2B, 0x34, 0x44);

        const string DEV_NAME  = "godsq";
        const string DEV_EMAIL = "godsq@qq.com";
        const string LICENSE   = "MIT License";

        const string ELEV_ERR_FMT =
            "提权未成功，{0}。\n\n可能原因：\n• UAC 提权被取消；\n• 被安全软件 / 杀毒软件 / Windows Defender 拦截了提权或本程序。\n\n请关闭拦截或将本程序加入白名单后重试。\n\n技术信息：";
        const string PERM_ERR_FMT =
            "写入注册表被拒绝，{0}。\n\n常见原因：\n• 未以管理员身份运行；\n• 被安全软件 / 杀毒软件 / Windows Defender 拦截了对 IFEO 注册表的修改。\n\n请以管理员身份运行本程序或将本程序加入白名单后重试。";

        #region Entry Point

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0)
            {
                switch (args[0])
                {
                    case "/enable":  TryRun(DoEnable, "启用失败"); return;
                    case "/restore": TryRun(DoRestore, "恢复失败"); return;
                    default:         Launcher(args); return;
                }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        static void TryRun(Action action, string errPrefix)
        {
            try { action(); Environment.ExitCode = 0; }
            catch (Exception ex) { MessageBox.Show(errPrefix + "：" + ex.Message, "错误"); Environment.ExitCode = 1; }
        }

        #endregion

        #region Admin Check (P/Invoke)

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);
        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, int TokenInformationLength, out int ReturnLength);

        const uint TOKEN_QUERY = 0x0008;
        const int TokenElevation = 20;

        static bool IsAdmin()
        {
            try
            {
                IntPtr token;
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token))
                    return false;
                IntPtr ptr = Marshal.AllocHGlobal(4);
                try
                {
                    int len;
                    return GetTokenInformation(token, TokenElevation, ptr, 4, out len) && Marshal.ReadInt32(ptr) != 0;
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            catch { return false; }
        }

        #endregion

        #region Config Persistence

        static void EnsureConfigLoaded()
        {
            if (_configLoaded) return;
            _configLoaded = true;
            try
            {
                if (!File.Exists(ConfigFile)) { SaveConfig(); return; }
                var lines = File.ReadAllLines(ConfigFile);
                if (lines.Length >= 1 && !string.Equals(lines[0].Trim(), GetMachineId(), StringComparison.OrdinalIgnoreCase))
                { SaveConfig(); return; }
                if (lines.Length >= 2) _cachedEditor = lines[1].Trim();
                if (lines.Length >= 3 && !string.IsNullOrWhiteSpace(lines[2]))
                    foreach (var p in lines[2].Split('|'))
                    {
                        var t = p.Trim();
                        if (!string.IsNullOrEmpty(t) && File.Exists(t) &&
                            !_cachedHistory.Exists(x => x.Equals(t, StringComparison.OrdinalIgnoreCase)))
                            _cachedHistory.Add(t);
                    }
            }
            catch { }
        }

        static void SaveConfig()
        {
            try
            {
                File.WriteAllLines(ConfigFile,
                    new[] { GetMachineId(), _cachedEditor, string.Join("|", _cachedHistory) },
                    System.Text.Encoding.UTF8);
            }
            catch { }
        }

        static string EditorPath() { EnsureConfigLoaded(); return _cachedEditor; }
        static void SetEditorPath(string path) { EnsureConfigLoaded(); _cachedEditor = path; SaveConfig(); }
        static List<string> LoadEditorHistory() { EnsureConfigLoaded(); return new List<string>(_cachedHistory); }

        static void AddEditorHistory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            EnsureConfigLoaded();
            var cmp = path.Trim();
            if (_cachedHistory.Exists(p => p.Equals(cmp, StringComparison.OrdinalIgnoreCase))) return;
            _cachedHistory.Add(cmp);
            SaveConfig();
        }

        static void RemoveEditorHistory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            EnsureConfigLoaded();
            _cachedHistory.RemoveAll(p => p.Equals(path.Trim(), StringComparison.OrdinalIgnoreCase));
            SaveConfig();
        }

        static void ClearEditorHistory() { EnsureConfigLoaded(); _cachedHistory.Clear(); SaveConfig(); }

        static string GetMachineId()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                    if (k != null)
                    {
                        var guid = k.GetValue("MachineGuid") as string;
                        if (!string.IsNullOrEmpty(guid)) return guid;
                    }
            }
            catch { }
            return Environment.MachineName;
        }

        #endregion

        #region IFEO Registry

        static void SetDebuggerAllViews(string exe)
        {
            foreach (var view in RegViews)
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var k = baseKey.CreateSubKey(IFEO_KEY))
                    k.SetValue("Debugger", "\"" + exe + "\"", RegistryValueKind.String);
        }

        static string ReadDebuggerAnyView()
        {
            string dbg = "";
            foreach (var view in RegViews)
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var k = baseKey.OpenSubKey(IFEO_KEY))
                        if (k != null)
                        {
                            var v = k.GetValue("Debugger") as string;
                            if (!string.IsNullOrEmpty(v)) dbg = v;
                        }
                }
                catch { }
            }
            return dbg;
        }

        static bool IsReplacementActive()
        {
            return ReadDebuggerAnyView().IndexOf(AppName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static void DoEnable()
        {
            string editor = EditorPath();
            if (string.IsNullOrWhiteSpace(editor) || !File.Exists(editor))
                throw new Exception("未设置有效的编辑器路径，请先在主界面选择编辑器。");
            SetDebuggerAllViews(Application.ExecutablePath);
        }

        static void DoRestore()
        {
            foreach (var view in RegViews)
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var k = baseKey.OpenSubKey(IFEO_KEY, true))
                {
                    if (k == null) continue;
                    var dbg = k.GetValue("Debugger") as string;
                    if (dbg != null && dbg.IndexOf(AppName, StringComparison.OrdinalIgnoreCase) >= 0)
                        k.DeleteValue("Debugger", false);
                }
        }

        static void Launcher(string[] args)
        {
            string editor = EditorPath();
            var sb = new System.Text.StringBuilder();
            for (int i = 1; i < args.Length; i++)
                sb.Append(" \"").Append(args[i]).Append("\"");
            try
            {
                string target = !string.IsNullOrWhiteSpace(editor) && File.Exists(editor) ? editor : "notepad.exe";
                Process.Start(target, sb.ToString().Trim());
            }
            catch { }
        }

        #endregion

        #region Shared UI Helpers

        public static GraphicsPath RoundedRect(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.X + r.Width - d, r.Y, d, d, 270, 90);
            p.AddArc(r.X + r.Width - d, r.Y + r.Height - d, d, d, 0, 90);
            p.AddArc(r.X, r.Y + r.Height - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void PaintHeader(Graphics g, Rectangle rect, int iconY)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var br = new LinearGradientBrush(rect, CAccent, CHdrEnd, 65f))
                g.FillRectangle(br, rect);
            try
            {
                using (var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                    g.DrawImage(ic.ToBitmap(), new Rectangle(16, iconY, 38, 38));
            }
            catch { }
            using (var bWhite = new SolidBrush(Color.White))
                g.DrawString("记事本替换工具", FntTitle, bWhite, 64, iconY - 2);
            using (var bSub = new SolidBrush(CHdrSub))
                g.DrawString("用你喜欢的编辑器接管系统记事本", FntSub9, bSub, 64, iconY + 26);
        }

        public static void StyleButton(Button b, Color back, Color fore, Color hover, bool outline)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = outline ? 1 : 0;
            b.FlatAppearance.BorderColor = CAccent;
            b.FlatAppearance.MouseDownBackColor = hover;
            b.FlatAppearance.MouseOverBackColor = hover;
            b.BackColor = back;
            b.ForeColor = fore;
            b.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
            b.Padding = new Padding(0);
        }

        public static string GetItemName(string item)
        {
            int idx = item.IndexOf(SEP);
            return idx >= 0 ? item.Substring(0, idx) : item;
        }

        public static string GetItemPath(string item)
        {
            int idx = item.IndexOf(SEP);
            return idx >= 0 ? item.Substring(idx + SEP.Length) : "";
        }

        #endregion

        public class MainForm : Form
        {
            TextBox txtEditor;
            ListBox lstEditors;
            Label lblStatus1, lblStatus2;
            RichTextBox tip;
            LinkLabel lnkToggle, lnkAbout;
            Panel pnlHeader, pnlStatus, pnlFooter;
            Button btnBrowse, btnEnable, btnRestore, btnAdmin, btnDelete, btnClear;
            System.Windows.Forms.Timer tempStatusTimer;
            string currentEditorPath = "";

            public MainForm()
            {
                InitializeComponent();
                DetectEditors();
                LoadCustomEditors();
                RefreshStatus();
            }

            void InitializeComponent()
            {
                this.Text = AppName;
                this.ClientSize = new Size(600, 570);
                this.FormBorderStyle = FormBorderStyle.FixedSingle;
                this.StartPosition = FormStartPosition.CenterScreen;
                this.BackColor = Color.White;
                this.Font = new Font("Segoe UI", 9.5f);
                this.DoubleBuffered = true;
                this.MaximizeBox = false;
                try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

                // Header
                pnlHeader = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = CAccent };
                pnlHeader.Paint += (s, e) => PaintHeader(e.Graphics, pnlHeader.ClientRectangle, (pnlHeader.Height - 38) / 2);
                this.Controls.Add(pnlHeader);

                // Section label
                this.Controls.Add(new Label
                {
                    Left = 16, Top = 82, Width = 560,
                    Text = "选择要替换系统记事本的编辑器，单击列表项或点击“浏览”选择：",
                    ForeColor = CText, Font = new Font("Segoe UI", 9.5f)
                });

                // Editor path input
                txtEditor = new TextBox
                {
                    Left = 16, Top = 108, Width = 452, Height = 24,
                    Font = new Font("Segoe UI", 10f), ForeColor = CText,
                    BorderStyle = BorderStyle.FixedSingle
                };
                this.Controls.Add(txtEditor);

                btnBrowse = CreateBtn(476, 107, 96, 26, "浏览...", Color.White, CAccent, CAccentBg, true);
                btnBrowse.Click += (s, e) => Browse();
                this.Controls.Add(btnBrowse);

                // List label
                this.Controls.Add(new Label
                {
                    Left = 16, Top = 150, Width = 568,
                    Text = "已自动检测到的编辑器（单击选用 ● 为当前已启用）：",
                    ForeColor = CSub, Font = FntSub9
                });

                // Editor list
                lstEditors = new ListBox
                {
                    Left = 16, Top = 176, Width = 568, Height = 194,
                    DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 46,
                    Font = new Font("Segoe UI", 10f),
                    BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White
                };
                lstEditors.Click += (s, e) => PickFromList();
                lstEditors.DrawItem += LstDraw;
                this.Controls.Add(lstEditors);

                // List action buttons
                btnDelete = CreateBtn(16, 380, 120, 28, "删除选中", Color.White, CRed, CRedBg, true);
                btnDelete.Click += (s, e) => DeleteSelected();
                this.Controls.Add(btnDelete);

                btnClear = CreateBtn(144, 380, 120, 28, "清空历史", Color.White, CSub, CGrayBg, true);
                btnClear.Click += (s, e) => ClearHistory();
                this.Controls.Add(btnClear);

                // Main action buttons
                btnEnable = CreateBtn(16, 416, 180, 36, "启用替换", CAccent, Color.White, CAccentHi, false);
                btnEnable.Click += (s, e) => Enable();
                this.Controls.Add(btnEnable);

                btnRestore = CreateBtn(206, 416, 184, 36, "恢复默认记事本", Color.White, CText, CGrayBg, true);
                btnRestore.Click += (s, e) => Restore();
                this.Controls.Add(btnRestore);

                btnAdmin = CreateBtn(396, 416, 188, 36, "以管理员身份运行", Color.White, CAmber, CAmberBg, true);
                btnAdmin.Click += (s, e) => RunAsAdmin();
                this.Controls.Add(btnAdmin);

                // Toggle tip
                lnkToggle = new LinkLabel
                {
                    Left = 16, Top = 458, Width = 560, Height = 20,
                    Text = "展开 中转原理 / 使用注意 ▼",
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                    LinkColor = CAccent, ActiveLinkColor = CAccentHi,
                    BackColor = Color.Transparent
                };
                lnkToggle.LinkClicked += (s, e) => ToggleTip();
                this.Controls.Add(lnkToggle);

                // Tip panel
                tip = new RichTextBox
                {
                    Left = 16, Top = 482, Width = 568, Height = 192,
                    ReadOnly = true, ScrollBars = RichTextBoxScrollBars.Vertical,
                    BackColor = CGrayBg, BorderStyle = BorderStyle.FixedSingle,
                    Font = FntTipBody, DetectUrls = false,
                    Margin = new Padding(0), Padding = new Padding(6),
                    Visible = false
                };
                this.Controls.Add(tip);
                FillTip(tip);

                // Status panel
                pnlStatus = new Panel { Left = 16, Top = 484, Width = 568, Height = 50 };
                lblStatus1 = MakeLabel(8, 6, 548, 18, CText);
                lblStatus2 = MakeLabel(8, 26, 548, 18, CSub);
                pnlStatus.Controls.Add(lblStatus1);
                pnlStatus.Controls.Add(lblStatus2);
                this.Controls.Add(pnlStatus);

                // Footer
                pnlFooter = new Panel { Left = 0, Top = 538, Width = 600, Height = 32, BackColor = CAccentBg };
                pnlFooter.Controls.Add(MakeLabel(16, 7, 250, 18, CSub,
                    "© 2026 " + DEV_NAME + " · 基于 " + LICENSE + " 开源"));
                pnlFooter.Controls.Add(MakeLabel(270, 7, 150, 18, CSub, DEV_EMAIL,
                    ContentAlignment.MiddleLeft));
                lnkAbout = new LinkLabel
                {
                    Left = 424, Top = 7, Width = 160, Height = 18,
                    Text = "查看开源声明 ▸", Font = new Font("Segoe UI", 9f, FontStyle.Underline),
                    LinkColor = CAccent, ActiveLinkColor = CAccentHi, BackColor = Color.Transparent,
                    TextAlign = ContentAlignment.MiddleRight
                };
                lnkAbout.LinkClicked += (s, e) => ShowAbout();
                pnlFooter.Controls.Add(lnkAbout);
                this.Controls.Add(pnlFooter);
            }

            Button CreateBtn(int x, int y, int w, int h, string text, Color back, Color fore, Color hover, bool outline)
            {
                var b = new Button { Left = x, Top = y, Width = w, Height = h, Text = text };
                StyleButton(b, back, fore, hover, outline);
                return b;
            }

            static Label MakeLabel(int x, int y, int w, int h, Color fg)
            {
                return new Label
                {
                    Left = x, Top = y, Width = w, Height = h,
                    ForeColor = fg, BackColor = Color.Transparent,
                    Font = FntSub9, UseMnemonic = false, AutoEllipsis = true
                };
            }

            static Label MakeLabel(int x, int y, int w, int h, Color fg, string text)
            {
                var lbl = MakeLabel(x, y, w, h, fg);
                lbl.Text = text;
                return lbl;
            }

            static Label MakeLabel(int x, int y, int w, int h, Color fg, string text, ContentAlignment align)
            {
                var lbl = MakeLabel(x, y, w, h, fg, text);
                lbl.TextAlign = align;
                return lbl;
            }

            void LstDraw(object s, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                var r = e.Bounds;
                bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

                string item = lstEditors.Items[e.Index].ToString();
                string name = GetItemName(item);
                string path = GetItemPath(item);
                bool isCurrent = !string.IsNullOrEmpty(path) &&
                    path.Equals(currentEditorPath, StringComparison.OrdinalIgnoreCase);

                // Background
                using (var br = new SolidBrush(sel ? CAccentBg : (e.Index % 2 == 0 ? Color.White : CAltRow)))
                    g.FillRectangle(br, r);
                if (sel)
                    using (var pen = new Pen(CAccent, 2))
                        g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);

                // Icon square + initial
                var sq = new Rectangle(r.X + 10, r.Y + (r.Height - 32) / 2, 32, 32);
                using (var br2 = new SolidBrush(isCurrent ? CGreen : CAccent))
                    g.FillPath(br2, RoundedRect(sq, 8));
                using (var bWhite = new SolidBrush(Color.White))
                {
                    string ini = name.Length > 0 ? char.ToUpper(name[0]).ToString() : "?";
                    g.DrawString(ini, FntInitial, bWhite, new RectangleF(sq.X, sq.Y, sq.Width, sq.Height), SfCenter);
                }

                // Name + path
                using (var bName = new SolidBrush(isCurrent ? CGreen : CText))
                    g.DrawString(name, FntItemName, bName, sq.Right + 10, r.Y + 6);
                var pathRect = new RectangleF(sq.Right + 10, r.Y + 25, r.Width - sq.Right - 40, 18);
                using (var bp = new SolidBrush(CSub))
                    g.DrawString(path, FntItemPath, bp, pathRect, SfTrim);

                // Current marker
                if (isCurrent)
                {
                    var dot = new Rectangle(r.Right - 22, r.Y + (r.Height - 12) / 2, 12, 12);
                    using (var b = new SolidBrush(CGreen)) g.FillEllipse(b, dot);
                    using (var b2 = new SolidBrush(Color.White)) g.FillEllipse(b2, new Rectangle(dot.X + 4, dot.Y + 4, 4, 4));
                }
            }

            void Browse()
            {
                var d = new OpenFileDialog { Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*", Title = "选择编辑器" };
                if (d.ShowDialog() == DialogResult.OK)
                {
                    txtEditor.Text = d.FileName;
                    AddEditorHistory(d.FileName);
                    AddEditorToList(d.FileName);
                    lstEditors.Invalidate();
                }
            }

            void PickFromList()
            {
                if (lstEditors.SelectedItem == null) return;
                string path = GetItemPath(lstEditors.SelectedItem.ToString());
                if (!string.IsNullOrEmpty(path)) { txtEditor.Text = path.Trim(); lstEditors.Invalidate(); }
            }

            void DeleteSelected()
            {
                if (lstEditors.SelectedIndex < 0)
                { MessageBox.Show("请先在列表中选中要删除的编辑器。", "提示"); return; }
                string path = GetItemPath(lstEditors.SelectedItem.ToString());
                if (!string.IsNullOrEmpty(path)) RemoveEditorHistory(path);
                lstEditors.Items.RemoveAt(lstEditors.SelectedIndex);
                lstEditors.Invalidate();
            }

            void ClearHistory()
            {
                if (lstEditors.Items.Count == 0) return;
                if (MessageBox.Show("确定清空所有编辑器历史记录吗？\n自动检测到的编辑器不受影响，仍会保留。",
                    "确认清空", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                ClearEditorHistory();
                lstEditors.Items.Clear();
                DetectEditors();
                EnsureCurrentInList();
                lstEditors.Invalidate();
            }

            void Enable()
            {
                string path = txtEditor.Text.Trim();
                if (!File.Exists(path)) { MessageBox.Show("编辑器路径无效，请重新选择。", "提示"); return; }
                SetEditorPath(path);
                AddEditorHistory(path);
                AddEditorToList(path);
                RunWithElevation("/enable", DoEnable, "替换", "\u2713 已启用替换：记事本已重定向到你设置的编辑器。");
            }

            void Restore()
            {
                RunWithElevation("/restore", DoRestore, "恢复", "\u2713 已恢复默认记事本。");
            }

            /// <summary>
            /// Shared logic for Enable/Restore: run elevated if needed, show temp status on success.
            /// </summary>
            void RunWithElevation(string arg, Action action, string noun, string successMsg)
            {
                if (!IsAdmin())
                {
                    try
                    {
                        var p = Process.Start(new ProcessStartInfo(Application.ExecutablePath, arg)
                        { Verb = "runas", UseShellExecute = true });
                        p.WaitForExit();
                        if (p.ExitCode == 0) { ShowTempStatus(successMsg); return; }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(string.Format(ELEV_ERR_FMT, noun + "未执行") + ex.Message,
                            "提权失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                else
                {
                    try
                    {
                        action();
                        ShowTempStatus(successMsg);
                        return;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        MessageBox.Show(string.Format(PERM_ERR_FMT, noun + "未能完成"),
                            "权限不足 / 被拦截", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(noun + "失败：" + ex.Message + "\n\n若被安全软件拦截，请将本程序加入白名单后以管理员身份重试。",
                            "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                RefreshStatus();
            }

            void RunAsAdmin()
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Application.ExecutablePath, "")
                    { Verb = "runas", UseShellExecute = true });
                    Application.Exit();
                }
                catch (Exception ex) { MessageBox.Show("无法以管理员身份运行：" + ex.Message, "提示"); }
            }

            void ToggleTip()
            {
                bool expand = !tip.Visible;
                tip.Visible = expand;
                lnkToggle.Text = expand ? "收起 中转原理 / 使用注意 ▲" : "展开 中转原理 / 使用注意 ▼";
                this.ClientSize = new Size(600, expand ? 760 : 570);
                pnlStatus.Top = expand ? 678 : 484;
                pnlFooter.Top = expand ? 724 : 538;
            }

            void ShowTempStatus(string message)
            {
                if (tempStatusTimer == null)
                {
                    tempStatusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
                    tempStatusTimer.Tick += (s, e) => { tempStatusTimer.Stop(); RefreshStatus(); };
                }
                pnlStatus.BackColor = CGreenBg;
                lblStatus1.ForeColor = CDkGreen;
                lblStatus1.Text = message;
                lblStatus2.Text = "";
                tempStatusTimer.Start();
            }

            void RefreshStatus()
            {
                bool elevated = IsAdmin();
                btnAdmin.Visible = !elevated;
                bool active = IsReplacementActive();
                string editor = EditorPath();
                string editorName = string.IsNullOrEmpty(editor) ? "(未记录)" : Path.GetFileName(editor);
                currentEditorPath = active ? editor : "";
                this.Text = AppName + " · " + (active ? "已启用替换" : "未启用替换") + " · " + editorName;
                lstEditors.Invalidate();

                Color bg, fg1, fg2;
                string t1, t2;
                if (active && elevated)
                {
                    bg = CGreenBg; fg1 = CDkGreen; fg2 = CAccent;
                    t1 = "已启用替换　当前编辑器：" + editorName + "　·　" + editor;
                    t2 = "已以管理员身份运行，可正常启用 / 恢复。";
                }
                else if (active)
                {
                    bg = CAmberBg; fg1 = CDkGreen; fg2 = CAmber;
                    t1 = "已启用替换　当前编辑器：" + editorName + "　·　" + editor;
                    t2 = "当前非管理员，恢复 / 修改时会请求 UAC 提权（或点「以管理员身份运行」）。";
                }
                else if (elevated)
                {
                    bg = CGrayBg; fg1 = CGrayText; fg2 = CAccent;
                    t1 = "未启用　目前使用系统默认记事本";
                    t2 = "已以管理员身份运行，点击「启用替换」即可生效。";
                }
                else
                {
                    bg = CAmberBg; fg1 = CDkAmber; fg2 = CAmber;
                    t1 = "未启用　目前使用系统默认记事本";
                    t2 = "当前非管理员：点击「启用替换」会请求 UAC 提权（或点「以管理员身份运行」）。";
                }
                pnlStatus.BackColor = bg;
                lblStatus1.ForeColor = fg1;
                lblStatus1.Text = t1;
                lblStatus2.ForeColor = fg2;
                lblStatus2.Text = t2;
            }

            void EnsureCurrentInList() { AddEditorToList(EditorPath()); }

            void LoadCustomEditors()
            {
                foreach (var p in LoadEditorHistory())
                    AddEditorToList(p);
            }

            void AddEditorToList(string editor)
            {
                if (string.IsNullOrWhiteSpace(editor) || !File.Exists(editor)) return;
                string item = Path.GetFileNameWithoutExtension(editor) + SEP + editor;
                foreach (var o in lstEditors.Items)
                    if (string.Equals(o.ToString(), item, StringComparison.OrdinalIgnoreCase)) return;
                lstEditors.Items.Add(item);
            }

            void ShowAbout() { using (var dlg = new AboutForm()) dlg.ShowDialog(this); }

            static void AppendTip(RichTextBox rt, Font font, Color color, string text)
            {
                rt.SelectionFont = font;
                rt.SelectionColor = color;
                rt.AppendText(text);
            }

            void FillTip(RichTextBox rt)
            {
                rt.Clear();
                AppendTip(rt, FntTipTitle, CText, "【中转原理】\n");
                AppendTip(rt, FntTipBody, CTipBody,
                    "本工具利用 Windows 映像劫持(IFEO)：把 notepad.exe 的“调试器”指向本程序。\n" +
                    "当任意程序或双击打开 .txt 时，系统实际启动的是本工具，再由本工具调用你设置的编辑器打开该文件。\n" +
                    "不修改任何系统文件，仅写入注册表，可随时一键恢复。\n\n");
                AppendTip(rt, FntTipTitle, CText, "【使用注意】\n");
                AppendTip(rt, FntTipBody, CTipBody,
                    "① 写入 IFEO 需管理员权限：点“启用替换”会请求 UAC 提权，或右键本程序“以管理员身份运行”。\n\n" +
                    "② 本程序必须保留在当前路径；删除前请先点“恢复默认记事本”解除，否则系统将无法打开记事本。\n\n" +
                    "③ 仅劫持 notepad.exe，不影响其他软件；异常时点“恢复默认记事本”即可立即还原。\n\n" +
                    "④ 注册表：HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\notepad.exe");
                rt.SelectionStart = 0;
                rt.SelectionLength = 0;
            }

            void DetectEditors()
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                Add(pf + @"\Notepad++\notepad++.exe", pf86 + @"\Notepad++\notepad++.exe");
                Add(local + @"\Programs\Microsoft VS Code\Code.exe",
                    pf + @"\Microsoft VS Code\Code.exe",
                    pf86 + @"\Microsoft VS Code\Code.exe",
                    local + @"\Programs\Microsoft VS Code Insiders\Code - Insiders.exe");
                Add(pf + @"\Sublime Text\sublime_text.exe", pf86 + @"\Sublime Text\sublime_text.exe",
                    pf + @"\Sublime Text 3\sublime_text.exe", pf86 + @"\Sublime Text 3\sublime_text.exe");
                Add(pf + @"\Vim\vim91\gvim.exe", pf + @"\Vim\vim90\gvim.exe",
                    pf + @"\Vim\vim82\gvim.exe", pf + @"\Vim\vim81\gvim.exe",
                    pf + @"\Vim\vim80\gvim.exe", pf + @"\Vim\vim74\gvim.exe");
                Add(pf + @"\Neovim\bin\nvim-qt.exe", local + @"\Programs\Neovim\bin\nvim-qt.exe");
                Add(pf + @"\EmEditor\emeditor.exe", pf86 + @"\EmEditor\emeditor.exe");
                Add(pf + @"\Notepad3\Notepad3.exe", pf86 + @"\Notepad3\Notepad3.exe");
                Add(pf + @"\Notepad2\Notepad2.exe", pf86 + @"\Notepad2\Notepad2.exe");
                Add(pf + @"\Atom\atom.exe", local + @"\atom\atom.exe");
                Add(pf + @"\Brackets\Brackets.exe", local + @"\Programs\Brackets\Brackets.exe");
                Add(pf + @"\IDM Computer Solutions\UltraEdit\uedit64.exe",
                    pf + @"\UltraEdit\uedit64.exe",
                    pf86 + @"\IDM Computer Solutions\UltraEdit\uedit32.exe");
                Add(pf + @"\EditPlus\editplus.exe", pf86 + @"\EditPlus\editplus.exe");
                Add(pf + @"\PSPad editor\PSPad.exe");
                Add(pf + @"\TextPad 8\TextPad.exe", pf + @"\TextPad 7\TextPad.exe", pf + @"\TextPad 6\TextPad.exe");
                Add(pf + @"\Crimson Editor\cedt.exe");
                Add(pf + @"\SciTE\SciTE.exe", pf86 + @"\SciTE\SciTE.exe");
                Add(pf + @"\CudaText\cudatext.exe", pf86 + @"\CudaText\cudatext.exe");
                Add(pf + @"\RJ TextEd\RJ.exe");
                Add(local + @"\Programs\Zed\Zed.exe");
                Add(local + @"\Programs\Cursor\Cursor.exe");
                Add(local + @"\Programs\JetBrains Fleet\fleet.exe", pf + @"\JetBrains\Fleet\fleet.exe");
                Add(pf + @"\Kate\bin\kate.exe");
                Add(pf + @"\Emacs\bin\runemacs.exe", pf86 + @"\Emacs\bin\runemacs.exe");
                Add(pf + @"\Geany\bin\geany.exe");
                Add(pf + @"\NotepadNext\NotepadNext.exe",
                    local + @"\Programs\Notepad Next\Notepad Next.exe",
                    pf86 + @"\NotepadNext\NotepadNext.exe");
                Add(pf + @"\Typora\Typora.exe", local + @"\Programs\Typora\Typora.exe");
                Add(local + @"\Programs\Obsidian\Obsidian.exe");
            }

            void Add(params string[] paths)
            {
                foreach (var p in paths)
                    if (File.Exists(p))
                    {
                        lstEditors.Items.Add(Path.GetFileNameWithoutExtension(p) + SEP + p);
                        return;
                    }
            }

            const string MIT_TEXT =
                "MIT License\n\n" +
                "Copyright (c) 2026 godsq\n\n" +
                "Permission is hereby granted, free of charge, to any person obtaining a copy\n" +
                "of this software and associated documentation files (the \"Software\"), to deal\n" +
                "in the Software without restriction, including without limitation the rights\n" +
                "to use, copy, modify, merge, publish, distribute, sublicense, and/or sell\n" +
                "copies of the Software, and to permit persons to whom the Software is\n" +
                "furnished to do so, subject to the following conditions:\n\n" +
                "The above copyright notice and this permission notice shall be included in all\n" +
                "copies or substantial portions of the Software.\n\n" +
                "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR\n" +
                "IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,\n" +
                "FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE\n" +
                "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER\n" +
                "LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,\n" +
                "OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE\n" +
                "SOFTWARE.";

            public class AboutForm : Form
            {
                public AboutForm()
                {
                    this.Text = "About " + AppName;
                    this.ClientSize = new Size(560, 460);
                    this.FormBorderStyle = FormBorderStyle.FixedSingle;
                    this.StartPosition = FormStartPosition.CenterParent;
                    this.BackColor = Color.White;
                    this.Font = new Font("Segoe UI", 9.5f);
                    this.DoubleBuffered = true;
                    this.MaximizeBox = false;
                    this.MinimizeBox = false;
                    try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

                    var pnl = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = CAccent };
                    pnl.Paint += (s, e) => PaintHeader(e.Graphics, pnl.ClientRectangle, (pnl.Height - 38) / 2);
                    this.Controls.Add(pnl);

                    this.Controls.Add(new Label
                    {
                        Left = 18, Top = 90, Width = 524, Height = 56,
                        Text = "Developer: " + DEV_NAME + " | License: " + LICENSE + "\nEmail: " + DEV_EMAIL,
                        ForeColor = CText, Font = new Font("Segoe UI", 10f)
                    });

                    var lic = new TextBox
                    {
                        Left = 18, Top = 152, Width = 524, Height = 232,
                        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                        Font = new Font("Consolas", 9f), ForeColor = CText, BackColor = CAltRow,
                        BorderStyle = BorderStyle.FixedSingle
                    };
                    lic.Text = MIT_TEXT;
                    this.Controls.Add(lic);

                    var btnCopy = new Button { Left = 18, Top = 396, Width = 140, Height = 32, Text = "Copy License" };
                    StyleButton(btnCopy, CAccent, Color.White, CAccentHi, false);
                    btnCopy.Click += (s, e) => { try { Clipboard.SetText(MIT_TEXT); MessageBox.Show("Copied to clipboard.", "Notice"); } catch { } };
                    this.Controls.Add(btnCopy);

                    var btnClose = new Button { Left = 402, Top = 396, Width = 140, Height = 32, Text = "Close" };
                    StyleButton(btnClose, Color.White, CText, CGrayBg, true);
                    btnClose.Click += (s, e) => this.Close();
                    this.Controls.Add(btnClose);
                }
            }
        }
    }
}
