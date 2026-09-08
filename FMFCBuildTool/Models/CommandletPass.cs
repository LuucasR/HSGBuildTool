using System.Collections.Generic;

namespace FMFCBuildTool.Models;

/// <summary>
/// One UnrealEditor-Cmd invocation within a single map's turn.
/// </summary>
/// <remarks>
/// Navigation and Lighting need exactly one invocation per map. HLOD needs two, because
/// clearing the existing HLODs and building the new ones are separate commandlet runs —
/// the same pair you would type by hand. Making the run loop pass-based rather than
/// map-based is what lets a page ask for both without a second run loop, a second
/// progress scheme and a second set of per-map results.
/// </remarks>
/// <param name="Label">
/// Names the pass in the log, the status line and the .bat, e.g. "Delete HLODs". Empty
/// for the single-pass pages, where naming the step would only repeat the page.
/// </param>
public sealed record CommandletPass(string Label, IReadOnlyList<string> Arguments);
