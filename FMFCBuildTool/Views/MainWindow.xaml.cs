using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Ookii.Dialogs.Wpf;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;

namespace FMFCBuildTool.Views;

public partial class MainWindow : Window
{
    private readonly ProcessRunner Runner = new();
    private readonly AppConfig Config;
    private bool IsBuilding;
    private List<MapItem> AllMaps = new();

    public MainWindow()
    {
        InitializeComponent();

        Config = ConfigService.Load();

        ArchiveTextBox.Text = Config.LastArchiveFolder;
        ProjectTextBox.Text = Config.LastProject;

        BuildButton.Click += BuildButton_Click;
        CancelButton.Click += CancelButton_Click;
        Runner.OutputReceived += text =>
        {
            Dispatcher.Invoke(() =>
            {
                OutputTextBox.AppendText(text + Environment.NewLine);
                OutputTextBox.ScrollToEnd();
            });
        };

        Runner.ProcessExited += code =>
        {
            Dispatcher.Invoke(() =>
            {
                OutputTextBox.AppendText(Environment.NewLine);

                if (code == 0)
                    OutputTextBox.AppendText("========== BUILD SUCCESS ==========" + Environment.NewLine);
                else
                    OutputTextBox.AppendText($"========== BUILD FAILED ({code}) ==========" + Environment.NewLine);

                OutputTextBox.ScrollToEnd();
            });
        };
    }

    private async void BuildButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsBuilding)
            return;

        IsBuilding = true;

        BuildButton.IsEnabled = false;
        CancelButton.IsEnabled = true;

        try
        {
            OutputTextBox.Clear();

            var cfg = new BuildConfiguration
            {
                ProjectFile = ProjectTextBox.Text,
                ProjectDirectory = ProjectLoader.GetProjectDirectory(ProjectTextBox.Text),
                ArchiveDirectory = ArchiveTextBox.Text,

                RunUAT = UnrealLocator.FindRunUAT(ProjectTextBox.Text),

                Configuration =
                    ((ComboBoxItem)ConfigurationComboBox.SelectedItem)
                    .Content!
                    .ToString()!,

                FullCook = FullCookRadio.IsChecked == true,

                Build = BuildCheckBox.IsChecked == true,
                Cook = CookCheckBox.IsChecked == true,
                Stage = StageCheckBox.IsChecked == true,
                Package = PackageCheckBox.IsChecked == true,
                Archive = ArchiveCheckBox.IsChecked == true,
                Pak = PakCheckBox.IsChecked == true,
                Compressed = CompressedCheckBox.IsChecked == true,

                UseProjectDefaultMaps = UseProjectDefaultMapsCheckBox.IsChecked == true
            };

            if (!cfg.UseProjectDefaultMaps)
            {
                foreach (var item in MapsListView.Items)
                {
                    if (item is MapItem map && map.Selected)
                    {
                        cfg.Maps.Add(map.RelativePath);
                    }
                }
            }

            var args = RunUATBuilder.Build(cfg);

            OutputTextBox.AppendText(cfg.RunUAT + Environment.NewLine);
            OutputTextBox.AppendText(args + Environment.NewLine + Environment.NewLine);

            await Runner.RunAsync(
                cfg.RunUAT,
                args,
                cfg.ProjectDirectory);

            IsBuilding = false;

            BuildButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            Config.LastArchiveFolder = cfg.ArchiveDirectory;
            Config.LastProject = cfg.ProjectFile;

            ConfigService.Save(Config);
        }
        catch (Exception ex)
        {
            IsBuilding = false;

            BuildButton.IsEnabled = true;
            CancelButton.IsEnabled = false;

            MessageBox.Show(ex.ToString());
        }
    }

    private void BrowseProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Unreal Project (*.uproject)|*.uproject",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
            return;

        ProjectTextBox.Text = dialog.FileName;

        AllMaps = MapScanner.Scan(dialog.FileName);

        MapsListView.ItemsSource = AllMaps;

        OutputTextBox.AppendText(
            $"Loaded {AllMaps.Count} maps from {ProjectLoader.GetProjectName(dialog.FileName)}{Environment.NewLine}");
    }

    private void BrowseArchive_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new VistaFolderBrowserDialog();

        if (dialog.ShowDialog(this) == true)
            ArchiveTextBox.Text = dialog.SelectedPath;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Runner.Cancel();

        OutputTextBox.AppendText(
            Environment.NewLine +
            "========== BUILD CANCELLED ==========" +
            Environment.NewLine);
    }
    private void MapSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = MapSearchTextBox.Text.Trim().ToLower();

        if (string.IsNullOrWhiteSpace(text))
        {
            MapsListView.ItemsSource = AllMaps;
            return;
        }

        MapsListView.ItemsSource = AllMaps
            .Where(x =>
                x.Name.ToLower().Contains(text) ||
                x.RelativePath.ToLower().Contains(text))
            .ToList();
    }
    private void SelectAllMaps_Click(object sender, RoutedEventArgs e)
    {
        foreach (var map in AllMaps)
        {
            map.Selected = true;
        }

        MapsListView.Items.Refresh();
    }


    private void DeselectAllMaps_Click(object sender, RoutedEventArgs e)
    {
        foreach (var map in AllMaps)
        {
            map.Selected = false;
        }

        MapsListView.Items.Refresh();
    }
}