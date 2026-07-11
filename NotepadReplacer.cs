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
        static string ConfigFile { get { return Path.ChangeExtension(Application.ExecutablePath, ".cfg"); } }
        static string AppName { get { return Path.GetFileNameWithoutExtension(Application.ExecutablePath); } }
        static string _cachedEditor = null;
        static List<string> _cachedHistory = null;

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "/enable")
            {
                try { DoEnable(); Environment.ExitCode = 0; }
                catch (Exception ex) { MessageBox.Show("启用失败：" + ex.Message, "错误"); Environment.ExitCode = 1; }
                return;
            }
            if (args.Length > 0 && args[0] == "/restore")
            {
                try { DoRestore(); Environment.ExitCode = 0; }
                catch (Exception ex) { MessageBox.Show("恢复失败：" + ex.Message, "错误"); Environment.ExitCode = 1; }
                return;
            }
            if (args.Length > 0) { Launcher(args); return; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

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
                    if (!GetTokenInformation(token, TokenElevation, ptr, 4, out len))
                        return false;
                    return Marshal.ReadInt32(ptr) != 0;
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            catch { return false; }
        }

        static void EnsureConfigLoaded()
        {
            if (_cachedEditor != null) return;
            _cachedEditor = "";
            _cachedHistory = new List<string>();
            try
            {
                if (File.Exists(ConfigFile))
                {
                    var lines = File.ReadAllLines(ConfigFile);
                    if (lines.Length >= 1)
                    {
                        string savedMachine = lines[0].Trim();
                        if (!string.Equals(savedMachine, GetMachineId(), StringComparison.OrdinalIgnoreCase))
                        { SaveConfig(); return; }
                    }
                    if (lines.Length >= 2) _cachedEditor = lines[1].Trim();
                    if (lines.Length >= 3 && !string.IsNullOrWhiteSpace(lines[2]))
                    {
                        foreach (var p in lines[2].Split('|'))
                        {
                            var t = p.Trim();
                            if (!string.IsNullOrEmpty(t) && File.Exists(t) &&
                                !_cachedHistory.Exists(x => x.Equals(t, StringComparison.OrdinalIgnoreCase)))
                                _cachedHistory.Add(t);
                        }
                    }
                }
                else SaveConfig();
            }
            catch { }
        }

        static void SaveConfig()
        {
            try
            {
                var lines = new string[]
                {
                    GetMachineId(),
                    _cachedEditor ?? "",
                    string.Join("|", _cachedHistory ?? new List<string>())
                };
                File.WriteAllLines(ConfigFile, lines, System.Text.Encoding.UTF8);
            }
            catch { }
        }

        static string EditorPath() { EnsureConfigLoaded(); return _cachedEditor ?? ""; }
        static void SetEditorPath(string path) { EnsureConfigLoaded(); _cachedEditor = path; SaveConfig(); }
        static List<string> LoadEditorHistory() { EnsureConfigLoaded(); return new List<string>(_cachedHistory); }

        static void AddEditorHistory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            EnsureConfigLoaded();
            var cmp = path.Trim();
            if (_cachedHistory.Exists(p => p.Equals(cmp, StringComparison.OrdinalIgnoreCase))) return;
            _cachedHistory.Add(cmp); SaveConfig();
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

        static void SetDebuggerAllViews(string exe)
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var k = baseKey.CreateSubKey(IFEO_KEY))
                    k.SetValue("Debugger", "\"" + exe + "\"", RegistryValueKind.String);
            }
        }

        static string ReadDebuggerAnyView()
        {
            string dbg = "";
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
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

        static void DoEnable()
        {
            string editor = EditorPath();
            if (string.IsNullOrWhiteSpace(editor) || !File.Exists(editor))
                throw new Exception("未设置有效的编辑器路径，请先在主界面选择编辑器。");
            SetDebuggerAllViews(Application.ExecutablePath);
        }

        static void DoRestore()
        {
            string currentExeName = AppName;
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var k = baseKey.OpenSubKey(IFEO_KEY, true))
                {
                    if (k == null) continue;
                    var dbg = k.GetValue("Debugger") as string;
                    if (dbg == null) continue;
                    // 匹配当前 exe 名称（支持改名场景）
                    if (dbg.IndexOf(currentExeName, StringComparison.OrdinalIgnoreCase) >= 0)
                        k.DeleteValue("Debugger", false);
                }
            }
        }

        static void Launcher(string[] args)
        {
            string editor = EditorPath();
            var sb = new System.Text.StringBuilder();
            for (int i = 1; i < args.Length; i++)
                sb.Append(" \"" + args[i] + "\"");
            string fileArgs = sb.ToString().Trim();
            try
            {
                if (!string.IsNullOrWhiteSpace(editor) && File.Exists(editor))
                    Process.Start(editor, fileArgs);
                else
                    Process.Start("notepad.exe", fileArgs);
            }
            catch { }
        }

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

        const string DEV_NAME  = "godsq";
        const string DEV_EMAIL = "godsq@qq.com";
        const string LICENSE   = "MIT License";

        public class MainForm : Form
        {
            TextBox txtEditor;
            ListBox lstEditors;
            Label lblStatus1, lblStatus2;
            RichTextBox tip;
            LinkLabel lnkToggle;
            Panel pnlHeader, pnlStatus, pnlFooter;
            Button btnBrowse, btnEnable, btnRestore, btnAdmin, btnDelete, btnClear;
            LinkLabel lnkAbout;
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

                pnlHeader = new Panel() { Dock = DockStyle.Top, Height = 68, BackColor = CAccent };
                pnlHeader.Paint += PnlHeader_Paint;
                this.Controls.Add(pnlHeader);

                var lbl1 = new Label()
                {
                    Left = 16, Top = 82, Width = 560,
                    Text = "选择要替换系统记事本的编辑器，单击列表项或点击“浏览”选择：",
                    ForeColor = CText, Font = new Font("Segoe UI", 9.5f)
                };
                this.Controls.Add(lbl1);

                txtEditor = new TextBox()
                {
                    Left = 16, Top = 108, Width = 452, Height = 24,
                    Font = new Font("Segoe UI", 10f), ForeColor = CText,
                    BorderStyle = BorderStyle.FixedSingle
                };
                this.Controls.Add(txtEditor);

                btnBrowse = new Button() { Left = 476, Top = 107, Width = 96, Height = 26, Text = "浏览..." };
                StyleButton(btnBrowse, Color.White, CAccent, CAccentBg, true);
                btnBrowse.Click += (s, e) => Browse();
                this.Controls.Add(btnBrowse);

                var lbl2 = new Label()
                {
                    Left = 16, Top = 150, Width = 568,
                    Text = "已自动检测到的编辑器（单击选用 ● 为当前已启用）：",
                    ForeColor = CSub, Font = new Font("Segoe UI", 9f)
                };
                this.Controls.Add(lbl2);

                lstEditors = new ListBox()
                {
                    Left = 16, Top = 176, Width = 568, Height = 194,
                    DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 46,
                    Font = new Font("Segoe UI", 10f),
                    BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White
                };
                lstEditors.Click += (s, e) => PickFromList();
                lstEditors.DrawItem += LstDraw;
                this.Controls.Add(lstEditors);

                btnDelete = new Button() { Left = 16, Top = 380, Width = 120, Height = 28, Text = "删除选中" };
                StyleButton(btnDelete, Color.White, Color.FromArgb(0xB9, 0x1C, 0x1C), Color.FromArgb(0xFE, 0xE2, 0xE2), true);
                btnDelete.Click += (s, e) => DeleteSelected();
                this.Controls.Add(btnDelete);

                btnClear = new Button() { Left = 144, Top = 380, Width = 120, Height = 28, Text = "清空历史" };
                StyleButton(btnClear, Color.White, CSub, CGrayBg, true);
                btnClear.Click += (s, e) => ClearHistory();
                this.Controls.Add(btnClear);

                btnEnable = new Button() { Left = 16, Top = 416, Width = 180, Height = 36, Text = "启用替换" };
                StyleButton(btnEnable, CAccent, Color.White, CAccentHi, false);
                btnEnable.Click += (s, e) => Enable();
                this.Controls.Add(btnEnable);

                btnRestore = new Button() { Left = 206, Top = 416, Width = 184, Height = 36, Text = "恢复默认记事本" };
                StyleButton(btnRestore, Color.White, CText, CGrayBg, true);
                btnRestore.Click += (s, e) => Restore();
                this.Controls.Add(btnRestore);

                btnAdmin = new Button() { Left = 396, Top = 416, Width = 188, Height = 36, Text = "以管理员身份运行" };
                StyleButton(btnAdmin, Color.White, CAmber, CAmberBg, true);
                btnAdmin.Click += (s, e) => RunAsAdmin();
                this.Controls.Add(btnAdmin);

                lnkToggle = new LinkLabel()
                {
                    Left = 16, Top = 458, Width = 560, Height = 20,
                    Text = "展开 中转原理 / 使用注意 ▼",
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                    LinkColor = CAccent, ActiveLinkColor = CAccentHi,
                    BackColor = Color.Transparent
                };
                lnkToggle.LinkClicked += (s, e) => ToggleTip();
                this.Controls.Add(lnkToggle);

                tip = new RichTextBox()
                {
                    Left = 16, Top = 482, Width = 568, Height = 192,
                    ReadOnly = true, ScrollBars = RichTextBoxScrollBars.Vertical,
                    BackColor = CGrayBg, BorderStyle = BorderStyle.FixedSingle,
                    Font = new Font("Segoe UI", 9f), DetectUrls = false,
                    Margin = new Padding(0), Padding = new Padding(6),
                    Visible = false
                };
                this.Controls.Add(tip);
                FillTip(tip);

                pnlStatus = new Panel() { Left = 16, Top = 484, Width = 568, Height = 50 };
                lblStatus1 = new Label()
                {
                    Left = 8, Top = 6, Width = 548, Height = 18,
                    ForeColor = CText, BackColor = Color.Transparent,
                    Font = new Font("Segoe UI", 9f), UseMnemonic = false,
                    AutoEllipsis = true
                };
                lblStatus2 = new Label()
                {
                    Left = 8, Top = 26, Width = 548, Height = 18,
                    ForeColor = CSub, BackColor = Color.Transparent,
                    Font = new Font("Segoe UI", 9f), UseMnemonic = false
                };
                pnlStatus.Controls.Add(lblStatus1);
                pnlStatus.Controls.Add(lblStatus2);
                this.Controls.Add(pnlStatus);

                pnlFooter = new Panel() { Left = 0, Top = 538, Width = 600, Height = 32, BackColor = CAccentBg };
                var lblDev = new Label()
                {
                    Left = 16, Top = 7, Width = 250, Height = 18,
                    Text = "© 2026 " + DEV_NAME + " · 基于 " + LICENSE + " 开源",
                    ForeColor = CSub, Font = new Font("Segoe UI", 9f), BackColor = Color.Transparent
                };
                var lblEmail = new Label()
                {
                    Left = 270, Top = 7, Width = 150, Height = 18,
                    Text = DEV_EMAIL, Font = new Font("Segoe UI", 9f),
                    ForeColor = CSub, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft
                };
                lnkAbout = new LinkLabel()
                {
                    Left = 424, Top = 7, Width = 160, Height = 18,
                    Text = "查看开源声明 ▸", Font = new Font("Segoe UI", 9f, FontStyle.Underline),
                    LinkColor = CAccent, ActiveLinkColor = CAccentHi, BackColor = Color.Transparent,
                    TextAlign = ContentAlignment.MiddleRight
                };
                lnkAbout.LinkClicked += (s, e) => ShowAbout();
                pnlFooter.Controls.Add(lblDev);
                pnlFooter.Controls.Add(lblEmail);
                pnlFooter.Controls.Add(lnkAbout);
                this.Controls.Add(pnlFooter);
            }

            static GraphicsPath RoundedRect(Rectangle r, int rad)
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

            void PnlHeader_Paint(object s, PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = pnlHeader.ClientRectangle;
                using (var br = new LinearGradientBrush(rect, CAccent, Color.FromArgb(0x4F, 0x46, 0xE5), 65f))
                    g.FillRectangle(br, rect);
                try
                {
                    using (var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                        g.DrawImage(ic.ToBitmap(), new Rectangle(16, (rect.Height - 38) / 2, 38, 38));
                }
                catch { }
                using (var f = new Font("Segoe UI", 14, FontStyle.Bold))
                using (var b = new SolidBrush(Color.White))
                    g.DrawString("记事本替换工具", f, b, 64, 13);
                using (var f2 = new Font("Segoe UI", 9))
                using (var b2 = new SolidBrush(Color.FromArgb(0xE0, 0xE7, 0xFF)))
                    g.DrawString("用你喜欢的编辑器接管系统记事本", f2, b2, 64, 41);
            }

            void LstDraw(object s, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                var r = e.Bounds;
                bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

                Color bg = sel ? CAccentBg : (e.Index % 2 == 0 ? Color.White : Color.FromArgb(0xF8, 0xFA, 0xFC));
                using (var br = new SolidBrush(bg)) g.FillRectangle(br, r);
                if (sel)
                    using (var pen = new Pen(CAccent, 2))
                        g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);

                string item = lstEditors.Items[e.Index].ToString();
                int idx = item.IndexOf("  ->  ");
                string name = idx >= 0 ? item.Substring(0, idx) : item;
                string path = idx >= 0 ? item.Substring(idx + 6) : "";
                bool isCurrent = !string.IsNullOrEmpty(path) &&
                    path.Equals(currentEditorPath, StringComparison.OrdinalIgnoreCase);

                var sq = new Rectangle(r.X + 10, r.Y + (r.Height - 32) / 2, 32, 32);
                using (var br2 = new SolidBrush(isCurrent ? CGreen : CAccent))
                    g.FillPath(br2, RoundedRect(sq, 8));
                string ini = name.Length > 0 ? char.ToUpper(name[0]).ToString() : "?";
                using (var f = new Font("Segoe UI", 13, FontStyle.Bold))
                using (var b = new SolidBrush(Color.White))
                using (var sf = new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(ini, f, b, new RectangleF(sq.X, sq.Y, sq.Width, sq.Height), sf);

                using (var fb = new Font("Segoe UI", 10.5f, FontStyle.Bold))
                using (var bb = new SolidBrush(isCurrent ? CGreen : CText))
                    g.DrawString(name, fb, bb, sq.Right + 10, r.Y + 6);

                using (var fp = new Font("Segoe UI", 8.5f))
                using (var bp = new SolidBrush(CSub))
                {
                    var pathRect = new RectangleF(sq.Right + 10, r.Y + 25, r.Width - sq.Right - 40, 18);
                    var psf = new StringFormat() { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Near };
                    g.DrawString(path, fp, bp, pathRect, psf);
                }

                if (isCurrent)
                {
                    var dot = new Rectangle(r.Right - 22, r.Y + (r.Height - 12) / 2, 12, 12);
                    using (var b = new SolidBrush(CGreen)) g.FillEllipse(b, dot);
                    using (var b2 = new SolidBrush(Color.White)) g.FillEllipse(b2, new Rectangle(dot.X + 4, dot.Y + 4, 4, 4));
                }
            }

            static void StyleButton(Button b, Color back, Color fore, Color hover, bool outline)
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

            void Browse()
            {
                var d = new OpenFileDialog()
                {
                    Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                    Title = "选择编辑器"
                };
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
                string s = lstEditors.SelectedItem.ToString();
                int idx = s.IndexOf("  ->  ");
                if (idx >= 0) { txtEditor.Text = s.Substring(idx + 6).Trim(); lstEditors.Invalidate(); }
            }

            void DeleteSelected()
            {
                if (lstEditors.SelectedIndex < 0)
                { MessageBox.Show("请先在列表中选中要删除的编辑器。", "提示"); return; }
                string s = lstEditors.SelectedItem.ToString();
                int idx = s.IndexOf("  ->  ");
                string path = idx >= 0 ? s.Substring(idx + 6).Trim() : "";
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

                if (!IsAdmin())
                {
                    try
                    {
                        var p = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "/enable")
                        { Verb = "runas", UseShellExecute = true });
                        p.WaitForExit();
                        if (p.ExitCode == 0)
                        {
                            ShowTempStatus("\u2713 已启用替换：记事本已重定向到你设置的编辑器。");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("提权未成功，替换未启用。\n\n可能原因：\n• UAC 提权被取消；\n• 被安全软件 / 杀毒软件 / Windows Defender 拦截了提权或本程序。\n\n请关闭拦截或将本程序加入白名单后重试。\n\n技术信息：" + ex.Message,
                            "提权失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                else
                {
                    try
                    {
                        DoEnable();
                        ShowTempStatus("\u2713 已启用替换：记事本已重定向到你设置的编辑器。");
                        return;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        MessageBox.Show("写入注册表被拒绝，替换未能生效。\n\n常见原因：\n• 未以管理员身份运行（请右键本程序→“以管理员身份运行”）；\n• 被安全软件 / 杀毒软件 / Windows Defender 拦截了对 IFEO 注册表的修改；\n• UAC 提权被取消。\n\n可先关闭拦截软件或将本程序加入白名单，再以管理员身份重试。",
                            "权限不足 / 被拦截", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("启用失败：" + ex.Message + "\n\n若被安全软件拦截，请将本程序加入白名单后以管理员身份重试。",
                            "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                RefreshStatus();
            }

            void Restore()
            {
                if (!IsAdmin())
                {
                    try
                    {
                        var p = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "/restore")
                        { Verb = "runas", UseShellExecute = true });
                        p.WaitForExit();
                        if (p.ExitCode == 0)
                        {
                            ShowTempStatus("\u2713 已恢复默认记事本。");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("提权未成功，未执行恢复。\n\n可能原因：\n• UAC 提权被取消；\n• 被安全软件 / 杀毒软件 / Windows Defender 拦截了提权或本程序。\n\n请关闭拦截或将本程序加入白名单后重试。\n\n技术信息：" + ex.Message,
                            "提权失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                else
                {
                    try
                    {
                        DoRestore();
                        ShowTempStatus("\u2713 已恢复默认记事本。");
                        return;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        MessageBox.Show("写入注册表被拒绝，恢复未能完成。\n\n常见原因：\n• 未以管理员身份运行；\n• 被安全软件 / 杀毒软件 / Windows Defender 拦截了对 IFEO 注册表的修改。\n\n请以管理员身份运行本程序或将本程序加入白名单后重试。",
                            "权限不足 / 被拦截", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("恢复失败：" + ex.Message + "\n\n若被安全软件拦截，请将本程序加入白名单后以管理员身份重试。",
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
                if (expand)
                {
                    this.ClientSize = new Size(600, 760);
                    pnlStatus.Top = 678;
                    pnlFooter.Top = 724;
                }
                else
                {
                    this.ClientSize = new Size(600, 570);
                    pnlStatus.Top = 484;
                    pnlFooter.Top = 538;
                }
            }

            void ShowTempStatus(string message)
            {
                if (tempStatusTimer == null)
                {
                    tempStatusTimer = new System.Windows.Forms.Timer();
                    tempStatusTimer.Interval = 2000;
                    tempStatusTimer.Tick += (s, e) => { tempStatusTimer.Stop(); RefreshStatus(); };
                }
                pnlStatus.BackColor = CGreenBg;
                lblStatus1.ForeColor = Color.FromArgb(0x14, 0x5A, 0x32);
                lblStatus1.Text = message;
                lblStatus2.Text = "";
                tempStatusTimer.Start();
            }

            void RefreshStatus()
            {
                bool elevated = IsAdmin();
                btnAdmin.Visible = !elevated;
                bool active = ReadDebuggerAnyView().IndexOf(AppName, StringComparison.OrdinalIgnoreCase) >= 0;
                string editor = EditorPath();
                string editorName = string.IsNullOrEmpty(editor) ? "(未记录)" : Path.GetFileName(editor);
                currentEditorPath = active ? editor : "";
                this.Text = AppName + " · " + (active ? "已启用替换" : "未启用替换") + " · " + editorName;
                lstEditors.Invalidate();

                if (active && elevated)
                {
                    pnlStatus.BackColor = CGreenBg;
                    lblStatus1.ForeColor = Color.FromArgb(0x14, 0x5A, 0x32);
                    lblStatus1.Text = "已启用替换　当前编辑器：" + editorName + "　·　" + editor;
                    lblStatus2.ForeColor = CAccent;
                    lblStatus2.Text = "已以管理员身份运行，可正常启用 / 恢复。";
                }
                else if (active && !elevated)
                {
                    pnlStatus.BackColor = CAmberBg;
                    lblStatus1.ForeColor = Color.FromArgb(0x14, 0x5A, 0x32);
                    lblStatus1.Text = "已启用替换　当前编辑器：" + editorName + "　·　" + editor;
                    lblStatus2.ForeColor = CAmber;
                    lblStatus2.Text = "当前非管理员，恢复 / 修改时会请求 UAC 提权（或点「以管理员身份运行」）。";
                }
                else if (!active && elevated)
                {
                    pnlStatus.BackColor = CGrayBg;
                    lblStatus1.ForeColor = Color.FromArgb(0x37, 0x41, 0x51);
                    lblStatus1.Text = "未启用　目前使用系统默认记事本";
                    lblStatus2.ForeColor = CAccent;
                    lblStatus2.Text = "已以管理员身份运行，点击「启用替换」即可生效。";
                }
                else
                {
                    pnlStatus.BackColor = CAmberBg;
                    lblStatus1.ForeColor = Color.FromArgb(0x92, 0x40, 0x06);
                    lblStatus1.Text = "未启用　目前使用系统默认记事本";
                    lblStatus2.ForeColor = CAmber;
                    lblStatus2.Text = "当前非管理员：点击「启用替换」会请求 UAC 提权（或点「以管理员身份运行」）。";
                }
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
                string item = Path.GetFileNameWithoutExtension(editor) + "  ->  " + editor;
                foreach (var o in lstEditors.Items)
                    if (string.Equals(o.ToString(), item, StringComparison.OrdinalIgnoreCase)) return;
                lstEditors.Items.Add(item);
            }

            void ShowAbout() { using (var dlg = new AboutForm()) dlg.ShowDialog(this); }

            void FillTip(RichTextBox rt)
            {
                rt.Clear();
                var title = new Font("Segoe UI", 10f, FontStyle.Bold);
                var body = new Font("Segoe UI", 9f);
                rt.SelectionFont = title; rt.SelectionColor = CText;
                rt.AppendText("【中转原理】\n");
                rt.SelectionFont = body; rt.SelectionColor = Color.FromArgb(0x2B, 0x34, 0x44);
                rt.AppendText("本工具利用 Windows 映像劫持(IFEO)：把 notepad.exe 的“调试器”指向本程序。\n");
                rt.AppendText("当任意程序或双击打开 .txt 时，系统实际启动的是本工具，再由本工具调用你设置的编辑器打开该文件。\n");
                rt.AppendText("不修改任何系统文件，仅写入注册表，可随时一键恢复。\n\n");
                rt.SelectionFont = title; rt.SelectionColor = CText;
                rt.AppendText("【使用注意】\n");
                rt.SelectionFont = body; rt.SelectionColor = Color.FromArgb(0x2B, 0x34, 0x44);
                rt.AppendText("① 写入 IFEO 需管理员权限：点“启用替换”会请求 UAC 提权，或右键本程序“以管理员身份运行”。\n\n");
                rt.AppendText("② 本程序必须保留在当前路径；删除前请先点“恢复默认记事本”解除，否则系统将无法打开记事本。\n\n");
                rt.AppendText("③ 仅劫持 notepad.exe，不影响其他软件；异常时点“恢复默认记事本”即可立即还原。\n\n");
                rt.AppendText("④ 注册表：HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\notepad.exe");
                rt.SelectionStart = 0; rt.SelectionLength = 0;
            }

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

                    var pnl = new Panel() { Dock = DockStyle.Top, Height = 76, BackColor = CAccent };
                    pnl.Paint += (s, e) =>
                    {
                        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                        var rect = pnl.ClientRectangle;
                        using (var br = new LinearGradientBrush(rect, CAccent, Color.FromArgb(0x4F, 0x46, 0xE5), 65f))
                            g.FillRectangle(br, rect);
                        try { using (var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath)) g.DrawImage(ic.ToBitmap(), new Rectangle(16, 19, 38, 38)); } catch { }
                        using (var f = new Font("Segoe UI", 14, FontStyle.Bold))
                        using (var b = new SolidBrush(Color.White))
                            g.DrawString("记事本替换工具", f, b, 64, 14);
                        using (var f2 = new Font("Segoe UI", 9))
                        using (var b2 = new SolidBrush(Color.FromArgb(0xE0, 0xE7, 0xFF)))
                            g.DrawString("用你喜欢的编辑器接管系统记事本", f2, b2, 64, 42);
                    };
                    this.Controls.Add(pnl);

                    var info = new Label()
                    {
                        Left = 18, Top = 90, Width = 524, Height = 56,
                        Text = "Developer: " + DEV_NAME + " | License: " + LICENSE + "\nEmail: " + DEV_EMAIL,
                        ForeColor = CText, Font = new Font("Segoe UI", 10f)
                    };
                    this.Controls.Add(info);

                    var lic = new TextBox()
                    {
                        Left = 18, Top = 152, Width = 524, Height = 232,
                        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                        Font = new Font("Consolas", 9f), ForeColor = CText, BackColor = Color.FromArgb(0xF8, 0xFA, 0xFC),
                        BorderStyle = BorderStyle.FixedSingle
                    };
                    lic.Text = MIT_TEXT;
                    this.Controls.Add(lic);

                    var btn = new Button() { Left = 18, Top = 396, Width = 140, Height = 32, Text = "Copy License" };
                    StyleButton(btn, CAccent, Color.White, CAccentHi, false);
                    btn.Click += (s, e) => { try { Clipboard.SetText(MIT_TEXT); MessageBox.Show("Copied to clipboard.", "Notice"); } catch { } };
                    this.Controls.Add(btn);

                    var btnClose = new Button() { Left = 402, Top = 396, Width = 140, Height = 32, Text = "Close" };
                    StyleButton(btnClose, Color.White, CText, CGrayBg, true);
                    btnClose.Click += (s, e) => this.Close();
                    this.Controls.Add(btnClose);
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
                        lstEditors.Items.Add(Path.GetFileNameWithoutExtension(p) + "  ->  " + p);
                        return;
                    }
            }
        }
    }
}
