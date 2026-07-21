using System.Text;
using FMFCBuildTool.Models;

namespace FMFCBuildTool.Services;

public static class NavigationBuilder
{
    public static string Build(NavigationConfiguration config)
    {
        var args = new StringBuilder();

        args.Append($"\"{config.ProjectFile}\" ");

        foreach(var map in config.Maps)
        {
            args.Append($"{map} ");
        }

        args.Append(
            "-run=WorldPartitionBuilderCommandlet " +
            "-AllowCommandletRendering " +
            "-builder=WorldPartitionNavigationDataBuilder " +
            "-SCCProvider=None");


        return args.ToString();
    }
}