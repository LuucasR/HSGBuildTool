using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FMFCBuildTool.Models;

namespace FMFCBuildTool.Services;

public static class MapScanner
{
    /// <summary>
    /// Enumerates every .umap under the project's Content folder.
    /// Async because a large project's Content tree can take a noticeable moment,
    /// and the previous synchronous call ran on the UI thread during construction.
    /// </summary>
    public static Task<List<MapItem>> ScanAsync(string projectFile)
        => Task.Run(() => Scan(projectFile));

    public static List<MapItem> Scan(string projectFile)
    {
        var maps = new List<MapItem>();

        if (!ProjectLoader.IsValidProject(projectFile))
            return maps;

        var contentFolder = ProjectLoader.GetContentDirectory(projectFile);

        if (!Directory.Exists(contentFolder))
            return maps;

        IEnumerable<string> files;

        try
        {
            files = Directory.EnumerateFiles(contentFolder, "*.umap", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            return maps;
        }

        foreach (var file in files)
        {
            maps.Add(new MapItem
            {
                Name = Path.GetFileNameWithoutExtension(file),
                FullPath = file,
                RelativePath = ToPackagePath(contentFolder, file),
                Selected = false
            });
        }

        return maps.OrderBy(m => m.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>C:\Proj\Content\Maps\L_Arena.umap → /Game/Maps/L_Arena</summary>
    private static string ToPackagePath(string contentFolder, string file)
    {
        var relative = Path.GetRelativePath(contentFolder, file).Replace('\\', '/');

        // Trim the extension explicitly: the old code used Replace(".umap", ""),
        // which also mangled any folder that happened to contain that substring.
        if (relative.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            relative = relative[..^".umap".Length];

        return "/Game/" + relative;
    }
}
