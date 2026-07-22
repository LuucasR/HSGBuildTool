using System;
using System.Text;

namespace FMFCBuildTool.Services;

public class OutputService
{
    private readonly StringBuilder Buffer = new();

    public event Action<string>? MessageReceived;


    public void Write(string message)
    {
        Buffer.AppendLine(message);

        MessageReceived?.Invoke(message);
    }


    public void Clear()
    {
        Buffer.Clear();

        MessageReceived?.Invoke(string.Empty);
    }


    public string GetContent()
    {
        return Buffer.ToString();
    }
}