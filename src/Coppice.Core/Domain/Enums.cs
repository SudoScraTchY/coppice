namespace Coppice.Core.Domain;

/// <summary>Risk tier (FR-09, 06-safety-model). Default policy admits <see cref="Safe"/> only.</summary>
public enum Risk
{
    Safe = 0,
    Review = 1,
    Manual = 2,
}

/// <summary>Reference resolution is three-state and deliberately has no "probably unreferenced" (FR-07).</summary>
public enum Usage
{
    Referenced = 0,
    Unreferenced = 1,
    Unknown = 2,
}

/// <summary>How a location root is in use (05-location-resolution).</summary>
public enum RootRole
{
    Active = 0,
    Additional = 1,
    Inactive = 2,
}

/// <summary>Which rung of the resolution chain produced a candidate (05).</summary>
public enum ResolvedVia
{
    Pin = 0,
    Tool = 1,
    Env = 2,
    Config = 3,
    Registry = 4,
    OsFile = 5,
    Default = 6,
}

/// <summary>Why a root is or is not cleanable. Every non-Ok value must be reported, never silently cleaned (FR-03).</summary>
public enum RootValidity
{
    Ok = 0,
    NotFound = 1,
    FailsFingerprint = 2,
    Denied = 3,
    Ambiguous = 4,
    NeedsElevation = 5,
}

public enum RemovalKind
{
    NativeCommand = 0,
    PathDelete = 1,
    ReportOnly = 2,
}

public enum Severity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}
