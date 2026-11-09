using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace GameRenderLab.Endfield
{
    internal static class ReShadeLayer
    {
        private const string DllHash = "0cee63f9c9f13f3ac909c5b4903f4dbb4b719a7ab3b4f13b0deaf83c814b94f7";
        private const string JsonHash = "aa21713718843e531da396e2bfc80772c9cb3c369d6c30b836be6b0ae812d503";
        private const string Dll32Hash = "da430e0a9c6eecefa0d1b27d05e16c426fb5d04e808b194d914eaac4b31bc0f8";
        private const string Json32Hash = "dbfdbadb8c64b398b2f8bf30469841ab8c35e10c9da40ba69b966559d5fa15f6";
        private const string RegistryPath = @"SOFTWARE\Khronos\Vulkan\ImplicitLayers";
        private static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ReShade");
        private static string AppsPath { get { return FileSafety.Under(DirectoryPath, "ReShadeApps.ini"); } }

        internal static bool CheckCompatible()
        {
            string manifest = FileSafety.Under(DirectoryPath, "ReShade64.json");
            string module = FileSafety.Under(DirectoryPath, "ReShade64.dll");
            bool registered = false;
            using (var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var layers = hive.OpenSubKey(RegistryPath))
            {
                if (layers != null)
                {
                    foreach (string value in layers.GetValueNames())
                    {
                        if (!String.Equals(Path.GetFileName(value), "ReShade64.json", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!String.Equals(value, manifest, StringComparison.OrdinalIgnoreCase) || !Object.Equals(layers.GetValue(value), 0))
                            throw new IOException("发现不同的系统级 ReShade Vulkan 注册。为保护其他游戏，安装未继续。");
                        registered = true;
                    }
                }
            }
            if (File.Exists(module) && !FileSafety.HashMatches(module, DllHash) ||
                File.Exists(manifest) && !FileSafety.HashMatches(manifest, JsonHash))
                throw new IOException("系统已有其他版本的共享 ReShade。为保护其他游戏，安装未继续。");
            bool complete = FileSafety.HashMatches(module, DllHash) && FileSafety.HashMatches(manifest, JsonHash);
            if (!complete || !registered)
            {
                string module32 = FileSafety.Under(DirectoryPath, "ReShade32.dll");
                string manifest32 = FileSafety.Under(DirectoryPath, "ReShade32.json");
                if (File.Exists(module32) && !FileSafety.HashMatches(module32, Dll32Hash) ||
                    File.Exists(manifest32) && !FileSafety.HashMatches(manifest32, Json32Hash))
                    throw new IOException("系统已有其他版本的 32 位共享 ReShade。为保护其他游戏，安装未继续。");
                CheckExistingLegacyLayers(manifest32);
            }
            if (!complete && File.Exists(AppsPath))
            {
                var existing = new IniText(File.ReadAllText(AppsPath));
                if (ParseList(existing.Value("", "Apps")).Count != 0)
                    throw new IOException("共享 ReShade 的安装不完整，但仍有已注册的游戏。请先通过官方安装器修复。");
            }
            return complete && registered;
        }

        private static void CheckExistingLegacyLayers(string expected32Manifest)
        {
            string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReShade");
            if (File.Exists(Path.Combine(legacy, "ReShadeApps.ini")))
                throw new IOException("发现旧的用户级 ReShade 应用配置。请先通过其官方安装器迁移，避免影响其他游戏。");
            string legacyPrefix = Path.GetFullPath(legacy).TrimEnd('\\') + "\\";
            foreach (RegistryHive scope in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                    using (var hive = RegistryKey.OpenBaseKey(scope, view))
                        foreach (string registry in new[] { RegistryPath, @"SOFTWARE\Khronos\Vulkan\ExplicitLayers" })
                            using (var key = hive.OpenSubKey(registry))
                            {
                                if (key == null) continue;
                                foreach (string entry in key.GetValueNames())
                                {
                                    if (entry.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase))
                                        throw new IOException("发现旧的用户级 ReShade Vulkan 注册。请先通过其官方安装器迁移。");
                                    if (String.Equals(Path.GetFileName(entry), "ReShade32.json", StringComparison.OrdinalIgnoreCase) &&
                                        (!String.Equals(entry, expected32Manifest, StringComparison.OrdinalIgnoreCase) || !Object.Equals(key.GetValue(entry), 0)))
                                        throw new IOException("发现不同的 32 位 ReShade Vulkan 注册。为保护其他游戏，安装未继续。");
                                }
                            }
        }

        internal static void Install(string setup, string executable)
        {
            if (!Program.IsAdministrator() || !FileSafety.HashMatches(setup, Deployment.SetupHash))
                throw new IOException("ReShade 安装程序或权限检查失败。");
            string arguments = "\"" + executable + "\" --api vulkan --headless --elevated";
            if (File.Exists(Path.Combine(Path.GetDirectoryName(executable), "ReShade.ini"))) arguments += " --state update";
            using (Process process = Process.Start(new ProcessStartInfo(setup, arguments)
            {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(setup)
            }))
            {
                if (process == null) throw new IOException("无法启动官方 ReShade 安装程序。");
                process.WaitForExit();
                if (process.ExitCode != 0) throw new IOException("官方 ReShade 安装失败，退出代码 " + process.ExitCode + "。");
            }
            if (!CheckCompatible() || !IsAllowed(executable)) throw new IOException("ReShade Vulkan 注册验证失败。");
        }

        internal static bool IsAllowed(string executable)
        {
            if (!File.Exists(AppsPath)) return false;
            var ini = new IniText(File.ReadAllText(AppsPath));
            return ParseList(ini.Value("", "Apps")).Contains(executable, StringComparer.OrdinalIgnoreCase);
        }

        internal static void AddAllowed(string executable)
        {
            var ini = new IniText(File.Exists(AppsPath) ? File.ReadAllText(AppsPath) : "");
            var apps = ParseList(ini.Value("", "Apps"));
            if (apps.Contains(executable, StringComparer.OrdinalIgnoreCase)) return;
            apps.Add(executable);
            ini.Set("", "Apps", SerializeList(apps));
            FileSafety.AtomicWrite(AppsPath, Encoding.UTF8.GetBytes(ini.ToString()));
        }

        internal static void RemoveAllowed(string executable)
        {
            if (!File.Exists(AppsPath)) return;
            var ini = new IniText(File.ReadAllText(AppsPath));
            var apps = ParseList(ini.Value("", "Apps"));
            int removed = apps.RemoveAll(value => String.Equals(value, executable, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return;
            ini.Set("", "Apps", SerializeList(apps));
            FileSafety.AtomicWrite(AppsPath, Encoding.UTF8.GetBytes(ini.ToString()));
        }

        internal static List<string> ParseList(string source)
        {
            var result = new List<string>();
            if (String.IsNullOrEmpty(source)) return result;
            var entry = new StringBuilder();
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] != ',') { entry.Append(source[i]); continue; }
                if (i + 1 < source.Length && source[i + 1] == ',') { entry.Append(','); i++; continue; }
                if (entry.Length != 0) result.Add(entry.ToString());
                entry.Clear();
            }
            if (entry.Length != 0) result.Add(entry.ToString());
            return result;
        }

        internal static string SerializeList(IEnumerable<string> values)
        {
            return String.Join(",", values.Select(value => value.Replace(",", ",,")));
        }

        internal static void MakeUserWritable(string path)
        {
            // Match the official setup's scope: allow the current user to save the game-specific INI.
            var acl = File.GetAccessControl(path);
            using (var identity = WindowsIdentity.GetCurrent())
                acl.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.Modify, AccessControlType.Allow));
            File.SetAccessControl(path, acl);
        }
    }

    internal sealed class IniText
    {
        private readonly List<string> lines;

        internal IniText(string text)
        {
            lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
            if (lines.Count == 1 && lines[0].Length == 0) lines.Clear();
        }

        private void Section(string name, out int first, out int last)
        {
            first = name.Length == 0 ? 0 : -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Trim();
                if (!line.StartsWith("[") || !line.EndsWith("]")) continue;
                if (first >= 0) { last = i; return; }
                if (String.Equals(line.Substring(1, line.Length - 2).Trim(), name, StringComparison.OrdinalIgnoreCase)) first = i + 1;
            }
            last = lines.Count;
        }

        private int Index(string section, string key, out int end)
        {
            int start;
            Section(section, out start, out end);
            if (start < 0) return -1;
            for (int i = end - 1; i >= start; i--)
            {
                int delimiter = lines[i].IndexOf('=');
                if (delimiter >= 0 && String.Equals(lines[i].Substring(0, delimiter).Trim(), key, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        internal string Value(string section, string key)
        {
            int end;
            int index = Index(section, key, out end);
            return index < 0 ? null : lines[index].Substring(lines[index].IndexOf('=') + 1).Trim();
        }

        internal void SetIfMissing(string section, string key, string value)
        {
            if (Value(section, key) == null) Set(section, key, value);
        }

        internal void Set(string section, string key, string value)
        {
            int first, end;
            Section(section, out first, out end);
            if (first < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Length > 0) lines.Add("");
                lines.Add("[" + section + "]");
                lines.Add(key + "=" + value);
                return;
            }
            int index = Index(section, key, out end);
            if (index >= 0) lines[index] = key + "=" + value;
            else lines.Insert(end, key + "=" + value);
        }

        public override string ToString() { return String.Join("\r\n", lines).TrimEnd('\r', '\n') + "\r\n"; }
    }
}
