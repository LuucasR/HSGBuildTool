using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Ookii.Dialogs.Wpf;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using System.IO;

namespace FMFCBuildTool.Views;

public partial class PackageView : UserControl
{
    private readonly ProcessRunner Runner;
    private readonly Stopwatch BuildTimer = new();
    private readonly AppConfig Config;
    private readonly BuildContext Context;

    private CancellationTokenSource? TimerCancellation;

    private bool IsBuilding;
    private List<MapItem> AllMaps = new();
    private readonly OutputService Output;


    public PackageView(
        BuildContext context,
        ProcessRunner runner,
        OutputService output)
    {
        InitializeComponent();

        Context = context;
        Runner = runner;
        Output = output;
        
        Config = ConfigService.Load();
        

        ArchiveTextBox.Text = Config.LastArchiveFolder;
        
        if (!string.IsNullOrWhiteSpace(Config.LastProject))
        {
            Context.ProjectFile = Config.LastProject;

            Context.ProjectDirectory =
                ProjectLoader.GetProjectDirectory(
                    Config.LastProject);

            ProjectTextBox.Text =
                Config.LastProject;
        }


        BuildButton.Click += BuildButton_Click;
        CancelButton.Click += CancelButton_Click;
        
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
            var cfg = new BuildConfiguration
            {
                FileOpenLog = FileOpenLogCheckBox.IsChecked == true,
                StdOut = StdOutCheckBox.IsChecked == true,
                CrashForUAT = CrashForUATCheckBox.IsChecked == true,
                Unattended = UnattendedCheckBox.IsChecked == true,
                NoLogTimes = NoLogTimesCheckBox.IsChecked == true,
                NoCompile = NoCompileCheckBox.IsChecked == true,
                NoCompileEditor = NoCompileEditorCheckBox.IsChecked == true,
                UnversionedCookedContent = UnversionedCookedContentCheckBox.IsChecked == true,
                CookIncremental = CookIncrementalCheckBox.IsChecked == true,
                ZenStore = ZenStoreCheckBox.IsChecked == true,
                
                ProjectFile = Context.ProjectFile,

                ProjectDirectory =
                    Context.ProjectDirectory,

                ArchiveDirectory =
                    ArchiveTextBox.Text,


                RunUAT =
                    UnrealLocator.FindRunUAT(
                        Context.ProjectFile),


                Configuration =
                    ((ComboBoxItem)ConfigurationComboBox.SelectedItem)
                    .Content!
                    .ToString()!,


                FullCook =
                    FullCookRadio.IsChecked == true,


                Build =
                    BuildCheckBox.IsChecked == true,

                Cook =
                    CookCheckBox.IsChecked == true,

                Stage =
                    StageCheckBox.IsChecked == true,

                Package =
                    PackageCheckBox.IsChecked == true,

                Archive =
                    ArchiveCheckBox.IsChecked == true,

                Pak =
                    PakCheckBox.IsChecked == true,

                Compressed =
                    CompressedCheckBox.IsChecked == true,

                SkipCookingEditorContent =
                    SkipCookingEditorContentCheckBox.IsChecked == true,
                
                UseProjectDefaultMaps =
                    UseProjectDefaultMapsCheckBox.IsChecked == true,
                
                UnrealEditorCmd =
                    UnrealLocator.FindUnrealEditorCmd(
                        Context.ProjectFile),
            };


            if (!cfg.UseProjectDefaultMaps)
            {
                foreach(var item in MapsListView.Items)
                {
                    if(item is MapItem map && map.Selected)
                    {
                        cfg.Maps.Add(
                            map.RelativePath);
                    }
                }
            }



            var args =
                RunUATBuilder.Build(cfg);


            BuildTimeText.Text =
                "Building...";


            BuildTimer.Restart();


            TimerCancellation?.Cancel();

            TimerCancellation =
                new CancellationTokenSource();


            _ = UpdateBuildTimer(
                TimerCancellation.Token);



            await Runner.RunAsync(
                cfg.RunUAT,
                args,
                cfg.ProjectDirectory);



            Config.LastArchiveFolder =
                cfg.ArchiveDirectory;

            Config.LastProject =
                cfg.ProjectFile;


            ConfigService.Save(Config);
        }
        catch(Exception ex)
        {
            MessageBox.Show(
                ex.ToString());
        }
        finally
        {
            BuildTimer.Stop();

            TimerCancellation?.Cancel();

            BuildTimeText.Text =
                $"Finished ({BuildTimer.Elapsed:hh\\:mm\\:ss})";
            
            IsBuilding = false;

            BuildButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }



    private async Task UpdateBuildTimer(
        CancellationToken token)
    {
        try
        {
            while(!token.IsCancellationRequested)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    BuildTimeText.Text =
                        $"Building {BuildTimer.Elapsed:hh\\:mm\\:ss}";
                });


                await Task.Delay(
                    1000,
                    token);
            }
        }
        catch(OperationCanceledException)
        {

        }
    }




    private void BrowseProject_Click(
        object sender,
        RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Unreal Project (*.uproject)|*.uproject",
            CheckFileExists = true
        };


        if (dialog.ShowDialog() != true)
            return;
        
        ProjectTextBox.Text = dialog.FileName;
        
        Context.ProjectFile = dialog.FileName;

        Context.ProjectDirectory =
            ProjectLoader.GetProjectDirectory(
                dialog.FileName);
        
        Context.Maps =
            MapScanner.Scan(
                dialog.FileName);


        AllMaps = Context.Maps;


        MapsListView.ItemsSource =
            Context.Maps;

        
        Config.LastProject =
            dialog.FileName;

        ConfigService.Save(Config);
    }

    
    private void BrowseArchive_Click(
        object sender,
        RoutedEventArgs e)
    {
        var dialog =
            new VistaFolderBrowserDialog();


        if(dialog.ShowDialog(
            Window.GetWindow(this)) == true)
        {
            ArchiveTextBox.Text =
                dialog.SelectedPath;
        }
    }
    

    private void CancelButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        Runner.Cancel();

        BuildTimer.Stop();

        TimerCancellation?.Cancel();
        
        BuildTimeText.Text =
            $"Canceled ({BuildTimer.Elapsed:hh\\:mm\\:ss})";
    }





    private void MapSearchTextBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        var text =
            MapSearchTextBox.Text
            .Trim()
            .ToLower();


        if(string.IsNullOrWhiteSpace(text))
        {
            MapsListView.ItemsSource =
                AllMaps;

            return;
        }



        MapsListView.ItemsSource =
            AllMaps
            .Where(x =>
                x.Name.ToLower().Contains(text) ||
                x.RelativePath.ToLower().Contains(text))
            .ToList();
    }





    private void SelectAllMaps_Click(
        object sender,
        RoutedEventArgs e)
    {
        foreach(var map in AllMaps)
        {
            map.Selected = true;
        }


        MapsListView.Items.Refresh();
    }




    private void DeselectAllMaps_Click(
        object sender,
        RoutedEventArgs e)
    {
        foreach(var map in AllMaps)
        {
            map.Selected = false;
        }


        MapsListView.Items.Refresh();
    }
    
}