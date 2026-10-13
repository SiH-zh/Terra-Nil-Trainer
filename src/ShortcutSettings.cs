using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TerraNilTrainer
{
    public sealed class ShortcutChoice
    {
        public string MainKey = "F6";
        public bool Control, Shift, Alt;
        public string Describe()
        {
            List<string> parts = new List<string>();
            if (Control) parts.Add("Ctrl（控制键）");
            if (Shift) parts.Add("Shift（上档键）");
            if (Alt) parts.Add("Alt（交替键）");
            parts.Add(ShortcutSettings.KeyLabel(MainKey));
            return String.Join("＋", parts.ToArray());
        }
    }
    public sealed class ShortcutOption
    {
        public string Key;
        public ShortcutOption(string key) { Key = key; }
        public override string ToString() { return ShortcutSettings.KeyLabel(Key); }
    }
    public static class ShortcutSettings
    {
        public const string Section = "快捷键", MainEntry = "面板快捷键";
        public const string ControlEntry = "需要控制键", ShiftEntry = "需要上档键", AltEntry = "需要交替键";
        public const string RelativePath = "BepInEx/config/local.terranil.chinese.trainer.cfg";
        public static string[] AllowedKeys()
        {
            return Enumerable.Range(1, 12).Select(n => "F" + n)
                .Concat(Enumerable.Range('A', 26).Select(n => ((char)n).ToString()))
                .Concat(Enumerable.Range(0, 10).Select(n => "Digit" + n))
                .Concat(new string[] { "Space", "Tab", "Home", "End", "PageUp", "PageDown", "Insert", "Delete", "UpArrow", "DownArrow", "LeftArrow", "RightArrow" }).ToArray();
        }
        public static bool IsSupported(string key) { return AllowedKeys().Contains(key); }
        public static string KeyLabel(string key)
        {
            if (key.StartsWith("Digit", StringComparison.Ordinal)) return "数字 " + key.Substring(5);
            switch (key)
            {
                case "Space": return "空格"; case "Tab": return "制表键";
                case "Home": return "起始键"; case "End": return "末尾键";
                case "PageUp": return "向上翻页"; case "PageDown": return "向下翻页";
                case "Insert": return "插入键"; case "Delete": return "删除键";
                case "UpArrow": return "↑"; case "DownArrow": return "↓";
                case "LeftArrow": return "←"; case "RightArrow": return "→";
                default: return key;
            }
        }
        public static ShortcutChoice Read(string path)
        {
            ShortcutChoice choice = new ShortcutChoice();
            if (!File.Exists(path)) return choice;
            bool section = false;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                string text = line.Trim();
                if (text.StartsWith("[")) { section = text == "[" + Section + "]"; continue; }
                if (!section || text.StartsWith("#")) continue;
                int separator = text.IndexOf('=');
                if (separator < 0) continue;
                string name = text.Substring(0, separator).Trim(), value = text.Substring(separator + 1).Trim();
                bool flag;
                if (name == MainEntry && IsSupported(value)) choice.MainKey = value;
                else if (Boolean.TryParse(value, out flag))
                {
                    if (name == ControlEntry) choice.Control = flag;
                    if (name == ShiftEntry) choice.Shift = flag;
                    if (name == AltEntry) choice.Alt = flag;
                }
            }
            return choice;
        }
        public static void Write(string path, ShortcutChoice choice)
        {
            if (choice == null || !IsSupported(choice.MainKey)) throw new IOException("不支持这个主按键，请重新选择。");
            Dictionary<string, string> desired = new Dictionary<string, string> {
                { MainEntry, choice.MainKey }, { ControlEntry, choice.Control.ToString().ToLowerInvariant() },
                { ShiftEntry, choice.Shift.ToString().ToLowerInvariant() }, { AltEntry, choice.Alt.ToString().ToLowerInvariant() } };
            List<string> output = new List<string>();
            HashSet<string> written = new HashSet<string>();
            bool section = false, found = false;
            Action appendMissing = delegate { foreach (var p in desired) if (written.Add(p.Key)) output.Add(p.Key + " = " + p.Value); };
            foreach (string line in File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8) : new string[0])
            {
                string text = line.Trim();
                if (text.StartsWith("["))
                {
                    if (section) appendMissing();
                    section = text == "[" + Section + "]";
                    found |= section;
                    output.Add(line); continue;
                }
                int separator = text.IndexOf('=');
                string name = separator >= 0 ? text.Substring(0, separator).Trim() : "";
                if (section && !text.StartsWith("#") && desired.ContainsKey(name))
                {
                    if (written.Add(name)) output.Add(name + " = " + desired[name]);
                }
                else output.Add(line);
            }
            if (!found) { output.Add(""); output.Add("# 伊始之地修改器：面板快捷键设置"); output.Add("[" + Section + "]"); }
            appendMissing();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string pending = path + ".pending-" + Guid.NewGuid().ToString("N");
            File.WriteAllLines(pending, output, new UTF8Encoding(true));
            if (File.Exists(path)) File.Replace(pending, path, null); else File.Move(pending, path);
        }
    }
}
