using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace FMFCBuildTool.Core;

/// <summary>
/// ObservableCollection that can be refilled in one shot.
/// </summary>
/// <remarks>
/// Rebuilding the log view on a filter change means replacing up to a few hundred
/// thousand rows; doing that through Clear()+Add() raises one CollectionChanged event
/// per row and locks the UI. This raises a single Reset instead.
/// </remarks>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void Reset(IEnumerable<T> items)
    {
        CheckReentrancy();

        Items.Clear();

        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void AddRange(IEnumerable<T> items)
    {
        CheckReentrancy();

        var added = false;

        foreach (var item in items)
        {
            Items.Add(item);
            added = true;
        }

        if (!added)
            return;

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Drops the first <paramref name="count"/> items without one event each.</summary>
    public void RemoveFirst(int count)
    {
        if (count <= 0)
            return;

        CheckReentrancy();

        for (var i = 0; i < count && Items.Count > 0; i++)
            Items.RemoveAt(0);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
