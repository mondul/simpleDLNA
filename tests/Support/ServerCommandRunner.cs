using System;
using System.IO;

namespace NMaier.SimpleDlna.Tests.Support
{
  internal sealed class CommandResult
  {
    public string Error;

    public int ExitCode;

    public string Output;

    public override string ToString()
    {
      return $"exit {ExitCode}\n--- stdout:\n{Output}\n--- stderr:\n{Error}";
    }
  }

  /// <summary>
  ///   Runs "sdlna --server ..." in-process with the console captured. Use
  ///   only from tests in <see cref="ProcessStateCollection" />.
  /// </summary>
  internal static class ServerCommandRunner
  {
    public static CommandResult Run(params string[] args)
    {
      var originalOut = Console.Out;
      var originalError = Console.Error;
      var output = new StringWriter();
      var error = new StringWriter();
      Console.SetOut(output);
      Console.SetError(error);
      try {
        var exitCode = ServerCommand.Run(args);
        return new CommandResult
        {
          ExitCode = exitCode,
          Output = output.ToString(),
          Error = error.ToString()
        };
      }
      finally {
        Console.SetOut(originalOut);
        Console.SetError(originalError);
      }
    }
  }
}
