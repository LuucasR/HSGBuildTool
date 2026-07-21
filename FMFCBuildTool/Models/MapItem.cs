using System.ComponentModel;

namespace FMFCBuildTool.Models;

public class MapItem : INotifyPropertyChanged
{
    private bool _selected = true;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;

            _selected = value;
            OnPropertyChanged(nameof(Selected));
        }
    }

    public string Name { get; set; } = "";

    public string RelativePath { get; set; } = "";

    public string FullPath { get; set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string property)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(property));
    }

    public override string ToString()
    {
        return RelativePath;
    }
}