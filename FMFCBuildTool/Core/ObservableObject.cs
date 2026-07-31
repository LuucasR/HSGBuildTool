using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FMFCBuildTool.Core;

/// <summary>
/// Minimal INotifyPropertyChanged base. Replaces the manual x:Name reads/writes
/// that used to live in the views' code-behind.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? property = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    /// <summary>
    /// Assigns <paramref name="value"/> to <paramref name="field"/> and raises
    /// PropertyChanged. Returns false when the value did not actually change,
    /// so callers can skip follow-up work (rebuilding the command preview, etc.).
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(property);

        return true;
    }
}
