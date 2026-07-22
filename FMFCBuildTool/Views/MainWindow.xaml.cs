using System;
using System.Windows;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;

namespace FMFCBuildTool.Views;

public partial class MainWindow : Window
{
    private readonly ProcessRunner Runner = new();
    private readonly BuildContext Context = new();
    private readonly OutputService Output = new();

    public MainWindow()
    {
        InitializeComponent();
        
        Runner.OutputReceived += text =>
        {
            Output.Write(text);
        };

        Runner.ProcessExited += code =>
        {
            Output.Write(
                Environment.NewLine +
                $"========== PROCESS EXITED ({code}) ==========" +
                Environment.NewLine);
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

        Runner.ProcessExited += code =>
        {
            Output.Write("");
            Output.Write($"========== PROCESS EXITED ({code}) ==========");
            Output.Write("");
        };
        Output.MessageReceived += OnOutputMessageReceived;

        Loaded += (_, _) =>
        {
            OutputTextBox.Text = Output.GetContent();
        };

        Unloaded += (_, _) =>
        {
            Output.MessageReceived -= OnOutputMessageReceived;
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


    private void Output_Click(
        object sender,
        RoutedEventArgs e)
    {
        SmallOutputPanel.Visibility = Visibility.Collapsed;
        
        MainContent.Content =
            new OutputView(Output);
        
    }
    

    private void ShowPackage()
    {
        SmallOutputPanel.Visibility = Visibility.Visible;
        
        MainContent.Content =
            new PackageView(
                Context,
                Runner, Output);
    }



    private void ShowNavigation()
    {
        SmallOutputPanel.Visibility = Visibility.Visible;
        
        MainContent.Content =
            new NavigationView(
                Context,
                Runner, Output);
    }
    private void OnOutputMessageReceived(string message)
    {
        Dispatcher.Invoke(() =>
        {
            OutputTextBox.AppendText(message + Environment.NewLine);
            OutputTextBox.ScrollToEnd();
        });
    }
}