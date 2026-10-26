using Coppice.Ports;

namespace Coppice.Cli;

/// <summary>The CLI's existing name for the shared config path; the implementation lives in Application.</summary>
public static class UserConfigPath
{
    public static string For(IEnvironment env) => Coppice.Application.UserConfigPath.For(env);
}
