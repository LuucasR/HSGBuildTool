using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using FMFCBuildTool.Core;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;

namespace FMFCBuildTool.ViewModels;

/// <summary>
/// The map list shared by the Package, Navigation and Lighting pages.
/// </summary>
/// <remarks>
/// Searching now filters an <see cref="ICollectionView"/> over the full collection
/// instead of swapping the ListView's ItemsSource for a filtered copy. That was the
/// cause of the worst bug in the old tool: the build read its map list back from
/// <c>MapsListView.Items</c>, so typing in the search box and pressing BUILD silently
/// dropped every selected map that didn't match the filter.
/// <see cref="SelectedMaps"/> always reads <see cref="_maps"/>, never the view.
/// </remarks>
public sealed class MapSelectionViewModel : ObservableObject
{
    private readonly ObservableCollection<MapItem> _maps = new();
    private readonly ICollectionView _view;

    private string _search = "";
    private bool _isScanning;
    private string _projectFile = "";

    public MapSelectionViewModel()
    {
        _view = CollectionViewSource.GetDefaultView(_maps);
        _view.Filter = MatchesSearch;

        SelectAllCommand = new RelayCommand(() => SetAll(true));
        SelectNoneCommand = new RelayCommand(() => SetAll(false));
        InvertCommand = new RelayCommand(Invert);
    }

    public ICollectionView Maps => _view;

    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ICommand InvertCommand { get; }

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
                _view.Refresh();
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set => SetProperty(ref _isScanning, value);
    }

    public int TotalCount => _maps.Count;

    public int SelectedCount => _maps.Count(m => m.Selected);

    /// <summary>Always visible in the UI, so a filtered view can't hide what will actually be built.</summary>
    public string SelectionSummary => TotalCount == 0
        ? "No maps found"
        : $"{SelectedCount} of {TotalCount} selected";

    /// <summary>Package paths of every selected map, regardless of the active search filter.</summary>
    public IReadOnlyList<string> SelectedMaps =>
        _maps.Where(m => m.Selected).Select(m => m.RelativePath).ToList();

    public async Task LoadAsync(string projectFile, IEnumerable<string>? selection = null)
    {
        if (_projectFile == projectFile && _maps.Count > 0)
        {
            if (selection is not null)
                ApplySelection(selection);

            return;
        }

        _projectFile = projectFile;

        IsScanning = true;

        try
        {
            var scanned = await MapScanner.ScanAsync(projectFile);

            foreach (var map in _maps)
                map.PropertyChanged -= OnMapChanged;

            _maps.Clear();

            foreach (var map in scanned)
            {
                map.PropertyChanged += OnMapChanged;
                _maps.Add(map);
            }

            if (selection is not null)
                ApplySelection(selection);

            _view.Refresh();

            OnPropertyChanged(nameof(TotalCount));
            RaiseSelectionChanged();
        }
        finally
        {
            IsScanning = false;
        }
    }

    public void ApplySelection(IEnumerable<string> paths)
    {
        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);

        foreach (var map in _maps)
            map.Selected = wanted.Contains(map.RelativePath);

        RaiseSelectionChanged();
    }

    private void SetAll(bool selected)
    {
        // Acts on what the user can currently see: with a search active, "Select all"
        // selecting hidden maps too would be surprising.
        foreach (var map in _view.Cast<MapItem>().ToList())
            map.Selected = selected;

        RaiseSelectionChanged();
    }

    private void Invert()
    {
        foreach (var map in _view.Cast<MapItem>().ToList())
            map.Selected = !map.Selected;

        RaiseSelectionChanged();
    }

    private bool MatchesSearch(object item)
    {
        if (string.IsNullOrWhiteSpace(_search))
            return true;

        if (item is not MapItem map)
            return false;

        return map.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
               map.RelativePath.Contains(_search, StringComparison.OrdinalIgnoreCase);
    }

    private void OnMapChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MapItem.Selected))
            RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(SelectedMaps));

        SelectionChanged?.Invoke();
    }

    /// <summary>Lets the owning page refresh its command preview and validation.</summary>
    public event Action? SelectionChanged;
}
