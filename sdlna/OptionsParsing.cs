using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NMaier.GetOptNet;

namespace NMaier.SimpleDlna
{
  internal partial class Options
  {
    /// <summary>
    ///   GetOptNet's pattern for short options (regDashesShort in its
    ///   GetOpt.cs): a dash, a letter or digit, then more options of the
    ///   group or a value. An argument that matches neither this nor a long
    ///   option is a parameter.
    /// </summary>
    private static readonly Regex shortArguments =
      new Regex(@"^\s*-([\w\d].*?)\s*$");

    /// <summary>
    ///   Parses the command line, working around two of GetOptNet's
    ///   failures.
    /// </summary>
    /// <remarks>
    ///   "-?" is read as --help; see <see cref="TranslateHelpArguments" />.
    ///   And GetOptNet reports a number it can't read ("-p abc", or one too
    ///   large for an int) as a ProgrammingErrorException, which is meant for
    ///   mistakes in the options class, instead of a GetOptException. So
    ///   Main didn't take it for a usage error: Debug builds ended with an
    ///   unhandled exception, and Release builds exited with status 1
    ///   without printing usage.
    /// </remarks>
    /// <exception cref="GetOptException">The command line is invalid.</exception>
    internal void ParseCommandLine(IReadOnlyList<string> args, bool windows)
    {
      try {
        Parse(TranslateHelpArguments(args, windows));
      }
      catch (ProgrammingErrorException ex) when (
        ex.InnerException is FormatException ||
        ex.InnerException is OverflowException) {
        throw new GetOptException(
          $"Invalid value: {ex.InnerException.Message}", ex);
      }
    }

    /// <summary>
    ///   Replaces "-?" with "--help" where GetOptNet would take it for a
    ///   folder.
    /// </summary>
    /// <remarks>
    ///   Usage lists "-?" as the short form of --help, but GetOptNet reads
    ///   short options only when a letter or digit follows the dash, so it
    ///   took "-?" for a folder: "sdlna -?" started the HTTP server, then
    ///   failed with "The directory name '.../-?' does not exist". After
    ///   "--" and as the value of a short option ("-n -?"), "-?" stays as it
    ///   was. With <paramref name="windows" />, "/?" is replaced too, as
    ///   Windows programs read it as help; no Windows folder can be named
    ///   "?".
    /// </remarks>
    internal string[] TranslateHelpArguments(IReadOnlyList<string> args,
      bool windows)
    {
      var valueArguments = ShortArgumentsWithValues();
      var translated = args.ToArray();
      for (var i = 0; i < translated.Length; ++i) {
        var arg = translated[i].Trim();
        if (arg == "--") {
          break;
        }
        if (arg == "-?" || windows && arg == "/?") {
          translated[i] = "--help";
          continue;
        }
        // GetOptNet gives the next argument to an option that ends its
        // group and takes a value, as in "-n name" or "-dn name". Earlier in
        // the group, the option takes the rest of the group instead.
        var match = shortArguments.Match(arg);
        if (match.Success) {
          var group = match.Groups[1].Value;
          if (group.IndexOfAny(valueArguments) == group.Length - 1) {
            ++i;
          }
        }
      }
      return translated;
    }

    /// <summary>
    ///   The short names of the options that take a value: all but the
    ///   flags, since Options has no counted options.
    /// </summary>
    private char[] ShortArgumentsWithValues()
    {
      return OptionMembers()
        .Where(m => m.Type != typeof (bool) &&
                    m.Member.IsDefined(typeof (ArgumentAttribute)))
        .SelectMany(m => m.Member.GetCustomAttributes<ShortArgumentAttribute>()
                      .Select(a => a.Arg)
                      .Concat(m.Member.GetCustomAttributes<ShortArgumentAliasAttribute>()
                                .Select(a => a.Alias)))
        .ToArray();
    }

    /// <summary>
    ///   The fields and properties GetOptNet reads options and parameters
    ///   from, with their types.
    /// </summary>
    private IEnumerable<(MemberInfo Member, Type Type)> OptionMembers()
    {
      const BindingFlags FLAGS =
        BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance;
      var type = GetType();
      return type.GetFields(FLAGS)
        .Select(f => ((MemberInfo)f, f.FieldType))
        .Concat(type.GetProperties(FLAGS)
                  .Select(p => ((MemberInfo)p, p.PropertyType)));
    }
  }
}
