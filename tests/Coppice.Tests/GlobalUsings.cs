// Global alias directives for this project.
//
// The test project sees BOTH Coppice.Core and Coppice.Ports, and the two declare types with the
// same name for different jobs: Core owns the working domain (a per-root ScanContext, Core
// Project/ProjectSet), Ports declares the portable shapes that cross the plugin boundary (a
// per-scan ScanContext carrying Roots + ProjectSet + the ports a plugin may use).
//
// A bare name here means the Core one, so every test written before the plugin contract existed
// keeps compiling and keeps testing the same type. The conformance harness, which is written
// against the plugin boundary, writes `Ports.ScanContext` and friends in full. That asymmetry is
// intentional: a conformance test that silently picked the Core type would prove nothing.

global using Project = Coppice.Core.Projects.Project;
global using ProjectSet = Coppice.Core.Projects.ProjectSet;
global using Risk = Coppice.Core.Domain.Risk;
global using RootRole = Coppice.Core.Domain.RootRole;
global using RootValidity = Coppice.Core.Domain.RootValidity;
global using ScanContext = Coppice.Core.Scanning.ScanContext;
global using ScanRoot = Coppice.Core.Scanning.ScanRoot;
global using Severity = Coppice.Core.Domain.Severity;
global using Usage = Coppice.Core.Domain.Usage;
