using Coppice.Core.Domain;
using Coppice.Ports;

namespace Coppice.Application;

/// <summary>
/// The Unknown -> not-Safe floor, applied at the application boundary for every ecosystem.
/// <para>
/// This is the structural form of C-7 and 04's rule 4: an item we could not prove unused must never
/// carry the tier a default policy acts on. The plugins already declare a risk; this only lowers it,
/// never raises it, and it is applied once so a second ecosystem cannot forget the rule.
/// </para>
/// </summary>
public static class UsageFloor
{
    public static Risk Apply(Ports.Usage usage, Risk declaredRisk) =>
        usage == Ports.Usage.Unknown && declaredRisk == Risk.Safe ? Risk.Review : declaredRisk;
}
