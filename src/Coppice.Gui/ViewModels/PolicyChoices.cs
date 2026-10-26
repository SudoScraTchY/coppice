using Coppice.Core.Domain;
using Policy = Coppice.Core.Domain.Policy;

namespace Coppice.Gui.ViewModels;

/// <summary>
/// The retention choices the GUI presents. This is the ONLY place a label is attached to a policy;
/// the policy objects are the same ones the CLI and TUI use.
/// </summary>
public static class PolicyChoices
{
    public static IReadOnlyList<PolicyOption> All { get; } =
    [
        new PolicyOption("Default — keep newest 2", "The CLI default; keeps the newest two versions of each item.", Policy.Default),
        new PolicyOption("Keep newest only", "Keeps the newest version of each item and proposes every older eligible one.", Policy.Default with { KeepLatestN = 1 }),
        new PolicyOption("Conservative — keep newest 3", "Keeps the newest three versions of each item.", Policy.Conservative),
        new PolicyOption("Custom keep count", "Uses the number below. Zero keeps no version, so every eligible version is proposed.", Policy.Default),
    ];

    public static PolicyOption KeepNewestOnly => All[1];
}
