// Global alias directives for this project.
//
// Coppice.Cli sees BOTH Coppice.Core and Coppice.Ports, and the two declare types with the same
// name for different jobs: Core owns the working domain (a per-root ScanContext, the resolution
// outcomes, the report shapes) while Ports declares the portable shapes that cross the plugin
// boundary (a per-scan ScanContext carrying the whole Roots/ProjectSet context).
//
// A bare `ScanContext` in this project therefore means the Core one — that is what every call
// site written before the plugin contract existed means, and what the CLI's own pipeline uses.
// Code that genuinely needs the portable shape writes `Ports.ScanContext` in full. The same rule
// holds for every alias below.
//
// A switch statement per enum (RootRoleTo, RiskTo) is the deliberate cost of NFR-08: the plugin
// boundary stays free of Core types, and the translation lives in exactly one visible place.

global using Policy = Coppice.Core.Domain.Policy;
global using Problem = Coppice.Core.Domain.Problem;
global using Project = Coppice.Core.Projects.Project;
global using ProjectSet = Coppice.Core.Projects.ProjectSet;
global using Risk = Coppice.Core.Domain.Risk;
global using RootRole = Coppice.Core.Domain.RootRole;
global using RootValidity = Coppice.Core.Domain.RootValidity;
global using ScanContext = Coppice.Core.Scanning.ScanContext;
global using ScanRoot = Coppice.Core.Scanning.ScanRoot;
global using Severity = Coppice.Core.Domain.Severity;
global using Usage = Coppice.Core.Domain.Usage;
