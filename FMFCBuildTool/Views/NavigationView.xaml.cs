using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using Ookii.Dialogs.Wpf;

namespace FMFCBuildTool.Views;

public partial class NavigationView : UserControl
{
    private readonly BuildContext Context;
    private readonly ProcessRunner Runner;
    private readonly OutputService Output;
    private readonly AppConfig Config;

    public NavigationView(
        BuildContext context,
        ProcessRunner runner,
        OutputService output)
    {
        InitializeComponent();

        Context = context;
        Runner = runner;
        Output = output;

        Config = ConfigService.Load();

        EngineTextBox.Text =
            Config.LastEnginePath;

        LoadData();
    }

    private void BrowseEngine_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new VistaFolderBrowserDialog();

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        var selected = dialog.SelectedPath;

        if (selected.EndsWith(@"Engine\Binaries\Win64"))
        {
            selected = Directory.GetParent(
                Directory.GetParent(
                    Directory.GetParent(selected)!.FullName)!.FullName)!.FullName;
        }

        EngineTextBox.Text = selected;

        Config.LastEnginePath = selected;

        ConfigService.Save(Config);
    }

    private void LoadData()
    {
        ProjectTextBox.Text = Context.ProjectFile;

        MapsListView.ItemsSource = Context.Maps;
    }

    private void SelectAllMaps_Click(object sender, RoutedEventArgs e)
    {
        foreach (var map in Context.Maps)
            map.Selected = true;

        MapsListView.Items.Refresh();
    }

    private void DeselectAllMaps_Click(object sender, RoutedEventArgs e)
    {
        foreach (var map in Context.Maps)
            map.Selected = false;

        MapsListView.Items.Refresh();
    }

    private async void BuildNav_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selectedMaps =
                Context.Maps
                    .Where(x => x.Selected)
                    .ToList();

            if (selectedMaps.Count == 0)
            {
                MessageBox.Show("Select at least one map");
                return;
            }

            var editorCmd =
                UnrealLocator.FindEditorCmd(
                    Config.LastEnginePath);

            var config = new NavigationConfiguration
            {
                UnrealEditorCmd = editorCmd,

                ProjectFile = Context.ProjectFile,

                Maps = selectedMaps
                    .Select(x => x.RelativePath)
                    .ToList()
            };

            var args =
                NavigationBuilder.Build(config);

            await Runner.RunAsync(
                config.UnrealEditorCmd,
                args,
                Context.ProjectDirectory);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString());
        }
    }

    private void DeleteNav_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Deleting Navigation Data");
    }
}