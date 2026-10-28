# NFR-03 no telemetry / no network — rc.2 (T-074 rebuild)

- Egress canary: build run `40696943889`, egress job artifact `egress-results` (artifact
  `12818999373`).
- TRX: `egress.trx`; test
  `Coppice.Tests.Network.EgressCanaryTests.Egress_is_blocked_in_this_environment`,
  `outcome="Passed"`, duration 13.7 ms, started 2026-10-28T13:30:53Z.
- The egress job runs both `Coppice.Tests` and `Coppice.ArchitectureTests` under `unshare --net`;
  `architecture.trx` is present in the same artifact.
- Source review: `grep -rn "ApplicationInsights|OpenTelemetry|TelemetryClient|System.Net|HttpClient|Socket"`
  over `src` returns no matches. The architecture tests pin the same rule.
- No network calls, no telemetry. NFR-02/NFR-03 evidence is the blocked-egress suite plus the canary,
  not a promise.
