using System.Diagnostics;

namespace Retromind.Tests.TestInfrastructure;

internal static class WinetricksTestProgram
{
    internal static Func<ProcessStartInfo, Process?> CreateStarter(TemporaryDirectory temp, string script)
    {
        var path = temp.CreateFile($"winetricks-test-{Guid.NewGuid():N}.sh", "#!/bin/sh\n" + script + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return startInfo =>
        {
            startInfo.FileName = path;
            return Process.Start(startInfo);
        };
    }
}
