using System;
using System.Windows;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;

namespace FMFCBuildTool.Views;

public partial class MainWindow : Window
{
    private readonly ProcessRunner Runner = new();
    private readonly BuildContext Context = new();


    public MainWindow()
    {
        InitializeComponent();


        Runner.OutputReceived += text =>
        {
            Dispatcher.Invoke(() =>
            {
                OutputTextBox.AppendText(
                    text + Environment.NewLine);

                OutputTextBox.ScrollToEnd();
            });
        };


        Runner.ProcessExited += code =>
        {
            Dispatcher.Invoke(() =>
            {
                OutputTextBox.AppendText(
                    Environment.NewLine +
                    $"========== PROCESS EXITED ({code}) ==========" +
                    Environment.NewLine);

                OutputTextBox.ScrollToEnd();
            });
        };


        ShowPackage();
    }


    private void Package_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowPackage();
    }



    private void Navigation_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowNavigation();
    }



    private void ShowPackage()
    {
        MainContent.Content =
            new PackageView(
                Context,
                Runner);
    }



    private void ShowNavigation()
    {
        MainContent.Content =
            new NavigationView(
                Context,
                Runner);
    }
}