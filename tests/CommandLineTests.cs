using System;
using System.Collections.Generic;
using System.Linq;
using NMaier.GetOptNet;
using NMaier.SimpleDlna.Tests.Support;
using Xunit;

namespace NMaier.SimpleDlna.Tests
{
  /// <summary>
  ///   How "sdlna" without --server reads its command line: the names of
  ///   --help, and what an invalid command line does.
  /// </summary>
  [Collection(ProcessStateCollection.NAME)]
  public sealed class CommandLineTests
  {
    private static readonly string nl = Environment.NewLine;

    private static Options Parse(bool windows, params string[] args)
    {
      var options = new Options();
      options.ParseCommandLine(args, windows);
      return options;
    }

    /// <summary>
    ///   Usage lists "-?" as the short form of --help, but GetOptNet reads
    ///   short options only when a letter or digit follows the dash. So
    ///   "sdlna -?" took "-?" for a folder, started the HTTP server and
    ///   failed with "The directory name '.../-?' does not exist", in Debug
    ///   builds as an unhandled exception.
    /// </summary>
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    public void HelpIsPrintedForEachOfItsNames(string help)
    {
      var result = ProgramRunner.Run(help);

      Assert.True(result.ExitCode == 0, result.ToString());
      Assert.Equal("", result.Error);
      Assert.StartsWith($"{nl}Usage: sdlna ", result.Output);
      Assert.Contains($"{nl}   -?, --help ", result.Output);
    }

    [Fact]
    public void SlashQuestionMarkPrintsHelpOnWindows()
    {
      Assert.SkipUnless(OperatingSystem.IsWindows(), "Elsewhere, /? is a folder");

      var result = ProgramRunner.Run("/?");

      Assert.True(result.ExitCode == 0, result.ToString());
      Assert.StartsWith($"{nl}Usage: sdlna ", result.Output);
    }

    /// <summary>
    ///   No Windows folder can be named "?", and Windows programs read "/?"
    ///   as help. Elsewhere it is a folder like any other.
    /// </summary>
    [Fact]
    public void SlashQuestionMarkIsHelpOnlyOnWindows()
    {
      Assert.True(Parse(true, "/?").ShowHelp);

      var options = Parse(false, "/?");
      Assert.False(options.ShowHelp);
      Assert.Equal("/?", Assert.Single(options.Directories).ToString());
    }

    /// <summary>
    ///   "-?" is help wherever GetOptNet would read an option, and only
    ///   there: after "--" it is a folder, and after a short option that
    ///   takes a value, it is that value.
    /// </summary>
    [Fact]
    public void QuestionMarkIsHelpOnlyWhereAnOptionCanBe()
    {
      Assert.True(Parse(false, "Videos", "-?").ShowHelp);
      Assert.True(Parse(false, "-d", "-?").ShowHelp);
      Assert.True(Parse(false, "-l", "DEBUG", "-?").ShowHelp);
      Assert.True(Parse(false, " -? ").ShowHelp);

      var afterDashes = Parse(false, "--", "-?");
      Assert.False(afterDashes.ShowHelp);
      Assert.Equal("-?", Assert.Single(afterDashes.Directories).ToString());

      var name = Parse(false, "-n", "-?");
      Assert.False(name.ShowHelp);
      Assert.Equal("-?", name.FriendlyName);

      var grouped = Parse(false, "-dn", "-?");
      Assert.False(grouped.ShowHelp);
      Assert.True(grouped.DescendingOrder);
      Assert.Equal("-?", grouped.FriendlyName);

      // -n takes the rest of its group, "d", so "-?" is an option again.
      var nameInGroup = Parse(false, "-nd", "-?");
      Assert.True(nameInGroup.ShowHelp);
      Assert.Equal("d", nameInGroup.FriendlyName);

      // "--" is the name here, not the end of the options.
      var dashesAsName = Parse(false, "-n", "--", "-?");
      Assert.True(dashesAsName.ShowHelp);
      Assert.Equal("--", dashesAsName.FriendlyName);
    }

    /// <summary>
    ///   Which short options take the next argument is decided apart from
    ///   GetOptNet, so every short name is held to GetOptNet's own reading
    ///   of "-h" after it, including options added later.
    /// </summary>
    [Fact]
    public void QuestionMarkAfterAShortOptionIsReadAsGetOptNetReadsDashH()
    {
      var flags = new List<string>();
      var withValues = new List<string>();
      var names = Enumerable.Range('a', 26)
        .Concat(Enumerable.Range('A', 26))
        .Concat(Enumerable.Range('0', 10))
        .Select(c => "-" + (char)c);
      foreach (var name in names) {
        bool helpIsAnOption;
        try {
          var options = new Options();
          options.Parse(new[] {name, "-h"});
          helpIsAnOption = options.ShowHelp;
        }
        catch (UnknownAttributeException) {
          continue;
        }
        catch (Exception ex) when (
          ex is GetOptException || ex is ProgrammingErrorException) {
          // The option took "-h" as its value, and rejected it.
          helpIsAnOption = false;
        }

        (helpIsAnOption ? flags : withValues).Add(name);
        Assert.Equal(
          new[] {name, helpIsAnOption ? "--help" : "-?"},
          new Options().TranslateHelpArguments(new[] {name, "-?"}, false));
      }

      Assert.Contains("-d", flags);
      Assert.Contains("-h", flags);
      Assert.Contains("-n", withValues);
      Assert.Contains("-p", withValues);
    }

    /// <summary>
    ///   An unknown option, or an invalid value, printed the error and usage
    ///   and then exited with status 0, as if sdlna had run.
    /// </summary>
    [Theory]
    [InlineData("There is no argument with the name \"--bogus\"", "--bogus")]
    [InlineData("There is no argument with the name \"-z\"", "-z")]
    [InlineData("Omitted value for short argument \"n\"", "-n")]
    [InlineData("Omitted value for argument \"port\"", "--port")]
    [InlineData("Argument \"help\" does not except a value", "--help=yes")]
    [InlineData("Wrong value type for argument \"t\"", "-t", "bogus")]
    [InlineData("Port must be between", "-p", "70000")]
    [InlineData("Not a valid IP address: x", "-i", "x")]
    public void UsageErrorsExitWithStatus2(string error, params string[] args)
    {
      var result = ProgramRunner.Run(args);

      Assert.True(result.ExitCode == 2, result.ToString());
      Assert.StartsWith($"Error: {error}", result.Error);
      Assert.StartsWith($"{nl}Usage: sdlna ", result.Output);
    }

    /// <summary>
    ///   GetOptNet reports a number it can't read as a mistake in the
    ///   options class, not as a GetOptException, so "sdlna -p abc" ended
    ///   with an unhandled exception in Debug builds, and exited with
    ///   status 1 without usage in Release builds.
    /// </summary>
    [Theory]
    [InlineData("-p", "abc")]
    [InlineData("--port=99999999999")]
    public void AnUnreadableNumberIsAUsageError(params string[] args)
    {
      var result = ProgramRunner.Run(args);

      Assert.True(result.ExitCode == 2, result.ToString());
      Assert.StartsWith("Error: Invalid value: ", result.Error);
      Assert.StartsWith($"{nl}Usage: sdlna ", result.Output);
    }
  }
}
