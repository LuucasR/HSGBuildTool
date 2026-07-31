using System;
using FMFCBuildTool.Core;

namespace FMFCBuildTool.ViewModels;

/// <summary>
/// One selectable cook culture. BuildPreset.CookCultures has always been a list, but
/// the old UI exposed it through a single-select ComboBox, so only one culture could
/// ever be cooked.
/// </summary>
public sealed class CultureOption : ObservableObject
{
    private bool _selected;

    public CultureOption(string code, string label)
    {
        Code = code;
        Label = label;
    }

    public string Code { get; }

    public string Label { get; }

    public string Display => $"{Code} — {Label}";

    public bool Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
                Changed?.Invoke();
        }
    }

    public event Action? Changed;
}
