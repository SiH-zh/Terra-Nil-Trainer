using System;
using System.Collections.Generic;
using System.ComponentModel;
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
            if (File.Exists(manifest)) return UpgradePlugin(root, payload, manifest, savePath);
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
            string backup = Inside(root, MarkerDirectory + "/存档备份-" + DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
                    "伊始之地中文修改器 1.2.0\r\n适配构建：22566272\r\n原始游戏代码未替换。\r\n" +
                    "存档备份来源：" + savePath + "\r\n备份文件数：" + saveCount + "\r\n备份目录：" + backup + "\r\n", new UTF8Encoding(true));
                return "安装完成。照常启动游戏，按 F6 打开面板。\r\n" + (saveCount > 0 ? "已备份 " + saveCount + " 个存档文件：\r\n" + backup : "未找到可备份的本地存档；首次游玩通常还没有存档。") + "\r\n全部修改默认关闭。";
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
        private static string UpgradePlugin(string root, string payload, string manifest, string savePath)
        {
            const string relative = "BepInEx/plugins/TerraNilTrainer/TerraNilTrainer.dll";
            string target = Inside(root, relative);
            RejectLinks(root, target);
            List<string[]> installed = ReadManifest(manifest);
            List<string[]> package = ReadManifest(Path.Combine(payload, "package.tsv"));
            foreach (string[] f in package)
                if (!File.Exists(Inside(payload, f[0])) || Hash(Inside(payload, f[0])) != f[1])
                    throw new IOException("安装包文件缺失或校验失败：" + f[0]);
            string[] newRecord = package.FirstOrDefault(f => f[0] == relative);
            if (newRecord == null) throw new IOException("安装包缺少插件校验记录。");
            string[] oldRecord = installed.FirstOrDefault(f => f[0] == relative);
            if (File.Exists(target))
            {
                if (oldRecord == null || Hash(target) != oldRecord[1]) throw new IOException("现有插件已被其他程序修改，拒绝覆盖。请先核对插件文件。");
                if (oldRecord[1] == newRecord[1]) return "当前已是相同插件版本，无需重复安装。\r\n照常启动游戏，按 F6 打开面板。";
            }
            foreach (string required in new string[] { "BepInEx/core/BepInEx.dll", "BepInEx/core/0Harmony.dll" })
            {
                string[] record = package.FirstOrDefault(f => f[0] == required);
                string path = Inside(root, required);
                RejectLinks(root, path);
                if (record == null || !File.Exists(path) || Hash(path) != record[1])
                    throw new IOException("共享加载库与本安装包不匹配，已停止升级：" + required);
            }
            string backup = Inside(root, MarkerDirectory + "/升级备份-" + DateTime.UtcNow.Ticks);
            Directory.CreateDirectory(backup);
            string oldPlugin = Path.Combine(backup, "TerraNilTrainer.dll");
            string oldManifest = Path.Combine(backup, "installed.tsv");
            bool existed = File.Exists(target);
            if (existed) File.Copy(target, oldPlugin, false);
            File.Copy(manifest, oldManifest, false);
            if (!String.IsNullOrEmpty(savePath) && Directory.Exists(savePath))
                foreach (string file in Directory.GetFiles(savePath, "*", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(file).StartsWith("Player", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(file).Equals(".log", StringComparison.OrdinalIgnoreCase)) continue;
                    RejectLinks(savePath, file);
                    string rel = file.Substring(savePath.TrimEnd(Path.DirectorySeparatorChar).Length + 1);
                    string dest = Inside(Path.Combine(backup, "存档"), rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(file, dest, false);
                }
            string pending = manifest + ".pending-" + DateTime.UtcNow.Ticks;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(Inside(payload, relative), target, true);
                if (Hash(target) != newRecord[1]) throw new IOException("升级插件写入校验失败。");
                List<string[]> updated = installed.Where(f => f[0] != relative).ToList();
                updated.Add(newRecord);
                File.WriteAllLines(pending, updated.Select(f => String.Join("\t", f)), new UTF8Encoding(true));
                File.Replace(pending, manifest, null);
            }
            catch
            {
                if (existed) File.Copy(oldPlugin, target, true);
                else if (File.Exists(target)) File.Move(target, Path.Combine(backup, "失败的新插件.dll"));
                File.Copy(oldManifest, manifest, true);
                throw;
            }
            return "插件安装／升级完成。已加入自定义快捷键，保留动物与建造功能。\r\n面板默认使用 F6，可在工具或游戏内设置其他按键。\r\n旧插件、安装记录及找到的当前存档已备份到：\r\n" + backup + "\r\n升级没有替换共享加载框架。";
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
        public static string ConfigureShortcut(string root, string payload, ShortcutChoice choice)
        {
            CheckGame(root);
            EnsureClosed(root);
            const string relative = "BepInEx/plugins/TerraNilTrainer/TerraNilTrainer.dll";
            string plugin = Inside(root, relative), supplied = Inside(payload, relative);
            if (!File.Exists(plugin) || !File.Exists(supplied) || Hash(plugin) != Hash(supplied))
                throw new IOException("请先点击“安装／更新修改器”安装本版，再保存快捷键。");
            string config = Inside(root, ShortcutSettings.RelativePath);
            RejectLinks(root, config);
            if (File.Exists(config))
            {
                string backup = Inside(root, MarkerDirectory + "/快捷键备份-" + DateTime.UtcNow.Ticks + "/原配置.cfg");
                Directory.CreateDirectory(Path.GetDirectoryName(backup));
                File.Copy(config, backup, false);
            }
            ShortcutSettings.Write(config, choice);
            return "面板快捷键已保存为 " + choice.Describe() + "。\r\n下一次启动游戏时生效。";
        }
    }
    internal sealed class ManagerForm : Form
    {
        private TextBox folder, info;
        private Button install, uninstall, launch, inspect, browse;
        private Label checkStatus;
        private ComboBox hotkey;
        private CheckBox hotkeyControl, hotkeyShift, hotkeyAlt;
        private Button saveHotkey;
        private bool inspecting;
        private int checkNumber;
        private string Payload { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "载荷"); } }
        public ManagerForm()
        {
            Text = "伊始之地 · 中文修改器安装与管理（自定义快捷键版 1.2.0）";
            Font = new Font("Microsoft YaHei UI", 10);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 625);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            Label heading = new Label { Text = "伊始之地中文修改器", Font = new Font(Font.FontFamily, 20, FontStyle.Bold), Location = new Point(24, 20), AutoSize = true };
            Controls.Add(heading);
            Controls.Add(new Label { Text = "适配当前构建 22566272　｜　默认 F6 打开面板　｜　支持单键及组合键", Location = new Point(26, 72), Size = new Size(720, 30) });
            Controls.Add(new Label { Text = "游戏目录", Location = new Point(26, 119), AutoSize = true });
            folder = new TextBox { Text = @"D:\SteamLibrary\steamapps\common\Terra Nil", Location = new Point(110, 114), Size = new Size(510, 28) };
            Controls.Add(folder);
            browse = new Button { Text = "选择目录", Location = new Point(628, 112), Size = new Size(106, 33) };
            browse.Click += delegate { using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "选择包含 Terra Nil.exe 的游戏目录", SelectedPath = folder.Text }) if (dialog.ShowDialog(this) == DialogResult.OK) { folder.Text = dialog.SelectedPath; Inspect(); } };
            Controls.Add(browse);
            Controls.Add(new Label { Text = "面板快捷键", Location = new Point(26, 164), AutoSize = true });
            hotkey = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(114, 159), Size = new Size(90, 30) };
            foreach (string key in ShortcutSettings.AllowedKeys()) hotkey.Items.Add(new ShortcutOption(key));
            Controls.Add(hotkey);
            hotkeyControl = new CheckBox { Text = "Ctrl 控制键", Location = new Point(215, 159), Size = new Size(112, 30) };
            hotkeyShift = new CheckBox { Text = "Shift 上档键", Location = new Point(327, 159), Size = new Size(121, 30) };
            hotkeyAlt = new CheckBox { Text = "Alt 交替键", Location = new Point(448, 159), Size = new Size(110, 30) };
            Controls.Add(hotkeyControl); Controls.Add(hotkeyShift); Controls.Add(hotkeyAlt);
            saveHotkey = new Button { Text = "保存快捷键", Location = new Point(560, 157), Size = new Size(174, 34) };
            saveHotkey.Click += delegate { Execute(delegate { return InstallEngine.ConfigureShortcut(folder.Text, Payload, SelectedShortcut()); }); };
            Controls.Add(saveHotkey);
            Controls.Add(new Label { Text = "也可在游戏面板中点击“自定义快捷键”录制。此处保存后，下次启动游戏生效。", Location = new Point(26, 198), Size = new Size(710, 30) });
            Controls.Add(new Label { Text = "功能：货币、三类范围、建造限制；动物心情拉满、持续保持与迁移位置推荐。\r\n首次安装自动备份存档；已有本工具安装记录时可直接升级插件，保留旧插件备份。", Location = new Point(26, 233), Size = new Size(710, 57) });
            checkStatus = new Label { Text = "尚未检查：点击“检查与预览”开始。", Location = new Point(26, 297), Size = new Size(708, 29), Font = new Font(Font, FontStyle.Bold), ForeColor = Color.DimGray };
            Controls.Add(checkStatus);
            info = new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Location = new Point(26, 335), Size = new Size(708, 169), BackColor = Color.White,
                Text = "点击下方“检查与预览”，检查游戏目录与版本。\r\n检查只读取文件；完成后会显示结果和下一步操作。" };
            Controls.Add(info);
            inspect = MakeButton("检查与预览", 26, delegate { Inspect(); });
            install = MakeButton("安装／更新修改器", 204, delegate { Execute(delegate {
                string savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Free Lives", "Terra Nil");
                string result = InstallEngine.Install(folder.Text, Payload, savePath);
                return result + "\r\n\r\n" + InstallEngine.ConfigureShortcut(folder.Text, Payload, SelectedShortcut()); }); });
            uninstall = MakeButton("卸载修改器", 382, delegate { Execute(delegate { return InstallEngine.Uninstall(folder.Text); }); });
            launch = MakeButton("启动游戏", 560, delegate { Execute(delegate {
                string exe = InstallEngine.Inside(folder.Text, "Terra Nil.exe");
                if (!File.Exists(exe)) throw new IOException("未找到游戏程序。");
                // 使用游戏平台原有启动流程。
                Process.Start(new ProcessStartInfo("steam://run/1593030") { UseShellExecute = true });
                return "已向游戏平台提交启动请求。面板快捷键：" + SelectedShortcut().Describe(); }); });
            Controls.Add(new Label { Text = "卸载不回退已保存的货币与地形变化。恢复存档前请退出游戏，并保留当前存档副本。", Location = new Point(26, 580), Size = new Size(720, 25) });
            LoadShortcutControls(folder.Text);
        }
        private ShortcutChoice SelectedShortcut()
        {
            ShortcutOption option = hotkey.SelectedItem as ShortcutOption;
            return new ShortcutChoice { MainKey = option != null ? option.Key : "F6", Control = hotkeyControl.Checked, Shift = hotkeyShift.Checked, Alt = hotkeyAlt.Checked };
        }
        private void LoadShortcutControls(string root)
        {
            ShortcutChoice choice;
            try { choice = ShortcutSettings.Read(InstallEngine.Inside(root, ShortcutSettings.RelativePath)); }
            catch { choice = new ShortcutChoice(); }
            foreach (ShortcutOption option in hotkey.Items) if (option.Key == choice.MainKey) { hotkey.SelectedItem = option; break; }
            hotkeyControl.Checked = choice.Control; hotkeyShift.Checked = choice.Shift; hotkeyAlt.Checked = choice.Alt;
        }
        private Button MakeButton(string title, int x, Action action)
        {
            Button button = new Button { Text = title, Location = new Point(x, 520), Size = new Size(174, 40) };
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
            if (inspecting) return;
            inspecting = true;
            int number = ++checkNumber;
            string root = folder.Text;
            inspect.Text = "正在检查……";
            inspect.Enabled = install.Enabled = uninstall.Enabled = launch.Enabled = browse.Enabled = folder.Enabled = false;
            hotkey.Enabled = hotkeyControl.Enabled = hotkeyShift.Enabled = hotkeyAlt.Enabled = saveHotkey.Enabled = false;
            Cursor = Cursors.WaitCursor;
            checkStatus.ForeColor = Color.DarkBlue;
            checkStatus.Text = "第 " + number + " 次检查：正在核对目录和游戏版本……";
            info.Text = "正在检查：\r\n" + root + "\r\n\r\n检查在后台执行，窗口仍可移动和关闭。";
            BackgroundWorker worker = new BackgroundWorker();
            worker.DoWork += delegate(object sender, DoWorkEventArgs e)
            {
                InstallEngine.CheckGame(root);
                bool installed = File.Exists(InstallEngine.Inside(root, InstallEngine.MarkerDirectory + "/installed.tsv"));
                e.Result = "游戏版本校验通过。\r\n游戏目录：" + Path.GetFullPath(root) + "\r\n" +
                    (installed ? "已找到本工具安装记录。退出游戏后点击“安装／更新修改器”升级插件；无需先卸载。" : "当前尚未安装。本次检查没有写入游戏目录。\r\n点击“安装／更新修改器”将添加加载框架及插件，并备份本地存档。") +
                    "\r\n\r\n安装后：\r\n1. 正常启动游戏。\r\n2. 使用保存的快捷键打开/隐藏面板，默认 F6。\r\n3. 进入关卡后设置货币、建筑或动物功能。\r\n4. 游戏面板中的“自定义快捷键”可录制新组合。\r\n\r\n修改功能默认关闭；自定义快捷键会保留。";
            };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e)
            {
                try
                {
                    if (IsDisposed || Disposing) return;
                    string time = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("HH:mm:ss");
                    if (e.Error != null)
                    {
                        checkStatus.ForeColor = Color.Firebrick;
                        checkStatus.Text = "第 " + number + " 次检查未通过（北京时间 " + time + "）。";
                        info.Text = "检查未通过：\r\n" + e.Error.GetBaseException().Message + "\r\n\r\n请确认上方选的是包含 Terra Nil.exe 的游戏目录。";
                    }
                    else
                    {
                        LoadShortcutControls(root);
                        checkStatus.ForeColor = Color.DarkGreen;
                        checkStatus.Text = "第 " + number + " 次检查通过（北京时间 " + time + "）；详细结果如下。";
                        info.Text = (string)e.Result;
                    }
                    info.SelectionStart = 0;
                    info.SelectionLength = 0;
                    info.ScrollToCaret();
                    inspect.Text = "重新检查与预览";
                    inspect.Enabled = install.Enabled = uninstall.Enabled = launch.Enabled = browse.Enabled = folder.Enabled = true;
                    hotkey.Enabled = hotkeyControl.Enabled = hotkeyShift.Enabled = hotkeyAlt.Enabled = saveHotkey.Enabled = true;
                    Cursor = Cursors.Default;
                }
                finally { inspecting = false; worker.Dispose(); }
            };
            worker.RunWorkerAsync();
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
