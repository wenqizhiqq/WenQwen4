using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

/// <summary>
/// 模型定位：配置路径不存在时，自动在各磁盘的浅层文件夹中搜索同名模型，
/// 仍找不到时退化为搜索文件名含 qwen 的最大 gguf。
/// </summary>
internal static class ModelLocator
{
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "$WinREAgent", "Config.Msi",
        "Windows", "WindowsApps", "Program Files", "Program Files (x86)", "ProgramData",
        "Recovery", "PerfLogs", "AppData", "node_modules", ".git", ".vs", ".nuget",
        "packages", "Temp", "obj", "bin"
    };

    // 从盘符根目录算起的搜索深度，D:\wqz\models\xxx.gguf 位于第 2 层
    private const int MaxDepth = 3;
    private const int MaxDirectoriesPerRoot = 60000;
    private static readonly TimeSpan PerRootBudget = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(10);

    public static string Resolve(string preferredPath)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
            return preferredPath;

        string fileName = Path.GetFileName(preferredPath);
        if (string.IsNullOrWhiteSpace(fileName))
            return preferredPath;

        string? found = Search(fileName, exactName: true)
                        ?? Search(fileName, exactName: false);
        return found ?? preferredPath;
    }

    private static string? Search(string fileName, bool exactName)
    {
        var total = Stopwatch.StartNew();
        foreach (string root in GetSearchRoots())
        {
            if (total.Elapsed >= TotalBudget) break;
            string? hit = SearchRoot(root, fileName, exactName);
            if (hit != null) return hit;
        }
        return null;
    }

    private static IEnumerable<string> GetSearchRoots()
    {
        var roots = new List<string>();

        try
        {
            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (Directory.Exists(baseDir)) roots.Add(baseDir);
        }
        catch { }

        string systemDrive = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\")
            .TrimEnd(Path.DirectorySeparatorChar);

        var drives = new List<DriveInfo>();
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) continue;
                drives.Add(drive);
            }
        }
        catch { }

        // 先查非系统盘（模型通常放数据盘），最后再查系统盘
        foreach (DriveInfo drive in drives.Where(d => !SameDrive(d.Name, systemDrive)))
            roots.Add(drive.Name);
        foreach (DriveInfo drive in drives.Where(d => SameDrive(d.Name, systemDrive)))
            roots.Add(drive.Name);

        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool SameDrive(string path, string drive)
        => string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), drive, StringComparison.OrdinalIgnoreCase);

    private static string? SearchRoot(string root, string fileName, bool exactName)
    {
        var budget = Stopwatch.StartNew();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        int visited = 0;

        while (queue.Count > 0 && budget.Elapsed < PerRootBudget && visited < MaxDirectoriesPerRoot)
        {
            (string dir, int depth) = queue.Dequeue();
            visited++;

            if (exactName)
            {
                string candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            else
            {
                string? best = FindLargestQwenGguf(dir);
                if (best != null) return best;
            }

            if (depth >= MaxDepth) continue;

            try
            {
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    if (SkipFolders.Contains(Path.GetFileName(sub))) continue;
                    try
                    {
                        if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch { }
        }

        return null;
    }

    private static string? FindLargestQwenGguf(string dir)
    {
        string? best = null;
        long bestSize = -1;
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*.gguf"))
            {
                if (Path.GetFileName(file).IndexOf("qwen", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    long size = new FileInfo(file).Length;
                    if (size > bestSize) { bestSize = size; best = file; }
                }
                catch { }
            }
        }
        catch { }
        return best;
    }
}
