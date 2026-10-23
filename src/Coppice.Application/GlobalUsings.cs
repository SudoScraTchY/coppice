// Application services see both Core and Ports, which declare same-named types for different jobs.
// A bare name in this project means the Core working type; code that needs a portable shape writes
// `Ports.X` in full. This mirrors the CLI's aliases so moved services keep their meanings.

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
