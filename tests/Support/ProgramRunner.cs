using System;

namespace NMaier.SimpleDlna.Tests.Support
{
  /// <summary>
  ///   Runs sdlna's Main in-process with the console captured, and returns
  ///   the exit code it set. Only for command lines that end before serving,
  ///   such as --help or an invalid option; anything else starts a server.
  ///   Use only from tests in <see cref="ProcessStateCollection" />.
  /// </summary>
  internal static class ProgramRunner
  {
    public static CommandResult Run(params string[] args)
    {
      return CommandResult.Capture(() =>
      {
        var originalExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
        try {
          Program.Main(args);
          return Environment.ExitCode;
        }
        finally {
          Environment.ExitCode = originalExitCode;
        }
      });
    }
  }
}
