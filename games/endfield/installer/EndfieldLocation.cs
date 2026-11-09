using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace GameRenderLab.Endfield
{
    internal static class EndfieldLocation
    {
        private static readonly string[] GameNames = { "Arknights Endfield", "Endfield Game", "Endfield", "明日方舟终末地", "明日方舟：终末地" };
        private static readonly string[] SearchNames = { "games", "Game", "Hypergryph Launcher", "GRYPHLINE Launcher", "Gryphline Launcher", "GameLibrary", "Program Files", "Program Files (x86)" };

        internal static List<string> Find()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in Process.GetProcessesByName("Endfield"))
            {
                using (process)
                {
                    try { AddRoot(roots, Path.GetDirectoryName(process.MainModule.FileName)); }
                    catch (Exception error) { if (!IsAccessError(error)) throw; }
                }
            }

            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (RegistryKey registry = RegistryKey.OpenBaseKey(hive, view))
                        {
                            ReadLauncherKeys(registry, @"Software\Hypergryph\Launcher", roots);
                            ReadLauncherKeys(registry, @"Software\GRYPHLINE\Launcher", roots);
                            ReadUninstallKeys(registry, roots);
                        }
                    }
                    catch (Exception error) { if (!IsAccessError(error)) throw; }
                }
            }

            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady) AddRoot(roots, drive.RootDirectory.FullName);
            }

            var results = new List<string>();
            var checkedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
                Probe(root, 4, checkedDirectories, results);
            return results;
        }

        private static bool IsAccessError(Exception error)
        {
            return error is UnauthorizedAccessException || error is System.Security.SecurityException ||
                error is System.ComponentModel.Win32Exception || error is IOException || error is ArgumentException ||
                error is InvalidOperationException || error is NotSupportedException;
        }

        private static void AddRoot(HashSet<string> roots, string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return;
            try
            {
                string path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')));
                if (Directory.Exists(path)) roots.Add(path);
            }
            catch (Exception error) { if (!IsAccessError(error)) throw; }
        }

        private static void ReadLauncherKeys(RegistryKey registry, string name, HashSet<string> roots)
        {
            using (var key = registry.OpenSubKey(name))
            {
                if (key == null) return;
                AddRoot(roots, key.GetValue("install_path") as string);
                foreach (string product in key.GetSubKeyNames())
                {
                    using (var child = key.OpenSubKey(product))
                        if (child != null) AddRoot(roots, child.GetValue("install_path") as string);
                }
            }
        }

        private static void ReadUninstallKeys(RegistryKey registry, HashSet<string> roots)
        {
            using (var key = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
            {
                if (key == null) return;
                foreach (string name in key.GetSubKeyNames())
                {
                    using (var child = key.OpenSubKey(name))
                    {
                        if (child == null) continue;
                        string title = child.GetValue("DisplayName") as string;
                        if (title == null) continue;
                        string lower = title.ToLowerInvariant();
                        if (lower.Contains("endfield") || lower.Contains("hypergryph") || lower.Contains("gryphline") || title.Contains("终末地") || title.Contains("鹰角"))
                            AddRoot(roots, child.GetValue("InstallLocation") as string);
                    }
                }
            }
        }

        private static void Probe(string directory, int depth, HashSet<string> visited, List<string> results)
        {
            if (visited.Count >= 600 || !visited.Add(directory)) return;
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
                if (File.Exists(Path.Combine(directory, "Endfield.exe")))
                {
                    string path = Verify(directory);
                    if (!results.Exists(x => String.Equals(x, path, StringComparison.OrdinalIgnoreCase))) results.Add(path);
                    return;
                }
                if (depth <= 0) return;
                foreach (string name in SearchNames)
                {
                    string child = Path.Combine(directory, name);
                    if (Directory.Exists(child)) Probe(child, depth - 1, visited, results);
                }
                foreach (string name in GameNames)
                {
                    string child = Path.Combine(directory, name);
                    if (Directory.Exists(child)) Probe(child, depth - 1, visited, results);
                }
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    string name = Path.GetFileName(child);
                    if (name.IndexOf("endfield", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("hypergryph", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("gryphline", StringComparison.OrdinalIgnoreCase) >= 0 || name.Contains("终末地"))
                        Probe(child, depth - 1, visited, results);
                }
            }
            catch (Exception error) { if (!IsAccessError(error)) throw; }
        }

        internal static string Verify(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory)) throw new IOException("缺少游戏目录。");
            string path = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(path)).DriveType != DriveType.Fixed)
                throw new IOException("仅支持本地固定磁盘上的游戏目录。");
            string executable = Path.Combine(path, "Endfield.exe");
            foreach (string name in new[] { "Endfield.exe", "GameAssembly.dll", "UnityPlayer.dll", @"Endfield_Data\globalgamemanagers" })
            {
                string file = Path.Combine(path, name);
                FileSafety.RejectReparsePoints(file);
                if (!File.Exists(file)) throw new IOException("目录缺少终末地文件 " + name + "。");
            }
            if (!NativeSignature.IsTrustedPublisher(executable, new[] { "Hypergryph", "Gryphline", "Gryph Frontier" }))
                throw new IOException("Endfield.exe 的发行者签名未通过验证。");
            return path;
        }
    }

    internal static class NativeSignature
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfo
        {
            internal uint Size;
            internal string Path;
            internal IntPtr FileHandle;
            internal IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData
        {
            internal uint Size;
            internal IntPtr PolicyCallbackData, SipClientData;
            internal uint UIChoice, RevocationChecks, UnionChoice;
            internal IntPtr File;
            internal uint StateAction;
            internal IntPtr StateData, UrlReference;
            internal uint Flags, UIContext;
            internal IntPtr SignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref TrustData data);

        internal static bool IsTrustedPublisher(string path, string[] publisherNames)
        {
            var file = new FileInfo { Size = (uint)Marshal.SizeOf(typeof(FileInfo)), Path = path };
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
            try
            {
                Marshal.StructureToPtr(file, buffer, false);
                var data = new TrustData
                {
                    Size = (uint)Marshal.SizeOf(typeof(TrustData)), UIChoice = 2, UnionChoice = 1,
                    File = buffer, Flags = 0x1000
                };
                var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
                if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) return false;
                using (var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                {
                    string commonName = cert.GetNameInfo(X509NameType.SimpleName, false);
                    foreach (string name in publisherNames)
                        if (commonName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                return false;
            }
            catch (System.Security.Cryptography.CryptographicException) { return false; }
            finally
            {
                Marshal.DestroyStructure(buffer, typeof(FileInfo));
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
