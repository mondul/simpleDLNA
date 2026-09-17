using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NMaier.GetOptNet;
using NMaier.SimpleDlna.Tests.Support;
using Xunit;

namespace NMaier.SimpleDlna.Tests
{
  /// <summary>
  ///   The usage text that "sdlna --help" prints, as does an invalid option.
  /// </summary>
  [Collection(ProcessStateCollection.NAME)]
  public sealed class UsageTests
  {
    private static readonly string nl = Environment.NewLine;

    /// <summary>
    ///   GetOptNet named the program after the entry assembly's file, and
    ///   Assembly.Location is "" in a single-file executable, so every
    ///   release threw instead of printing usage: "sdlna --help" printed
    ///   nothing, and an unknown option ended in an unhandled
    ///   ArgumentException. An assembly loaded from bytes has no Location
    ///   either, so as the entry assembly it stands in for a release build.
    /// </summary>
    [Fact]
    public void UsageIsPrintedWhenTheProgramHasNoFile()
    {
      var fileless = Assembly.Load(File.ReadAllBytes(typeof (Options).Assembly.Location));
      Assert.Equal("", fileless.Location);

      var entry = Assembly.GetEntryAssembly();
      var originalOut = Console.Out;
      var output = new StringWriter();
      Assembly.SetEntryAssembly(fileless);
      Console.SetOut(output);
      try {
        new Options().PrintUsage();
      }
      finally {
        Console.SetOut(originalOut);
        Assembly.SetEntryAssembly(entry);
      }

      var usage = output.ToString();
      Assert.StartsWith($"Usage: sdlna [OPTION] [...] Directory Directory ...{nl}{nl}Options:{nl}", usage);
      Assert.Contains($"{nl}   -?, --help ", usage);
      Assert.EndsWith($"see 'sdlna --server help'.{nl}", usage);
    }

    /// <summary>
    ///   Options rebuilds the option list that GetOptNet keeps private. Apart
    ///   from the program's name, the text must stay what GetOptNet shows, so
    ///   the two can't drift apart when options change.
    /// </summary>
    [Theory]
    [InlineData(80, true, true)]
    [InlineData(20, true, true)]
    [InlineData(40, true, true)]
    [InlineData(120, true, true)]
    [InlineData(200, true, true)]
    [InlineData(80, false, true)]
    [InlineData(80, true, false)]
    public void UsageLooksAsGetOptNetShowsIt(int width, bool fixedWidthFont, bool introAndEpilogue)
    {
      // GetOptNet upper-cases help variables in the current UI culture,
      // which turns "file" into "FİLE" in Turkish. Options doesn't.
      var culture = CultureInfo.CurrentCulture;
      var uiCulture = CultureInfo.CurrentUICulture;
      CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
      CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
      try {
        var options = new Options();
        foreach (var category in new[] {HelpCategory.Basic, HelpCategory.Advanced}) {
          // GetOptNet names the program after the test host.
          var expected = Regex.Replace(
            options.AssembleGetOptNetUsage(width, category, fixedWidthFont, introAndEpilogue),
            @"^Usage: \S+ ", "Usage: sdlna ");
          Assert.Equal(expected, options.AssembleUsage(width, category, fixedWidthFont, introAndEpilogue));
        }
      }
      finally {
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = uiCulture;
      }
    }
  }
}
