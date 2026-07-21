using System.Collections.Generic;
using System.IO;
using System.Linq;
using FMFCBuildTool.Models;

namespace FMFCBuildTool.Services;

public static class MapScanner
{
    public static List<MapItem> Scan(string projectFile)
    {
        var maps = new List<MapItem>();

        if (!ProjectLoader.IsValidProject(projectFile))
            return maps;

        var contentFolder = ProjectLoader.GetContentDirectory(projectFile);

        if (!Directory.Exists(contentFolder))
            return maps;

        var files = Directory.GetFiles(
            contentFolder,
            "*.umap",
            SearchOption.AllDirectories);

        maps.AddRange(files.Select(file => new MapItem
        {
            Name = Path.GetFileNameWithoutExtension(file),
            FullPath = file,
            RelativePath = "/Game/" +
                           Path.GetRelativePath(contentFolder, file)
                               .Replace('\\', '/')
                               .Replace(".umap", ""),
            Selected = true
        }));

        return maps.OrderBy(m => m.RelativePath).ToList();
    }
}