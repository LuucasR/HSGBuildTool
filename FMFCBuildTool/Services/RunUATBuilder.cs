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

        args.Append("-platform=Win64 ");

        args.Append($"-clientconfig={config.Configuration} ");

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

        if (config.Archive)
        {
            args.Append("-archive ");
            args.Append($"-archivedirectory=\"{config.ArchiveDirectory}\" ");
        }

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

        return args.ToString().Trim();
    }
}