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
        
        if (config.CookCultures.Count > 0)
        {
            args.Append($"-CookCultures={string.Join("+", config.CookCultures)} ");
            
        }
        
        args.Append($"-clientconfig={config.Configuration} ");
        args.Append($"-serverconfig={config.Configuration} ");

        args.Append("-installed ");
        args.Append("-utf8output ");

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
        {
            args.Append("-ZenStore ");
            args.Append("-forcerecook=false ");
        }
        
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

        if (config.IoStore)
            args.Append("-iostore ");
        if (config.Prereqs)
            args.Append("-prereqs ");
        
        if (config.Distribution)
            args.Append("-distribution ");
        
        if (config.CrashReporter)
            args.Append("-crashreporter ");
        if (config.Server)
            args.Append("-server ");
        if (config.Client)
            args.Append("-client ");
        if (config.Compressed)
            args.Append("-compressed ");

        if (config.FileOpenLog)
            args.Append("-fileopenlog ");

        if (config.StdOut)
            args.Append("-stdout ");

        if (config.CrashForUAT)
            args.Append("-CrashForUAT ");

        if (config.Unattended)
            args.Append("-unattended ");

        if (config.NoLogTimes)
            args.Append("-NoLogTimes ");
        
        if (!config.UseProjectDefaultMaps && config.Maps.Count > 0)
        {
            args.Append($"-map={string.Join("+", config.Maps)} ");
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