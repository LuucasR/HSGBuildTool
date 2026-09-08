using System.Collections.Generic;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;

namespace FMFCBuildTool.ViewModels;

/// <summary>
/// Rebuilds World Partition HLODs for the selected maps, through Simplygon or through
/// Unreal's own builder.
/// </summary>
/// <remarks>
/// Two invocations per map — delete, then setup and build — because that is what the
/// commandlet requires; passing -DeleteHLODs alongside -BuildHLODs deletes and then has
/// nothing left to build. Everything else on the page is the shared commandlet plumbing:
/// the same map presets, per-map results, Stop, elapsed clock, history and log export as
/// Navigation and Lighting.
/// </remarks>
public sealed class HlodViewModel : CommandletPageViewModel
{
    private string _builder = CommandletPreset.DefaultHlodBuilder;
    private bool _deleteHlods = true;
    private bool _setupHlods = true;
    private bool _buildHlods = true;
    private bool _allowCommandletRendering = true;
    private bool _waitMutex = true;
    private bool _unattended = true;
    private string _extraArguments = "";

    public HlodViewModel(
        BuildContext context,
        ProcessRunner runner,
        OutputService output,
        AppConfig config,
        BuildHistoryService history)
        : base(context, runner, output, config, history)
    {
    }

    public override string Kind => "hlod";

    public override string RunButtonText => "BUILD HLODS";

    public IReadOnlyList<HlodBuilderOption> Builders => HlodBuilder.Builders;

    /// <summary>The -Builder= value. Bound through SelectedValue, so the dropdown can show
    /// "Simplygon" while the preset stores the commandlet's own name.</summary>
    public string Builder
    {
        get => _builder;
        set => SetOption(ref _builder, value, nameof(Builder));
    }

    /// <summary>Runs -DeleteHLODs as a first, separate invocation.</summary>
    public bool DeleteHlods
    {
        get => _deleteHlods;
        set => SetOption(ref _deleteHlods, value, nameof(DeleteHlods));
    }

    public bool SetupHlods
    {
        get => _setupHlods;
        set => SetOption(ref _setupHlods, value, nameof(SetupHlods));
    }

    public bool BuildHlods
    {
        get => _buildHlods;
        set => SetOption(ref _buildHlods, value, nameof(BuildHlods));
    }

    public bool AllowCommandletRendering
    {
        get => _allowCommandletRendering;
        set => SetOption(ref _allowCommandletRendering, value, nameof(AllowCommandletRendering));
    }

    public bool WaitMutex
    {
        get => _waitMutex;
        set => SetOption(ref _waitMutex, value, nameof(WaitMutex));
    }

    public bool Unattended
    {
        get => _unattended;
        set => SetOption(ref _unattended, value, nameof(Unattended));
    }

    /// <summary>Appended to every invocation verbatim — the escape hatch for the flags the
    /// page does not have a checkbox for, e.g. -DistributedBuild.</summary>
    public string ExtraArguments
    {
        get => _extraArguments;
        set => SetOption(ref _extraArguments, value ?? "", nameof(ExtraArguments));
    }

    protected override string ActionName => "HLOD build";

    protected override string HistoryDetail => HlodBuilder.DisplayName(Builder);

    private HlodOptions Options => new(
        Builder,
        DeleteHlods,
        SetupHlods,
        BuildHlods,
        AllowCommandletRendering,
        WaitMutex,
        Unattended,
        ExtraArguments);

    protected override IReadOnlyList<CommandletPass> PassesFor(string map)
        => HlodBuilder.Passes(Context.ProjectFile, map, Options);

    /// <summary>The build pass on its own. Only reachable through the base class's
    /// single-pass default, which <see cref="PassesFor"/> replaces.</summary>
    protected override IReadOnlyList<string> ArgumentsFor(string map)
        => HlodBuilder.BuildArguments(Context.ProjectFile, map, Options);

    protected override IReadOnlyList<string> ValidateInputs(IReadOnlyList<string> maps)
        => HlodBuilder.Validate(Context.ProjectFile, maps, Options);

    protected override void OnPresetApplied(CommandletPreset preset)
    {
        SetProperty(ref _builder, preset.HlodBuilder, nameof(Builder));
        SetProperty(ref _deleteHlods, preset.DeleteHlods, nameof(DeleteHlods));
        SetProperty(ref _setupHlods, preset.SetupHlods, nameof(SetupHlods));
        SetProperty(ref _buildHlods, preset.BuildHlods, nameof(BuildHlods));
        SetProperty(ref _allowCommandletRendering, preset.AllowCommandletRendering, nameof(AllowCommandletRendering));
        SetProperty(ref _waitMutex, preset.WaitMutex, nameof(WaitMutex));
        SetProperty(ref _unattended, preset.Unattended, nameof(Unattended));
        SetProperty(ref _extraArguments, preset.ExtraArguments, nameof(ExtraArguments));
    }

    protected override void CaptureIntoPreset(CommandletPreset preset)
    {
        preset.HlodBuilder = Builder;
        preset.DeleteHlods = DeleteHlods;
        preset.SetupHlods = SetupHlods;
        preset.BuildHlods = BuildHlods;
        preset.AllowCommandletRendering = AllowCommandletRendering;
        preset.WaitMutex = WaitMutex;
        preset.Unattended = Unattended;
        preset.ExtraArguments = ExtraArguments;
    }

    /// <summary>
    /// Writes straight through to the preset, the way Lighting's Quality does, so
    /// switching preset and back does not quietly keep the other preset's flags.
    /// </summary>
    private void SetOption<T>(ref T field, T value, string propertyName)
    {
        if (!SetProperty(ref field, value, propertyName))
            return;

        CaptureIntoPreset(ActivePreset);

        Refresh();
    }
}
