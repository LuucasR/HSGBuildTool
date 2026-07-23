using System.IO;
using System.Text;
using FMFCBuildTool.Models;

namespace FMFCBuildTool.Services;

public static class RunUATBuilder
{
    public static string Build(BuildConfiguration config)
    {
        var args = new StringBuilder();
        
        
        args.Append("BuildCookRun ");

        args.Append($"-project=\"{config.ProjectFile}\" ");

        args.Append("-noP4 ");
        args.Append("-platform=Win64 ");
        args.Append($"-clientconfig={config.Configuration} ");
        args.Append($"-serverconfig={config.Configuration} ");

        args.Append("-installed ");
        args.Append("-utf8output ");

        var engineDir = Directory.GetParent(
            Directory.GetParent(
                Directory.GetParent(config.RunUAT)!.FullName)!.FullName)!.FullName;

        if (!string.IsNullOrWhiteSpace(config.UnrealEditorCmd))
        {
            args.Append($"-unrealexe=\"{config.UnrealEditorCmd}\" ");
        }

        if (config.NoCompile)
            args.Append("-nocompile ");

        if (config.SkipCookingEditorContent)
            args.Append("-SkipCookingEditorContent ");
        
        if (config.NoCompileEditor)
            args.Append("-nocompileeditor ");

        if (config.UnversionedCookedContent)
            args.Append("-unversionedcookedcontent ");

        if (config.CookIncremental)
            args.Append("-cookincremental ");

        if (config.ZenStore)
            args.Append("-zenstore ");

        if (config.Build)
            args.Append("-build ");

        if (config.Cook)
            args.Append("-cook ");

        if (config.FullCook)
            args.Append("-clean ");

        if (config.Stage)
            args.Append("-stage ");

        if (config.Package)
            args.Append("-package ");

        if (config.Pak)
            args.Append("-pak ");

        if (config.Compressed)
            args.Append("-compressed ");

        if (!config.UseProjectDefaultMaps)
        {
            foreach (var map in config.Maps)
            {
                args.Append($"-map={map} ");
            }
        }
        
        if (config.Archive)
        {
            if (string.IsNullOrWhiteSpace(config.ArchiveDirectory))
                throw new InvalidOperationException("Archive directory is empty.");

            args.Append("-archive ");
            args.Append($"-archivedirectory=\"{config.ArchiveDirectory}\" ");
        }
        
        return args.ToString().Trim();
    }
}