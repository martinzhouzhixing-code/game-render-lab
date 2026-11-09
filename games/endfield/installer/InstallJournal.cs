using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GameRenderLab.Endfield
{
    internal sealed class InstallJournal
    {
        private readonly string store;
        private readonly string game;
        private readonly bool addedWhitelist;
        private readonly Dictionary<string, string> before;
        internal string Location { get; private set; }

        private InstallJournal(string store, string game, string location, bool addedWhitelist, Dictionary<string, string> before)
        {
            this.store = store;
            this.game = game;
            Location = location;
            this.addedWhitelist = addedWhitelist;
            this.before = before;
        }

        private static string Pointer(string store) { return FileSafety.Under(store, "active.txt"); }
        internal static bool Exists(string store) { return File.Exists(Pointer(store)); }

        internal static InstallJournal Begin(string store, string game, bool addedWhitelist)
        {
            if (Exists(store)) throw new IOException("另一个安装事务尚未回滚。");
            string name = "backup-" + Guid.NewGuid().ToString("N");
            string location = FileSafety.Under(store, name);
            Directory.CreateDirectory(location);
            var backup = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string relative in Deployment.ManagedFiles)
            {
                string source = FileSafety.Under(game, relative);
                if (File.Exists(source))
                {
                    string destination = FileSafety.Under(location, "original-" + relative);
                    File.Copy(source, destination);
                    backup.Add(relative, FileSafety.HashFile(destination));
                }
                else backup.Add(relative, "-");
            }
            FileSafety.AtomicWrite(FileSafety.Under(location, "game.txt"), Encoding.UTF8.GetBytes(game));
            FileSafety.AtomicWrite(FileSafety.Under(location, "whitelist.txt"), Encoding.ASCII.GetBytes(addedWhitelist ? "1" : "0"));
            WriteManifest(FileSafety.Under(location, "before.tsv"), backup);
            FileSafety.AtomicWrite(Pointer(store), Encoding.ASCII.GetBytes(name));
            return new InstallJournal(store, game, location, addedWhitelist, backup);
        }

        internal static InstallJournal Open(string store, string game)
        {
            if (!Exists(store)) throw new IOException("找不到此目录的安装记录。");
            string name = File.ReadAllText(Pointer(store));
            if (!Regex.IsMatch(name, "^backup-[a-f0-9]{32}$")) throw new IOException("安装记录不完整。");
            string location = FileSafety.Under(store, name);
            if (!String.Equals(File.ReadAllText(FileSafety.Under(location, "game.txt")), game, StringComparison.OrdinalIgnoreCase))
                throw new IOException("安装记录与游戏目录不匹配。");
            string flag = File.ReadAllText(FileSafety.Under(location, "whitelist.txt"));
            if (flag != "1" && flag != "0") throw new IOException("安装记录不完整。");
            var before = ReadManifest(FileSafety.Under(location, "before.tsv"));
            return new InstallJournal(store, game, location, flag == "1", before);
        }

        internal bool HadFile(string relative) { return before[relative] != "-"; }

        internal void Complete()
        {
            var installed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string relative in Deployment.ManagedFiles)
                installed.Add(relative, GetFileDigest(FileSafety.Under(game, relative)));
            WriteManifest(FileSafety.Under(Location, "after.tsv"), installed);
        }

        internal void Restore(bool requireClosed = true)
        {
            if (requireClosed) Deployment.RequireGameClosed();
            string committed = FileSafety.Under(Location, "after.tsv");
            var installed = File.Exists(committed) ? ReadManifest(committed) : null;
            // Validate every backup and check for other changes before touching any game file.
            foreach (string relative in Deployment.ManagedFiles)
            {
                string target = FileSafety.Under(game, relative);
                string current = GetFileDigest(target);
                if (installed != null && relative != "ReShade.ini" && current != installed[relative] && current != before[relative])
                    throw new IOException("文件已被其他程序更新，回滚未覆盖它：" + relative + "。备份：" + Location);
                if (before[relative] != "-" && !FileSafety.HashMatches(FileSafety.Under(Location, "original-" + relative), before[relative]))
                    throw new IOException("备份校验失败：" + relative + "。备份：" + Location);
            }

            foreach (string relative in Deployment.ManagedFiles)
            {
                string target = FileSafety.Under(game, relative);
                if (GetFileDigest(target) == before[relative]) continue;
                if (relative == "ReShade.ini" && File.Exists(target))
                {
                    string preserved = FileSafety.Under(Location, "post-install-ReShade.ini");
                    if (!File.Exists(preserved)) File.Copy(target, preserved);
                }
                if (before[relative] == "-") File.Delete(target);
                else FileSafety.AtomicCopy(FileSafety.Under(Location, "original-" + relative), target);
            }

            if (addedWhitelist) ReShadeLayer.RemoveAllowed(Path.Combine(game, "Endfield.exe"));
            File.Delete(Pointer(store));
        }

        private static string GetFileDigest(string path) { return File.Exists(path) ? FileSafety.HashFile(path) : "-"; }

        private static void WriteManifest(string file, Dictionary<string, string> records)
        {
            string content = String.Join("\n", records.Select(entry => entry.Key + "\t" + entry.Value)) + "\n";
            FileSafety.AtomicWrite(file, Encoding.ASCII.GetBytes(content));
        }

        private static Dictionary<string, string> ReadManifest(string file)
        {
            var records = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(file))
            {
                string[] fields = line.Split('\t');
                if (fields.Length != 2 || !Deployment.ManagedFiles.Contains(fields[0], StringComparer.Ordinal) ||
                    !Regex.IsMatch(fields[1], "^(-|[0-9a-f]{64})$") || records.ContainsKey(fields[0]))
                    throw new IOException("安装记录不完整。");
                records.Add(fields[0], fields[1]);
            }
            if (records.Count != Deployment.ManagedFiles.Length) throw new IOException("安装记录缺少文件。");
            return records;
        }
    }
}
