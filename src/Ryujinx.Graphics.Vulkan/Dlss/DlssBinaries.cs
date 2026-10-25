
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Ryujinx.Graphics.Vulkan.Dlss
{
    public static class DlssBinaries
    {
        public const string NgxDlssDll = "nvngx_dlss.dll";
        public const string StreamlineInterposer = "sl.interposer.dll";

        public const string NgxDlssGDll = "nvngx_dlssg.dll";
        public const string StreamlineDlssG = "sl.dlss_g.dll";
        public const string StreamlineReflex = "sl.reflex.dll";
        public const string StreamlinePcl = "sl.pcl.dll";

        private static string PathCacheFile
        {
            get
            {
                try
                {
                    return Path.Combine(Common.Configuration.AppDataManager.BaseDirPath, "dlss_path.cfg");
                }
                catch
                {
                    return null;
                }
            }
        }

        private static string ReadCachedNgxPath()
        {
            try
            {
                string cacheFile = PathCacheFile;
                if (cacheFile != null && File.Exists(cacheFile))
                {
                    string cached = File.ReadAllText(cacheFile).Trim();
                    if (cached.Length > 0 && File.Exists(cached))
                    {
                        return cached;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static void WriteCachedNgxPath(string path)
        {
            try
            {
                string cacheFile = PathCacheFile;
                if (cacheFile != null)
                {
                    File.WriteAllText(cacheFile, path);
                }
            }
            catch
            {
            }
        }

        public static string LocateNgxDlss(string byoFolder, bool autoScan = true)
        {
            if (!string.IsNullOrEmpty(byoFolder))
            {
                string explicitPath = Path.Combine(byoFolder, NgxDlssDll);
                if (File.Exists(explicitPath))
                {
                    return explicitPath;
                }
            }

            if (!autoScan)
            {
                return null;
            }

            string cached = ReadCachedNgxPath();
            if (cached != null)
            {
                return cached;
            }

            string best = null;
            Version bestVersion = null;

            foreach (string root in GetScanRoots())
            {
                foreach (string candidate in SafeEnumerate(root, NgxDlssDll))
                {
                    Version version = GetFileVersion(candidate);

                    if (best == null || (version != null && (bestVersion == null || version > bestVersion)))
                    {
                        best = candidate;
                        bestVersion = version;
                    }
                }
            }

            if (best != null)
            {
                WriteCachedNgxPath(best);
            }

            return best;
        }

        public static string LocateStreamlineFolder(string byoFolder, string bundledFolder)
        {
            foreach (string folder in new[] { byoFolder, bundledFolder })
            {
                if (!string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, StreamlineInterposer)))
                {
                    return folder;
                }
            }

            return null;
        }

        public static string[] MissingFrameGenPlugins(string folder)
        {
            if (string.IsNullOrEmpty(folder))
            {
                return new[] { StreamlineDlssG, StreamlineReflex, StreamlinePcl };
            }

            List<string> missing = new();

            foreach (string dll in new[] { StreamlineDlssG, StreamlineReflex, StreamlinePcl })
            {
                if (!File.Exists(Path.Combine(folder, dll)))
                {
                    missing.Add(dll);
                }
            }

            return missing.ToArray();
        }

        public static string StageNgxDlssG(string dlssFolder, out string action)
        {
            if (string.IsNullOrEmpty(dlssFolder))
            {
                action = "no plugin folder";

                return null;
            }

            string target = Path.Combine(dlssFolder, NgxDlssGDll);
            string driverCopy = LocateNgxDlssG(null);

            if (File.Exists(target))
            {
                Version targetVersion = GetFileVersion(target);
                Version driverVersion = driverCopy != null ? GetFileVersion(driverCopy) : null;

                if (driverVersion != null && targetVersion != null && driverVersion > targetVersion)
                {
                    try
                    {
                        File.Copy(driverCopy, target, overwrite: true);
                        action = $"updated from DriverStore {targetVersion} -> {driverVersion} (\"{driverCopy}\")";
                    }
                    catch (Exception)
                    {
                        action = $"present ({targetVersion}); newer DriverStore copy ({driverVersion}) could not be staged (in use? copy manually)";
                    }
                }
                else
                {
                    action = $"already staged ({targetVersion?.ToString() ?? "unknown version"})";
                }

                return target;
            }

            if (driverCopy == null)
            {
                action = "not found (neither plugin folder nor NVIDIA DriverStore)";

                return null;
            }

            try
            {
                File.Copy(driverCopy, target);
                action = $"staged from DriverStore (\"{driverCopy}\", {GetFileVersion(driverCopy)?.ToString() ?? "unknown version"})";

                return target;
            }
            catch (Exception)
            {
                action = $"found at \"{driverCopy}\" but could NOT be copied into the plugin folder - copy it there manually";

                return driverCopy;
            }
        }

        public static string LocateNgxDlssG(string byoFolder)
        {
            if (!string.IsNullOrEmpty(byoFolder))
            {
                string explicitPath = Path.Combine(byoFolder, NgxDlssGDll);
                if (File.Exists(explicitPath))
                {
                    return explicitPath;
                }
            }

            string driverStore = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                @"System32\DriverStore\FileRepository");

            string[] driverDirs;
            try
            {
                driverDirs = Directory.GetDirectories(driverStore, "nv_dispi.inf_amd64_*");
            }
            catch
            {
                return null;
            }

            string best = null;
            Version bestVersion = null;

            foreach (string dir in driverDirs)
            {
                string candidate = Path.Combine(dir, NgxDlssGDll);
                if (!File.Exists(candidate))
                {
                    continue;
                }

                Version version = GetFileVersion(candidate);

                if (best == null || (version != null && (bestVersion == null || version > bestVersion)))
                {
                    best = candidate;
                    bestVersion = version;
                }
            }

            return best;
        }

        private static IEnumerable<string> GetScanRoots()
        {
            string[] relativeRoots =
            {
                @"SteamLibrary\steamapps\common",
                @"Program Files (x86)\Steam\steamapps\common",
                @"XboxGames",
                @"Program Files\Epic Games",
                @"Epic Games",
                @"Program Files\EA Games",
                @"Program Files\Ubisoft\Ubisoft Game Launcher\games",
            };

            foreach (DriveInfo drive in GetReadyDrives())
            {
                foreach (string relative in relativeRoots)
                {
                    string root = Path.Combine(drive.RootDirectory.FullName, relative);
                    if (Directory.Exists(root))
                    {
                        yield return root;
                    }
                }
            }

            string driverStore = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                @"System32\DriverStore\FileRepository");

            if (Directory.Exists(driverStore))
            {
                yield return driverStore;
            }
        }

        private static IEnumerable<DriveInfo> GetReadyDrives()
        {
            DriveInfo[] drives;

            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch
            {
                yield break;
            }

            foreach (DriveInfo drive in drives)
            {
                bool ready;

                try
                {
                    ready = drive.IsReady && drive.DriveType == DriveType.Fixed;
                }
                catch
                {
                    ready = false;
                }

                if (ready)
                {
                    yield return drive;
                }
            }
        }

        private static IEnumerable<string> SafeEnumerate(string root, string fileName)
        {
            Queue<string> pending = new();
            pending.Enqueue(root);

            while (pending.Count > 0)
            {
                string dir = pending.Dequeue();

                string[] matches = null;
                try
                {
                    matches = Directory.GetFiles(dir, fileName);
                }
                catch
                {
                }

                if (matches != null)
                {
                    foreach (string match in matches)
                    {
                        yield return match;
                    }
                }

                string[] subDirs = null;
                try
                {
                    subDirs = Directory.GetDirectories(dir);
                }
                catch
                {
                }

                if (subDirs != null)
                {
                    foreach (string subDir in subDirs)
                    {
                        pending.Enqueue(subDir);
                    }
                }
            }
        }

        private static Version GetFileVersion(string path)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);

                if (info.FileMajorPart != 0 || info.FileMinorPart != 0 || info.FileBuildPart != 0 || info.FilePrivatePart != 0)
                {
                    return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
                }
            }
            catch
            {
            }

            return null;
        }
    }
}
