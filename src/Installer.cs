using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace TerraNilTrainer
{
    public static class InstallEngine
    {
        public const string ExpectedGameHash = "FF04732FEBC096F2C0296953D8477C16170E0B6752A8967B50051748EC7AED23";
        public const string MarkerDirectory = ".TerraNilTrainer";
        public static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream file = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
        }
        public static string Inside(string root, string relative)
        {
            string canonical = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(canonical, relative));
            if (!path.StartsWith(canonical, StringComparison.OrdinalIgnoreCase))
                throw new IOException("安装清单含有越界路径，操作已停止。");
            return path;
        }
        public static void RejectLinks(string root, string target)
        {
            string canonical = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            for (string p = Path.GetFullPath(target); p != null && p.Length >= canonical.Length; p = Path.GetDirectoryName(p))
                if ((Directory.Exists(p) || File.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("目标路径包含目录链接或符号链接，请选择实际游戏目录。");
        }
        public static void EnsureClosed(string root)
        {
            foreach (Process p in Process.GetProcessesByName("Terra Nil"))
            {
                p.Dispose();
                throw new IOException("请先退出《伊始之地》，再安装或卸载。");
            }
        }
        public static void CheckGame(string root)
        {
            string exe = Inside(root, "Terra Nil.exe");
            string assembly = Inside(root, "Terra Nil_Data/Managed/Assembly-CSharp.dll");
            RejectLinks(root, assembly);
            if (!File.Exists(exe) || !File.Exists(assembly)) throw new IOException("该目录不是完整的《伊始之地》安装目录。");
            if (Hash(assembly) != ExpectedGameHash) throw new IOException("游戏版本不匹配。本修改器仅适配当前构建 22566272；未写入任何游戏文件。");
        }
        public static List<string[]> ReadManifest(string path)
        {
            return File.ReadAllLines(path, Encoding.UTF8).Where(x => !String.IsNullOrWhiteSpace(x))
                .Select(x => x.Split('\t')).Select(x => {
                    if (x.Length != 2 || x[1].Length != 64) throw new IOException("安装清单损坏。");
                    return x;
                }).ToList();
        }
        public static string Install(string root, string payload, string savePath)
        {
            root = Path.GetFullPath(root);
            CheckGame(root);
            EnsureClosed(root);
            string marker = Inside(root, MarkerDirectory);
            string manifest = Inside(root, MarkerDirectory + "/installed.tsv");
            if (File.Exists(manifest)) throw new IOException("已经安装本修改器。无需重复安装；更新时先卸载旧版本。");
            // 不接管已有框架或其他模组的文件。
            foreach (string conflict in new string[] { "winhttp.dll", "doorstop_config.ini", "MelonLoader", "version.dll" })
                if (File.Exists(Inside(root, conflict)) || Directory.Exists(Inside(root, conflict)))
                    throw new IOException("检测到现有模组框架或加载文件：" + conflict + "。已停止自动安装，请按说明手动仅添加本插件。");
            string bep = Inside(root, "BepInEx");
            if (Directory.Exists(bep))
            {
                bool ownRecovery = Directory.Exists(marker) && Directory.GetFiles(marker, "installed.tsv", SearchOption.AllDirectories).Length > 0;
                bool nonGenerated = Directory.GetFiles(bep, "*", SearchOption.AllDirectories).Any(f => {
                    string rel = f.Substring(bep.Length + 1).Replace('\\', '/');
                    return !rel.StartsWith("config/", StringComparison.OrdinalIgnoreCase) &&
                        !rel.StartsWith("cache/", StringComparison.OrdinalIgnoreCase) &&
                        !rel.Equals("LogOutput.log", StringComparison.OrdinalIgnoreCase);
                });
                if (!ownRecovery || nonGenerated) throw new IOException("检测到现有模组加载框架。已停止自动安装，请按说明手动仅添加本插件。");
            }
            string packageManifest = Path.Combine(payload, "package.tsv");
            List<string[]> files = ReadManifest(packageManifest);
            foreach (string[] f in files)
            {
                string source = Inside(payload, f[0]), target = Inside(root, f[0]);
                RejectLinks(root, target);
                if (!File.Exists(source) || Hash(source) != f[1]) throw new IOException("安装包文件缺失或校验失败：" + f[0]);
                if (File.Exists(target)) throw new IOException("目标文件已经存在，拒绝覆盖：" + f[0]);
            }
            Directory.CreateDirectory(marker);
            string backup = Inside(root, MarkerDirectory + "/存档备份-" + DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyyMMdd-HHmmss"));
            int saveCount = 0;
            if (!String.IsNullOrEmpty(savePath) && Directory.Exists(savePath))
            {
                foreach (string file in Directory.GetFiles(savePath, "*", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(file).StartsWith("Player", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(file).Equals(".log", StringComparison.OrdinalIgnoreCase)) continue;
                    RejectLinks(savePath, file);
                    string rel = file.Substring(savePath.TrimEnd(Path.DirectorySeparatorChar).Length + 1);
                    string dest = Inside(backup, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(file, dest, false);
                    saveCount++;
                }
            }
            List<string[]> copied = new List<string[]>();
            try
            {
                // 先保存恢复清单，逐个复制；加载入口最后写入。
                File.WriteAllLines(manifest, files.Select(f => String.Join("\t", f)), new UTF8Encoding(true));
                foreach (string[] f in files.OrderBy(f => f[0].Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
                {
                    string target = Inside(root, f[0]);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(Inside(payload, f[0]), target, false);
                    copied.Add(f);
                    if (Hash(target) != f[1]) throw new IOException("写入校验失败：" + f[0]);
                }
                File.WriteAllText(Inside(root, MarkerDirectory + "/安装记录.txt"),
                    "伊始之地中文修改器 1.0.0\r\n适配构建：22566272\r\n原始游戏代码未替换。\r\n" +
                    "存档备份来源：" + savePath + "\r\n备份文件数：" + saveCount + "\r\n备份目录：" + backup + "\r\n", new UTF8Encoding(true));
                return "安装完成。照常启动游戏，按 F8 打开面板。\r\n" + (saveCount > 0 ? "已备份 " + saveCount + " 个存档文件：\r\n" + backup : "未找到可备份的本地存档；首次游玩通常还没有存档。") + "\r\n全部修改默认关闭。";
            }
            catch
            {
                // 对已写入的文件做恢复搬移，保留故障现场及存档备份。
                string failed = Inside(root, MarkerDirectory + "/安装失败恢复-" + DateTime.UtcNow.Ticks);
                foreach (string[] f in copied)
                {
                    string target = Inside(root, f[0]);
                    if (!File.Exists(target)) continue;
                    string dest = Inside(failed, f[0]);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Move(target, dest);
                }
                if (File.Exists(manifest)) File.Move(manifest, Inside(root, MarkerDirectory + "/失败清单-" + DateTime.UtcNow.Ticks + ".tsv"));
                CleanEmptyDirectories(root, files);
                throw;
            }
        }
        private static void CleanEmptyDirectories(string root, List<string[]> files)
        {
            HashSet<string> directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string[] f in files)
            {
                string dir = Path.GetDirectoryName(Inside(root, f[0]));
                while (dir != null && !dir.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    directories.Add(dir);
                    dir = Path.GetDirectoryName(dir);
                }
            }
            foreach (string dir in directories.OrderByDescending(x => x.Length))
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir, false);
        }
        public static string Uninstall(string root)
        {
            root = Path.GetFullPath(root);
            EnsureClosed(root);
            string manifest = Inside(root, MarkerDirectory + "/installed.tsv");
            RejectLinks(root, manifest);
            if (!File.Exists(manifest)) throw new IOException("此目录没有本工具的安装记录，无法自动卸载。");
            List<string[]> files = ReadManifest(manifest);
            HashSet<string> owned = new HashSet<string>(files.Select(x => Inside(root, x[0])), StringComparer.OrdinalIgnoreCase);
            string plugins = Inside(root, "BepInEx/plugins");
            bool others = Directory.Exists(plugins) && Directory.GetFiles(plugins, "*.dll", SearchOption.AllDirectories).Any(x => !owned.Contains(x));
            List<string[]> remove = others ? files.Where(f => f[0].StartsWith("BepInEx/plugins/TerraNilTrainer/", StringComparison.OrdinalIgnoreCase)).ToList() : files;
            foreach (string[] f in remove)
            {
                string target = Inside(root, f[0]);
                RejectLinks(root, target);
                if (File.Exists(target) && Hash(target) != f[1]) throw new IOException("文件已被其他程序修改，拒绝搬移：" + f[0] + "。请先检查该文件。");
            }
            string recovery = Inside(root, MarkerDirectory + "/卸载恢复-" + DateTime.UtcNow.Ticks);
            Directory.CreateDirectory(recovery);
            // 优先移走加载入口；有其他插件时保留共享框架。
            foreach (string[] f in remove.OrderBy(f => f[0].Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                string target = Inside(root, f[0]);
                if (!File.Exists(target)) continue;
                string dest = Inside(recovery, f[0]);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Move(target, dest);
            }
            if (others)
                File.WriteAllLines(manifest, files.Except(remove).Select(f => String.Join("\t", f)), new UTF8Encoding(true));
            else File.Move(manifest, Inside(recovery, "installed.tsv"));
            CleanEmptyDirectories(root, remove);
            return "修改器已卸载。" + (others ? "检测到其他插件，共享加载框架已保留。" : "加载入口已移走。") + "\r\n存档及备份已保留。卸载文件可从以下目录找回：\r\n" + recovery;
        }
    }
    internal sealed class ManagerForm : Form
    {
        private TextBox folder, info;
        private Button install, uninstall, launch;
        private string Payload { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "载荷"); } }
        public ManagerForm()
        {
            Text = "伊始之地 · 中文修改器安装与管理";
            Font = new Font("Microsoft YaHei UI", 10);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 570);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            Label heading = new Label { Text = "伊始之地中文修改器", Font = new Font(Font.FontFamily, 20, FontStyle.Bold), Location = new Point(24, 20), AutoSize = true };
            Controls.Add(heading);
            Controls.Add(new Label { Text = "适配当前构建 22566272　｜　安装后随游戏加载　｜　F8 打开中文面板", Location = new Point(26, 72), Size = new Size(720, 30) });
            Controls.Add(new Label { Text = "游戏目录", Location = new Point(26, 119), AutoSize = true });
            folder = new TextBox { Text = @"D:\SteamLibrary\steamapps\common\Terra Nil", Location = new Point(110, 114), Size = new Size(510, 28) };
            Controls.Add(folder);
            Button browse = new Button { Text = "选择目录", Location = new Point(628, 112), Size = new Size(106, 33) };
            browse.Click += delegate { using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "选择包含 Terra Nil.exe 的游戏目录", SelectedPath = folder.Text }) if (dialog.ShowDialog(this) == DialogResult.OK) { folder.Text = dialog.SelectedPath; Inspect(); } };
            Controls.Add(browse);
            Controls.Add(new Label { Text = "六项功能：设置叶子货币、灌溉范围、回收范围、建筑范围、任意位置建造、忽略建造条件。\r\n安装仅添加插件和加载框架；安装前自动备份本地存档。范围倍率为 1 到 5 倍。", Location = new Point(26, 162), Size = new Size(710, 64) });
            info = new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Location = new Point(26, 236), Size = new Size(708, 225), BackColor = Color.White };
            Controls.Add(info);
            Button inspect = MakeButton("检查与预览", 26, delegate { Inspect(); });
            install = MakeButton("安装修改器", 204, delegate { Execute(delegate {
                string savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Free Lives", "Terra Nil");
                return InstallEngine.Install(folder.Text, Payload, savePath); }); });
            uninstall = MakeButton("卸载修改器", 382, delegate { Execute(delegate { return InstallEngine.Uninstall(folder.Text); }); });
            launch = MakeButton("启动游戏", 560, delegate { Execute(delegate {
                string exe = InstallEngine.Inside(folder.Text, "Terra Nil.exe");
                if (!File.Exists(exe)) throw new IOException("未找到游戏程序。");
                // 使用游戏平台原有启动流程。
                Process.Start(new ProcessStartInfo("steam://run/1593030") { UseShellExecute = true });
                return "已向游戏平台提交启动请求。进入游戏后按 F8 打开面板。"; }); });
            Controls.Add(new Label { Text = "卸载不回退已保存的货币与地形变化。恢复存档前请退出游戏，并保留当前存档副本。", Location = new Point(26, 535), Size = new Size(720, 25) });
            Shown += delegate { Inspect(); };
        }
        private Button MakeButton(string title, int x, Action action)
        {
            Button button = new Button { Text = title, Location = new Point(x, 480), Size = new Size(174, 40) };
            button.Click += delegate { action(); };
            Controls.Add(button);
            return button;
        }
        private void Execute(Func<string> action)
        {
            Enabled = false;
            Cursor = Cursors.WaitCursor;
            try { info.Text = action(); }
            catch (Exception ex) { info.Text = "操作未完成：\r\n" + ex.Message; }
            finally { Enabled = true; Cursor = Cursors.Default; }
        }
        private void Inspect()
        {
            try
            {
                InstallEngine.CheckGame(folder.Text);
                bool installed = File.Exists(InstallEngine.Inside(folder.Text, InstallEngine.MarkerDirectory + "/installed.tsv"));
                info.Text = "游戏版本校验通过。\r\n游戏目录：" + Path.GetFullPath(folder.Text) + "\r\n" +
                    (installed ? "已找到本工具安装记录。可直接启动游戏，或退出游戏后卸载。" : "当前尚未安装。本次检查没有写入游戏目录。\r\n点击“安装修改器”将添加加载框架及插件，并备份本地存档。") +
                    "\r\n\r\n安装后：\r\n1. 正常启动游戏。\r\n2. F8 打开/隐藏面板。\r\n3. 进入关卡后设置货币；选择倍率或开关。\r\n4. 隐藏面板后建造或回收。\r\n\r\n全部功能初始关闭，每次重启恢复默认。";
            }
            catch (Exception ex) { info.Text = "检查结果：\r\n" + ex.Message; }
        }
    }
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ManagerForm());
        }
    }
}
