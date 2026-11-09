using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameRenderLab.Endfield
{
    internal static class Deployment
    {
        internal const string RuntimeHash = "a4bc27f82fb83433fd5bf7243e2b598449b98e79d8f8a5acb3516f012d322985";
        internal const string SetupHash = "afe4c8f13048306307983b8b3d41d5bf00a86820440b0e57dea10950e1176445";
        private const string RuntimeUrl = "https://github.com/Yukikaze20170315/endfield-dlss5-vulkan-plugin/releases/download/unified-20261103l/unified-20261103l-candidate.zip";
        private const string SetupUrl = "https://reshade.me/downloads/ReShade_Setup_6.8.0_Addon.exe";
        private const string RuntimePath = "dlss5/nr/rtx50/nvngx_dlssnr.dll";
        private const string NrHash = "e16bcf15e16e13f527491cdf7845b2fe6521a738d8f7c9c721866a8496e1fc8e";
        private const string BridgeHash = "778265b3e6ab58119888cddc9c788dabe26e5cc7a2ef0df98538667874acf014";
        internal static readonly string[] PayloadFiles = { "renodx-dlss5.addon64", "renodx-mfgunlock.addon64", "nvngx_dlssnr.dll" };
        internal static readonly string[] ManagedFiles = new[] { "ReShade.ini", "dlss5-bridge.addon64" }.Concat(PayloadFiles).ToArray();

        internal static void Run(string requestedDirectory, bool rollback, IProgress<string> progress)
        {
            if (!Program.IsAdministrator()) throw new IOException("请在 Windows 管理员确认后运行。");
            string game = EndfieldLocation.Verify(requestedDirectory);
            RequireGameClosed();
            string machineRoot = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string store = Path.Combine(machineRoot, "GameRenderLabData", FileSafety.HashText(game.ToUpperInvariant()).Substring(0, 24));
            FileSafety.RejectReparsePoints(store);
            Directory.CreateDirectory(store);
            string lockPath = Path.Combine(store, "operation.lock");
            FileSafety.RejectReparsePoints(lockPath);
            using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                if (rollback)
                {
                    InstallJournal journal = InstallJournal.Open(store, game);
                    progress.Report("正在恢复备份…");
                    journal.Restore();
                    return;
                }
                if (InstallJournal.Exists(store)) throw new IOException("这个目录已有本安装器的安装记录。更新或重新安装前，请先使用「回滚安装」。");
                Install(store, game, progress);
            }
        }

        private static void Install(string store, string game, IProgress<string> progress)
        {
            progress.Report("正在检查 RTX 50、驱动和现有 Vulkan 安装…");
            CheckGpu();
            CheckLegacyProxies(game);
            string executable = Path.Combine(game, "Endfield.exe");
            bool allowlisted = ReShadeLayer.IsAllowed(executable);
            bool layerReady = ReShadeLayer.CheckCompatible();
            string cache = Path.Combine(store, "downloads");
            FileSafety.RejectReparsePoints(cache);
            Directory.CreateDirectory(cache);

            progress.Report("正在下载并校验上游组件，首次约 280 MB…");
            string archive = PrepareDownload(cache, "runtime.zip", "offline-runtime.zip", RuntimeUrl, RuntimeHash);
            string setup = layerReady ? null : PrepareDownload(cache, "ReShade_Setup_6.8.0_Addon.exe", "offline-reshade.exe", SetupUrl, SetupHash);

            string stage = Path.Combine(store, "stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                StageRelease(archive, stage);
                StageCandidate(stage, "renodx-dlss5.addon64", BuildPayload.GenericHash);
                StageCandidate(stage, "renodx-mfgunlock.addon64", BuildPayload.MfgHash);
                progress.Report("组件校验通过。正在备份游戏目录中即将更新的文件…");
                RequireGameClosed();
                InstallJournal journal = InstallJournal.Begin(store, game, !allowlisted);
                try
                {
                    if (!layerReady)
                    {
                        progress.Report("正在运行 ReShade 官方 Vulkan 安装程序…");
                        ReShadeLayer.Install(setup, executable);
                    }
                    else if (!allowlisted) ReShadeLayer.AddAllowed(executable);
                    RequireGameClosed();
                    progress.Report("正在安装已校验的汉化面板和运行组件…");
                    foreach (string relative in PayloadFiles)
                        FileSafety.AtomicCopy(FileSafety.Under(stage, relative), FileSafety.Under(game, relative));

                    string oldBridge = FileSafety.Under(game, "dlss5-bridge.addon64");
                    if (File.Exists(oldBridge))
                    {
                        if (!FileSafety.HashMatches(oldBridge, BridgeHash)) throw new IOException("发现未知版本的 Bridge。请先用其原安装器卸载。");
                        File.Delete(oldBridge);
                    }

                    string targetIni = FileSafety.Under(game, "ReShade.ini");
                    string sourceIni = File.Exists(targetIni) ? File.ReadAllText(targetIni) : "";
                    FileSafety.AtomicWrite(targetIni, Encoding.UTF8.GetBytes(ConfigureIni(sourceIni)));
                    if (!journal.HadFile("ReShade.ini")) ReShadeLayer.MakeUserWritable(targetIni);
                    journal.Complete();
                }
                catch (Exception error)
                {
                    try { journal.Restore(); }
                    catch (Exception rollbackError)
                    {
                        throw new IOException(error.Message + "\n自动回滚未完成。备份保留在 " + journal.Location + "。\n" + rollbackError.Message, error);
                    }
                    throw new IOException(error.Message + "\n已恢复原有游戏文件。", error);
                }
            }
            finally
            {
                // Only these private, known staging files are ever written.
                foreach (string relative in PayloadFiles) FileSafety.TryDeleteFile(FileSafety.Under(stage, relative));
                FileSafety.TryDeleteEmptyDirectory(stage);
            }
        }

        internal static void RequireGameClosed()
        {
            var games = Process.GetProcessesByName("Endfield");
            foreach (Process game in games) game.Dispose();
            if (games.Length != 0) throw new IOException("请先退出《终末地》再安装或回滚。启动器可以保持打开。");
        }

        private static void CheckGpu()
        {
            using (var query = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController"))
            using (var adapters = query.Get())
            {
                bool hasRtx50 = false;
                foreach (ManagementBaseObject adapter in adapters)
                {
                    using (adapter)
                    {
                        string model = Convert.ToString(adapter["Name"]);
                        if (!Regex.IsMatch(model, @"\bGeForce\s+RTX\s+50[0-9]{2}\b", RegexOptions.IgnoreCase)) continue;
                        hasRtx50 = true;
                        string version = Convert.ToString(adapter["DriverVersion"]);
                        if (DriverAtLeast615(version)) return;
                    }
                }
                throw new IOException(hasRtx50 ? "请先安装 NVIDIA 615.00 或更新的显卡驱动。" : "此版仅支持 GeForce RTX 50 系列和 NVIDIA 签名的神经渲染运行库。");
            }
        }

        internal static bool DriverAtLeast615(string version)
        {
            Version parsed;
            return Version.TryParse(version, out parsed) && parsed.Build >= 0 && parsed.Revision >= 0 &&
                (parsed.Build % 10) * 10000 + parsed.Revision >= 61500;
        }

        private static void CheckLegacyProxies(string game)
        {
            foreach (string proxy in new[] { "dxgi.dll", "d3d11.dll", "d3d12.dll", "vulkan-1.dll", "ReShade64.dll", "dlss5-feed.addon64", "dlss5-feed-helper.addon64", "dlss5-vk-fg-relay.addon64" })
                if (File.Exists(FileSafety.Under(game, proxy)))
                    throw new IOException("发现会与 Vulkan 安装冲突的组件 " + proxy + "。请先用其原安装器卸载。");
            string rootRuntime = FileSafety.Under(game, "nvngx_dlssnr.dll");
            if (File.Exists(rootRuntime) && !FileSafety.HashMatches(rootRuntime, NrHash))
                throw new IOException("发现游戏根目录中的旧神经渲染运行库。请先用其原安装器恢复。");
            string bridge = FileSafety.Under(game, "dlss5-bridge.addon64");
            if (File.Exists(bridge) && !FileSafety.HashMatches(bridge, BridgeHash))
                throw new IOException("发现未知版本的 Bridge。请先用其原安装器卸载。");
        }

        private static string PrepareDownload(string cache, string filename, string resource, string url, string hash)
        {
            string path = FileSafety.Under(cache, filename);
            if (File.Exists(path) && FileSafety.HashMatches(path, hash)) return path;
            string temporary = FileSafety.Under(cache, Guid.NewGuid().ToString("N") + ".download");
            try
            {
                if (!CopyEmbedded(resource, temporary))
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    using (var client = new WebClient())
                    {
                        client.Headers[HttpRequestHeader.UserAgent] = "GameRenderLab-EndfieldInstaller";
                        client.DownloadFile(new Uri(url), temporary);
                    }
                }
                if (!FileSafety.HashMatches(temporary, hash)) throw new IOException("下载组件 SHA-256 校验失败：" + filename);
                FileSafety.MoveIntoPlace(temporary, path);
                return path;
            }
            finally { FileSafety.TryDeleteFile(temporary); }
        }

        internal static bool CopyEmbedded(string resource, string destination)
        {
            using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (source == null) return false;
                using (Stream target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(target);
                return true;
            }
        }

        internal static void StageCandidate(string stage, string name, string hash)
        {
            string target = FileSafety.Under(stage, name);
            if (!CopyEmbedded("candidate/" + name, target) || !FileSafety.HashMatches(target, hash))
                throw new IOException("安装器缺少通过校验的汉化组件：" + name);
            FileSafety.RequireX64Dll(target);
        }

        internal static void StageRelease(string archive, string stage)
        {
            using (var zip = ZipFile.OpenRead(archive))
            {
                Extract(zip, RuntimePath, FileSafety.Under(stage, "nvngx_dlssnr.dll"), NrHash);
            }
            if (!NativeSignature.IsTrustedPublisher(FileSafety.Under(stage, "nvngx_dlssnr.dll"), new[] { "NVIDIA Corporation" }))
                throw new IOException("神经渲染运行库的 NVIDIA 签名未通过验证。");
        }

        private static void Extract(ZipArchive archive, string path, string destination, string hash)
        {
            var matches = archive.Entries.Where(entry => entry.FullName.Replace('\\', '/').EndsWith("/" + path, StringComparison.Ordinal) ||
                entry.FullName.Replace('\\', '/') == path).ToList();
            if (matches.Count != 1) throw new IOException("上游压缩包内容不符合预期：" + path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            matches[0].ExtractToFile(destination);
            if (!FileSafety.HashMatches(destination, hash)) throw new IOException("上游组件校验失败：" + path);
        }

        internal static string ConfigureIni(string input)
        {
            var ini = new IniText(input);
            string locale = ini.Value("OVERLAY", "Language");
            if (String.IsNullOrWhiteSpace(locale)) ini.Set("OVERLAY", "Language", "zh-CN");
            ini.SetIfMissing("OVERLAY", "TutorialProgress", "4");
            ini.SetIfMissing("INPUT", "KeyOverlay", "36,0,0,0");
            foreach (var entry in new Dictionary<string, string>
            {
                { "UiLanguage", "zh-Hans" }, { "EnableHooks", "2" }, { "NeuralUplift", "1" },
                { "NRRenderPasses", "0" }, { "NRUpscaledPasses", "1" }, { "NRPresentPasses", "0" },
                { "NRResolutionScale", "0.5" }, { "NRFollowInputRes", "0" }
            }) ini.SetIfMissing("RenoDX.DLSS5", entry.Key, entry.Value);
            ini.SetIfMissing("RenoDX.MFGUnlock", "Enabled", "0");
            return ini.ToString();
        }
    }

    internal static class FileSafety
    {
        internal static string Under(string root, string relative)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            string path = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', '\\')));
            if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("安装路径超出目标目录。");
            RejectReparsePoints(path);
            return path;
        }

        internal static void RejectReparsePoints(string path)
        {
            for (string current = Path.GetFullPath(path); !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("安装路径包含重解析点：" + current);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }

        internal static string HashFile(string file)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(file)) return Hex(sha.ComputeHash(stream));
        }

        internal static string HashText(string input)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(input)));
        }

        private static string Hex(byte[] digest)
        {
            return BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
        }

        internal static bool HashMatches(string path, string expected)
        {
            return File.Exists(path) && String.Equals(HashFile(path), expected, StringComparison.OrdinalIgnoreCase);
        }

        internal static void MoveIntoPlace(string temporary, string path)
        {
            RejectReparsePoints(path);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        internal static void AtomicCopy(string source, string target)
        {
            string parent = Path.GetDirectoryName(target);
            RejectReparsePoints(parent);
            Directory.CreateDirectory(parent);
            string temporary = Path.Combine(parent, ".grl-" + Guid.NewGuid().ToString("N") + ".tmp");
            try { File.Copy(source, temporary, false); MoveIntoPlace(temporary, target); }
            finally { TryDeleteFile(temporary); }
        }

        internal static void AtomicWrite(string target, byte[] content)
        {
            string parent = Path.GetDirectoryName(target);
            RejectReparsePoints(parent);
            Directory.CreateDirectory(parent);
            string temporary = Path.Combine(parent, ".grl-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(content, 0, content.Length);
                    stream.Flush(true);
                }
                MoveIntoPlace(temporary, target);
            }
            finally { TryDeleteFile(temporary); }
        }

        internal static void RequireX64Dll(string path)
        {
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                if (reader.BaseStream.Length < 256 || reader.ReadUInt16() != 0x5a4d) throw new IOException("插件不是有效的 Windows 文件。");
                reader.BaseStream.Position = 0x3c;
                int pe = reader.ReadInt32();
                if (pe < 64 || pe > reader.BaseStream.Length - 24) throw new IOException("插件的 PE 头无效。");
                reader.BaseStream.Position = pe;
                if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664) throw new IOException("插件必须为 Windows x64。");
                reader.BaseStream.Position = pe + 22;
                if ((reader.ReadUInt16() & 0x2000) == 0) throw new IOException("插件不是动态链接库。");
            }
        }

        internal static void TryDeleteFile(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static void TryDeleteEmptyDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
