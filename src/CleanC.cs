using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CleanC
{
    enum Kind { DirContents, DirRemove, FileGlob, RecycleBin, HibernateOff }

    class CleanItem
    {
        public string Group;
        public string Name;
        public string Note;
        public Kind Kind;
        public string[] Paths;
        public bool DefaultChecked;
        public long Size;
        public bool On;
        public ListViewItem Row;

        public CleanItem(string g, string n, string note, Kind k, string[] paths, bool def)
        {
            Group = g; Name = n; Note = note; Kind = k; Paths = paths; DefaultChecked = def; On = def;
        }
    }

    static class Util
    {
        public static List<string> Errors = new List<string>();
        public static string SysDrive
        {
            get { return Path.GetPathRoot(Environment.SystemDirectory).TrimEnd('\\'); }
        }

        public static List<string> Resolve(string raw)
        {
            var res = new List<string>();
            string p = Environment.ExpandEnvironmentVariables(raw);
            try
            {
                if (p.IndexOf('*') >= 0 || p.IndexOf('?') >= 0)
                {
                    string[] parts = p.Split('\\');
                    var cur = new List<string>();
                    cur.Add(parts[0] + "\\");
                    for (int i = 1; i < parts.Length; i++)
                    {
                        var next = new List<string>();
                        foreach (string c in cur)
                        {
                            if (parts[i].IndexOf('*') >= 0 || parts[i].IndexOf('?') >= 0)
                            {
                                try { next.AddRange(Directory.GetDirectories(c, parts[i])); }
                                catch { }
                            }
                            else next.Add(Path.Combine(c, parts[i]));
                        }
                        cur = next;
                        if (cur.Count == 0) break;
                    }
                    res.AddRange(cur);
                }
                else res.Add(p);
            }
            catch { }
            return res;
        }

        public static long WalkSize(string path)
        {
            long total = 0;
            if (!Directory.Exists(path)) return 0;
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                string cur = stack.Pop();
                FileSystemInfo[] ents;
                try { ents = new DirectoryInfo(cur).GetFileSystemInfos(); }
                catch { continue; }
                foreach (FileSystemInfo e in ents)
                {
                    try
                    {
                        if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        DirectoryInfo d = e as DirectoryInfo;
                        if (d != null) stack.Push(d.FullName);
                        else total += ((FileInfo)e).Length;
                    }
                    catch { }
                }
            }
            return total;
        }

        // 返回实际释放字节数
        public static long Purge(string path, bool removeRoot)
        {
            long freed = 0;
            try
            {
                if (File.Exists(path))
                {
                    long s = new FileInfo(path).Length;
                    try { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); freed += s; }
                    catch { }
                    return freed;
                }
                if (!Directory.Exists(path)) return 0;

                var dirs = new List<string>();
                var stack = new Stack<string>();
                stack.Push(path);
                while (stack.Count > 0)
                {
                    string cur = stack.Pop();
                    dirs.Add(cur);
                    FileSystemInfo[] ents;
                    try { ents = new DirectoryInfo(cur).GetFileSystemInfos(); }
                    catch (Exception ex) { if (Errors.Count < 20) Errors.Add("enum:" + cur + " -> " + ex.GetType().Name + " " + ex.Message); continue; }
                    foreach (FileSystemInfo e in ents)
                    {
                        try
                        {
                            if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                DirectoryInfo rd = e as DirectoryInfo;
                                if (rd != null) { try { Directory.Delete(rd.FullName, false); } catch { } }
                                else
                                {
                                    long rs = ((FileInfo)e).Length;
                                    try { File.Delete(e.FullName); freed += rs; }
                                    catch { }
                                }
                                continue;
                            }
                            DirectoryInfo d = e as DirectoryInfo;
                            if (d != null) { stack.Push(d.FullName); continue; }
                            long s = ((FileInfo)e).Length;
                            try
                            {
                                File.SetAttributes(e.FullName, FileAttributes.Normal);
                                File.Delete(e.FullName);
                                freed += s;
                            }
                            catch (Exception ex2) { if (Errors.Count < 20) Errors.Add("del:" + e.FullName + " -> " + ex2.GetType().Name + " " + ex2.Message); }
                        }
                        catch { }
                    }
                }
                for (int i = dirs.Count - 1; i >= 0; i--)
                {
                    if (!removeRoot && i == 0) continue;
                    try { if (Directory.Exists(dirs[i])) Directory.Delete(dirs[i], false); }
                    catch { }
                }
            }
            catch { }
            return freed;
        }

        public static string Fmt(long bytes)
        {
            double b = bytes;
            if (b >= 1073741824.0) return (b / 1073741824.0).ToString("0.00") + " GB";
            if (b >= 1048576.0) return (b / 1048576.0).ToString("0.0") + " MB";
            if (b >= 1024.0) return (b / 1024.0).ToString("0") + " KB";
            return bytes + " B";
        }
    }

    static class Native
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);
    }

    class Program
    {
        static List<CleanItem> items = new List<CleanItem>();
        static ListView lv;
        static Label lblDisk, lblTotal;
        static TextBox txtLog;
        static Button btnScan, btnClean, btnAll, btnSafe, btnNone;
        static bool suppress = false;
        static bool busy = false;
        static string S = Util.SysDrive;

        static void BuildItems()
        {
            items.Clear();
            string U = "%USERPROFILE%";
            string L = "%LOCALAPPDATA%";

            Action<string, string, string, Kind, string[], bool> A = delegate(string g, string n, string note, Kind k, string[] p, bool def)
            {
                items.Add(new CleanItem(g, n, note, k, p, def));
            };

            // ① 系统临时文件
            string g1 = "① 系统临时文件";
            A(g1, "Windows 临时目录", "系统运行时的临时文件，可随时清空",
              Kind.DirContents, new string[] { "%SystemRoot%\\Temp" }, true);
            A(g1, "Windows 更新下载缓存", "已安装更新的安装包残留，不影响已装更新",
              Kind.DirContents, new string[] { "%SystemRoot%\\SoftwareDistribution\\Download" }, true);
            A(g1, "错误报告与崩溃转储", "WER 报告、程序崩溃转储、内核转储",
              Kind.DirContents, new string[] {
                  S + "\\ProgramData\\Microsoft\\Windows\\WER",
                  L + "\\CrashDumps",
                  "%SystemRoot%\\Minidump",
                  "%SystemRoot%\\LiveKernelReports" }, true);
            A(g1, "内核内存转储文件", "蓝屏转储，排查完即可删",
              Kind.FileGlob, new string[] { "%SystemRoot%\\MEMORY.DMP" }, true);
            A(g1, "传递优化缓存", "Windows 更新与商店的分发缓存",
              Kind.DirContents, new string[] { "%SystemRoot%\\ServiceProfiles\\NetworkService\\AppData\\Local\\Microsoft\\Windows\\DeliveryOptimization" }, true);

            // ② 用户与显卡缓存
            string g2 = "② 用户与显卡缓存";
            A(g2, "当前用户临时文件", "%TEMP% 目录，占用常常最大",
              Kind.DirContents, new string[] { "%TEMP%" }, true);
            A(g2, "所有用户临时文件", "本机全部账户的 Temp 目录",
              Kind.DirContents, new string[] { S + "\\Users\\*\\AppData\\Local\\Temp" }, true);
            A(g2, "DirectX 着色器缓存", "显卡编译的着色器，删后自动重建",
              Kind.DirContents, new string[] { L + "\\D3DSCache" }, true);
            A(g2, "NVIDIA 着色器缓存", "DXCache/GLCache，常有十几 GB；删后游戏首次加载略慢",
              Kind.DirContents, new string[] { L + "\\NVIDIA\\DXCache", L + "\\NVIDIA\\GLCache", L + "\\NVIDIA Corporation\\NV_Cache" }, true);
            A(g2, "AMD 着色器缓存", "AMD 显卡着色器缓存",
              Kind.DirContents, new string[] { L + "\\AMD\\DxCache", L + "\\AMD\\DxcCache", L + "\\AMD\\GLCache", L + "\\AMD\\VkCache" }, true);
            A(g2, "缩略图与图标缓存", "资源管理器自动重建",
              Kind.FileGlob, new string[] { L + "\\Microsoft\\Windows\\Explorer\\thumbcache_*.db", L + "\\Microsoft\\Windows\\Explorer\\iconcache_*.db" }, true);
            A(g2, "浏览器缓存", "Edge / Chrome 的网页缓存与缓存存储",
              Kind.DirContents, new string[] {
                  L + "\\Microsoft\\Edge\\User Data\\Default\\Cache",
                  L + "\\Microsoft\\Edge\\User Data\\Default\\Code Cache",
                  L + "\\Microsoft\\Edge\\User Data\\Default\\GPUCache",
                  L + "\\Microsoft\\Edge\\User Data\\Default\\Service Worker\\CacheStorage",
                  L + "\\Google\\Chrome\\User Data\\Default\\Cache",
                  L + "\\Google\\Chrome\\User Data\\Default\\Code Cache",
                  L + "\\Google\\Chrome\\User Data\\Default\\GPUCache",
                  L + "\\Google\\Chrome\\User Data\\Default\\Service Worker\\CacheStorage" }, true);
            A(g2, "回收站", "清空所有分区的回收站",
              Kind.RecycleBin, new string[] { "" }, true);

            // ③ 开发工具缓存
            string g3 = "③ 开发工具缓存";
            A(g3, "pip 缓存", "Python 下载的 wheel 缓存",
              Kind.DirContents, new string[] { L + "\\pip\\Cache", L + "\\pip" }, true);
            A(g3, "npm 缓存", "npm 包缓存",
              Kind.DirContents, new string[] { L + "\\npm-cache" }, true);
            A(g3, "pnpm 缓存与仓库", "pnpm 全局存储，重装依赖会重新下载",
              Kind.DirContents, new string[] { L + "\\pnpm-cache", L + "\\pnpm\\store" }, true);
            A(g3, "Yarn 缓存", "Yarn 包缓存",
              Kind.DirContents, new string[] { L + "\\Yarn\\Cache" }, true);
            A(g3, "NuGet 包缓存", ".NET 依赖包，恢复项目时重新下载",
              Kind.DirContents, new string[] { U + "\\.nuget\\packages" }, true);
            A(g3, "Gradle 缓存", "Android/Java 构建缓存",
              Kind.DirContents, new string[] { U + "\\.gradle\\caches" }, true);
            A(g3, "Maven 仓库缓存", "Java 依赖缓存",
              Kind.DirContents, new string[] { U + "\\.m2\\repository" }, true);
            A(g3, "Cargo registry 缓存", "Rust 依赖源码缓存",
              Kind.DirContents, new string[] { U + "\\.cargo\\registry" }, true);
            A(g3, "Go 模块缓存", "GOPATH/pkg/mod",
              Kind.DirContents, new string[] { U + "\\go\\pkg\\mod" }, true);

            // ④ 需确认
            string g4 = "④ 需确认（有代价）";
            A(g4, "旧系统 Windows.old", "删除后无法回滚到旧版本系统",
              Kind.DirRemove, new string[] { S + "\\Windows.old" }, false);
            A(g4, "休眠文件", "关闭休眠与快速启动，释放约等于内存大小",
              Kind.HibernateOff, new string[] { S + "\\hiberfil.sys" }, false);
            A(g4, "Prefetch 预读文件", "删除后头几次启动略慢",
              Kind.DirContents, new string[] { "%SystemRoot%\\Prefetch" }, false);
            A(g4, "系统日志", "Windows\\Logs，排障时才需要",
              Kind.DirContents, new string[] { "%SystemRoot%\\Logs" }, false);
            A(g4, "Playwright 浏览器", "自动化测试用的浏览器，用时会重新下载",
              Kind.DirContents, new string[] { L + "\\ms-playwright" }, false);
            A(g4, "剪映草稿缓存", "剪映的临时渲染缓存",
              Kind.DirContents, new string[] { L + "\\JianyingPro\\User Data\\Cache" }, false);
        }

        static long ItemSize(CleanItem it)
        {
            long total = 0;
            if (it.Kind == Kind.RecycleBin)
            {
                foreach (string d in Directory.GetLogicalDrives())
                {
                    string rb = d + "$Recycle.Bin";
                    total += Util.WalkSize(rb);
                }
                return total;
            }
            if (it.Kind == Kind.HibernateOff)
            {
                foreach (string p in it.Paths)
                {
                    string f = Environment.ExpandEnvironmentVariables(p);
                    try { if (File.Exists(f)) total += new FileInfo(f).Length; } catch { }
                }
                return total;
            }
            foreach (string raw in it.Paths)
            {
                foreach (string p in Util.Resolve(raw))
                {
                    if (it.Kind == Kind.FileGlob)
                    {
                        try
                        {
                            string dir = Path.GetDirectoryName(p);
                            if (Directory.Exists(dir))
                                foreach (string f in Directory.GetFiles(dir, Path.GetFileName(p)))
                                    total += new FileInfo(f).Length;
                        }
                        catch { }
                    }
                    else total += Util.WalkSize(p);
                }
            }
            return total;
        }

        static long CleanOne(CleanItem it, Action<string> log)
        {
            long freed = 0;
            if (it.Kind == Kind.RecycleBin)
            {
                long before = ItemSize(it);
                try { Native.SHEmptyRecycleBin(IntPtr.Zero, null, 0x7); } catch { }
                freed = before;
                return freed;
            }
            if (it.Kind == Kind.HibernateOff)
            {
                long before = ItemSize(it);
                try
                {
                    var psi = new ProcessStartInfo("powercfg", "/h off");
                    psi.UseShellExecute = false; psi.CreateNoWindow = true;
                    Process.Start(psi).WaitForExit();
                }
                catch { }
                long after = ItemSize(it);
                freed = before - after;
                return freed;
            }
            foreach (string raw in it.Paths)
            {
                foreach (string p in Util.Resolve(raw))
                {
                    if (it.Kind == Kind.FileGlob)
                    {
                        try
                        {
                            string dir = Path.GetDirectoryName(p);
                            if (!Directory.Exists(dir)) continue;
                            foreach (string f in Directory.GetFiles(dir, Path.GetFileName(p)))
                                freed += Util.Purge(f, false);
                        }
                        catch { }
                    }
                    else
                    {
                        freed += Util.Purge(p, it.Kind == Kind.DirRemove);
                    }
                }
            }
            return freed;
        }

        [STAThread]
        static void Main(string[] args)
        {
            BuildItems();

            if (args.Length > 0 && args[0] == "-selftest")
            {
                var sb = new StringBuilder();
                sb.AppendLine("C盘清理工具 自检  " + DateTime.Now);
                foreach (CleanItem it in items)
                {
                    long s = ItemSize(it);
                    sb.AppendLine(string.Format("[{0}] {1} = {2}", it.Group, it.Name, Util.Fmt(s)));
                }
                string outp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.txt");
                File.WriteAllText(outp, sb.ToString(), Encoding.UTF8);
                return;
            }

            if (args.Length > 1 && args[0] == "-purge")
            {
                var sb2 = new StringBuilder();
                long g = Util.Purge(args[1], args.Length > 2 && args[2] == "-rm");
                sb2.AppendLine("purge " + args[1] + " = " + Util.Fmt(g));
                foreach (string er in Util.Errors) sb2.AppendLine("ERR " + er);
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "purge.txt"), sb2.ToString(), Encoding.UTF8);
                return;
            }

            if (args.Length > 0 && args[0] == "-autoclean")
            {
                long total = 0;
                var sb = new StringBuilder();
                long free0 = new DriveInfo(S).AvailableFreeSpace;
                foreach (CleanItem it in items)
                {
                    if (!it.On) continue;
                    long f = CleanOne(it, delegate(string s) { });
                    total += f;
                    sb.AppendLine(string.Format("{0} = {1}", it.Name, Util.Fmt(f)));
                }
                long free1 = new DriveInfo(S).AvailableFreeSpace;
                sb.AppendLine(string.Format("合计删除 {0}，磁盘可用增加 {1}", Util.Fmt(total), Util.Fmt(free1 - free0)));
                foreach (string er in Util.Errors) sb.AppendLine("ERR " + er);
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "autoclean.txt"), sb.ToString(), Encoding.UTF8);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        public static List<CleanItem> Items { get { return items; } }
        public static string SysDriveRoot { get { return S; } }

        // ---------------- UI ----------------
        public class MainForm : Form
        {
            public MainForm()
            {
                Text = "C盘清理工具  ·  安全清理缓存与临时文件";
                ClientSize = new Size(980, 880);
                StartPosition = FormStartPosition.CenterScreen;
                MinimumSize = new Size(840, 620);
                Font = new Font("Microsoft YaHei UI", 9F);
                BackColor = Color.FromArgb(248, 249, 251);

                var top = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = Color.White };
                lblDisk = new Label { Left = 18, Top = 12, Width = 620, Height = 22, Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold), Text = "磁盘信息读取中…" };
                var hint = new Label { Left = 18, Top = 40, Width = 700, Height = 20, ForeColor = Color.Gray, Text = "勾选要清理的项目 → 点「开始清理」。默认只勾选安全项，可放心清理。" };
                top.Controls.Add(lblDisk);
                top.Controls.Add(hint);

                var bar = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Color.FromArgb(248, 249, 251) };
                btnScan = MkBtn("重新扫描", 18, 8, 96, Color.FromArgb(0, 120, 215));
                btnClean = MkBtn("开始清理", 122, 8, 110, Color.FromArgb(214, 68, 68));
                btnAll = MkBtn("全选", 246, 8, 72, Color.FromArgb(110, 118, 129));
                btnSafe = MkBtn("仅安全项", 324, 8, 96, Color.FromArgb(110, 118, 129));
                btnNone = MkBtn("全不选", 426, 8, 80, Color.FromArgb(110, 118, 129));
                lblTotal = new Label { Left = 530, Top = 14, Width = 380, Height = 24, Text = "已选：0 项 / 0 B", Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), ForeColor = Color.FromArgb(214, 68, 68) };
                bar.Controls.Add(btnScan);
                bar.Controls.Add(btnClean);
                bar.Controls.Add(btnAll);
                bar.Controls.Add(btnSafe);
                bar.Controls.Add(btnNone);
                bar.Controls.Add(lblTotal);

                lv = new ListView
                {
                    Dock = DockStyle.Fill,
                    View = View.Details,
                    CheckBoxes = true,
                    FullRowSelect = true,
                    HideSelection = false,
                    BorderStyle = BorderStyle.FixedSingle,
                    HeaderStyle = ColumnHeaderStyle.Nonclickable,
                    ShowGroups = true
                };
                lv.Columns.Add("项目", 250);
                lv.Columns.Add("说明", 560);
                lv.Columns.Add("占用", 130, HorizontalAlignment.Right);

                var bottom = new Panel { Dock = DockStyle.Bottom, Height = 168, BackColor = Color.White };
                var lbLog = new Label { Left = 18, Top = 8, Width = 200, Height = 20, Text = "运行日志", Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) };
                txtLog = new TextBox
                {
                    Left = 18, Top = 30, Width = 940, Height = 126,
                    Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true,
                    BackColor = Color.FromArgb(250, 250, 252), BorderStyle = BorderStyle.FixedSingle
                };
                bottom.Controls.Add(lbLog);
                bottom.Controls.Add(txtLog);

                Controls.Add(lv);
                Controls.Add(bottom);
                Controls.Add(bar);
                Controls.Add(top);

                lv.ItemCheck += OnItemCheck;
                btnScan.Click += delegate { StartScan(); };
                btnClean.Click += delegate { StartClean(); };
                btnAll.Click += delegate { SetAll(true, true); };
                btnNone.Click += delegate { SetAll(false, true); };
                btnSafe.Click += delegate { SetAll(false, false); };
                txtLog.AppendText("就绪。" + Environment.NewLine);
                if (!IsAdmin()) txtLog.AppendText("提示：未以管理员身份运行，系统目录项目可能清理失败，建议右键「以管理员身份运行」。" + Environment.NewLine);

                Shown += delegate { LoadRows(); RefreshDisk(); StartScan(); };
            }

            Button MkBtn(string t, int x, int y, int w, Color c)
            {
                var b = new Button
                {
                    Text = t, Left = x, Top = y, Width = w, Height = 32,
                    FlatStyle = FlatStyle.Flat, BackColor = c, ForeColor = Color.White,
                    Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold), Cursor = Cursors.Hand
                };
                b.FlatAppearance.BorderSize = 0;
                return b;
            }

            static bool IsAdmin()
            {
                try
                {
                    var p = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent());
                    return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
                catch { return false; }
            }

            void LoadRows()
            {
                suppress = true;
                lv.Items.Clear();
                var groups = new Dictionary<string, ListViewGroup>();
                foreach (CleanItem it in Program.Items)
                {
                    ListViewGroup g;
                    if (!groups.TryGetValue(it.Group, out g))
                    {
                        g = new ListViewGroup(it.Group, it.Group);
                        lv.Groups.Add(g);
                        groups[it.Group] = g;
                    }
                    var row = new ListViewItem(it.Name);
                    row.SubItems.Add(it.Note);
                    row.SubItems.Add("扫描中…");
                    row.Group = g;
                    row.Checked = it.On;
                    row.Tag = it;
                    it.Row = row;
                    lv.Items.Add(row);
                }
                suppress = false;
                UpdateTotal();
            }

            void SetAll(bool on, bool includeRisky)
            {
                suppress = true;
                foreach (CleanItem it in Program.Items)
                {
                    bool risky = (it.Group == "④ 需确认（有代价）");
                    if (on && risky && !includeRisky) { it.On = false; }
                    else it.On = on;
                    if (it.Row != null) it.Row.Checked = it.On;
                }
                suppress = false;
                UpdateTotal();
            }

            void OnItemCheck(object sender, ItemCheckEventArgs e)
            {
                if (suppress) return;
                var it = lv.Items[e.Index].Tag as CleanItem;
                if (it != null) it.On = (e.NewValue == CheckState.Checked);
                BeginInvoke(new Action(UpdateTotal));
                if (it != null && it.Group.StartsWith("④") && e.NewValue == CheckState.Checked)
                {
                    txtLog.AppendText("[提醒] 「" + it.Name + "」" + it.Note + Environment.NewLine);
                }
            }

            void UpdateTotal()
            {
                long total = 0; int n = 0;
                foreach (CleanItem it in Program.Items)
                    if (it.On) { total += it.Size; n++; }
                lblTotal.Text = string.Format("已选：{0} 项 / {1}", n, Util.Fmt(total));
            }

            void RefreshDisk()
            {
                var di = new DriveInfo(Program.SysDriveRoot);
                lblDisk.Text = string.Format("{0} 盘   总容量 {1}    已用 {2}    可用 {3}",
                    Program.SysDriveRoot, Util.Fmt(di.TotalSize), Util.Fmt(di.TotalSize - di.TotalFreeSpace), Util.Fmt(di.AvailableFreeSpace));
            }

            void Log(string s)
            {
                if (InvokeRequired) { BeginInvoke(new Action<string>(Log), s); return; }
                txtLog.AppendText(s + Environment.NewLine);
                txtLog.SelectionStart = txtLog.TextLength;
                txtLog.ScrollToCaret();
            }

            void Busy(bool b)
            {
                busy = b;
                btnScan.Enabled = !b;
                btnClean.Enabled = !b;
                btnAll.Enabled = !b;
                btnSafe.Enabled = !b;
                btnNone.Enabled = !b;
                Cursor = b ? Cursors.WaitCursor : Cursors.Default;
            }

            void StartScan()
            {
                if (busy) return;
                Busy(true);
                var t = new Thread(delegate()
                {
                    try
                    {
                        foreach (CleanItem it in Program.Items)
                        {
                            long s = ItemSize(it);
                            CleanItem ci = it;
                            BeginInvoke(new Action(delegate()
                            {
                                ci.Size = s;
                                if (ci.Row != null) ci.Row.SubItems[2].Text = Util.Fmt(s);
                            }));
                        }
                        Log("扫描完成。");
                    }
                    catch (Exception ex) { Log("扫描出错：" + ex.Message); }
                    BeginInvoke(new Action(delegate() { Busy(false); UpdateTotal(); RefreshDisk(); }));
                });
                t.IsBackground = true;
                t.Start();
            }

            void StartClean()
            {
                if (busy) return;
                var sel = new List<CleanItem>();
                foreach (CleanItem it in Program.Items) if (it.On) sel.Add(it);
                if (sel.Count == 0) { MessageBox.Show("请先勾选要清理的项目。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

                var di0 = new DriveInfo(Program.SysDriveRoot);
                long free0 = di0.AvailableFreeSpace;
                if (MessageBox.Show(string.Format("将清理 {0} 个项目，预计释放 {1}。\n\n清理过程不可撤销，是否继续？",
                    sel.Count, Util.Fmt(SumSel(sel))), "确认清理", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

                Busy(true);
                var t = new Thread(delegate()
                {
                    long total = 0;
                    try
                    {
                        foreach (CleanItem it in sel)
                        {
                            Log("正在清理：" + it.Name + " …");
                            long f = CleanOne(it, Log);
                            total += f;
                            CleanItem ci = it;
                            BeginInvoke(new Action(delegate()
                            {
                                ci.Size = 0;
                                if (ci.Row != null) ci.Row.SubItems[2].Text = "已清理";
                            }));
                        }
                    }
                    catch (Exception ex) { Log("清理出错：" + ex.Message); }
                    BeginInvoke(new Action(delegate()
                    {
                        var di1 = new DriveInfo(Program.SysDriveRoot);
                        long gained = di1.AvailableFreeSpace - free0;
                        Log(string.Format("清理完成：统计删除约 {0}，磁盘可用增加 {1}。", Util.Fmt(total), Util.Fmt(gained)));
                        Busy(false);
                        RefreshDisk();
                        UpdateTotal();
                        MessageBox.Show(string.Format("清理完成！\n\n删除文件约 {0}\n可用空间增加 {1}\n\n当前可用：{2}",
                            Util.Fmt(total), Util.Fmt(gained), Util.Fmt(di1.AvailableFreeSpace)),
                            "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                });
                t.IsBackground = true;
                t.Start();
            }

            long SumSel(List<CleanItem> sel)
            {
                long s = 0;
                foreach (CleanItem it in sel) s += it.Size;
                return s;
            }
        }
    }
}
