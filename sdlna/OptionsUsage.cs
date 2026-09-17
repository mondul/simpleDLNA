using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NMaier.GetOptNet;

// The option list and its layout are adapted from GetOptNet 4.0.8
// (GetOpt_Usage.cs and OptInfo.cs, https://github.com/nmaier/GetOptNet),
// which is distributed under this notice:
//
// Copyright (c) 2009-2019 Nils Maier
//
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.

namespace NMaier.SimpleDlna
{
  internal partial class Options
  {
    /// <summary>
    ///   The name usage shows, as the epilog and "sdlna --server help" do.
    /// </summary>
    private const string PROGRAM_NAME = "sdlna";

    /// <summary>
    ///   GetOptNet's AssembleUsage starts with
    ///   new FileInfo(GetEntryAssembly().Location) to name the program.
    ///   Location is "" in a single-file executable, so that threw before
    ///   anything was written: in every release, "sdlna --help" printed
    ///   nothing and the usage after an unknown option ended in an unhandled
    ///   ArgumentException. This builds the same text without the file.
    ///   GetOptNet keeps its option list private, so the list and its
    ///   layout are rebuilt here from the same attributes, for the attribute
    ///   settings Options uses; UsageTests holds the result to GetOptNet's
    ///   own output.
    /// </summary>
    public override string AssembleUsage(int width,
      HelpCategory category = HelpCategory.Basic, bool fixedWidthFont = true,
      bool introAndEpilogue = true, bool includeCommands = true)
    {
      // includeCommands is moot: Options registers no commands.
      var nl = Environment.NewLine;
      var rv = new StringBuilder();

      if (introAndEpilogue) {
        var intro = GetUsageIntro(PROGRAM_NAME, string.Empty);
        if (!string.IsNullOrEmpty(intro)) {
          rv.Append(intro);
          rv.Append(nl);
        }
      }

      var entries = UsageEntries(category);
      if (entries.Count > 0) {
        rv.Append(nl);
        rv.Append("Options:");
        rv.Append(nl);

        // GetOptNet's layout, overlong lines included: help text starts
        // after the option when that fits in half the width, and is then
        // wrapped as if it had started in the help column.
        var maxLine = width / 2;
        var maxArg = width / 4;
        foreach (var (argText, helpText) in entries) {
          rv.Append(argText);
          var len = argText.Length;
          if (!fixedWidthFont || len > maxLine) {
            rv.Append(nl);
            len = 0;
          }
          rv.Append(' ', Math.Max(1, maxArg - len));

          len = width - maxArg;
          foreach (var word in helpText.Split(' ', '\t')) {
            var w = word + " ";
            if (len < w.Length) {
              rv.Append(nl);
              rv.Append(' ', maxArg);
              len = width - maxArg;
            }
            rv.Append(w);
            len -= w.Length;
          }
          rv.Append(nl);
        }
      }

      if (introAndEpilogue) {
        rv.Append(nl);
        rv.Append(EPILOG);
      }

      rv.Append(nl);
      return rv.ToString();
    }

    /// <summary>
    ///   GetOptNet's own rendering, for tests to compare against. It throws
    ///   when the entry assembly has no file, as in a single-file executable.
    /// </summary>
    internal string AssembleGetOptNetUsage(int width, HelpCategory category,
      bool fixedWidthFont, bool introAndEpilogue)
    {
      return base.AssembleUsage(width, category, fixedWidthFont, introAndEpilogue);
    }

    /// <summary>
    ///   One line per option, sorted by the name shown first, then the
    ///   positional parameters. Options keeps GetOptNet's default CaseType,
    ///   UsagePrefix and UsageShowAliases: long names in lower case, dashes,
    ///   and no aliases.
    /// </summary>
    private List<(string ArgText, string HelpText)> UsageEntries(
      HelpCategory category)
    {
      var options = new List<(string SortName, string ArgText, string HelpText)>();
      (string ArgText, string HelpText)? parameters = null;
      foreach (var (member, memberType) in OptionMembers()) {
        var elementType = memberType.IsArray
          ? memberType.GetElementType()
          : memberType;

        var parametersAttribute = member.GetCustomAttribute<ParametersAttribute>();
        if (parametersAttribute != null) {
          var helpVar = string.IsNullOrEmpty(parametersAttribute.HelpVar)
            ? DefaultHelpVar(elementType)
            : parametersAttribute.HelpVar.ToUpperInvariant();
          parameters = parametersAttribute.Min == 1 && parametersAttribute.Max == 1
            ? ("   " + helpVar, "Positional parameter")
            : ("   " + helpVar + ", ...", "Positional parameters");
        }

        var argument = member.GetCustomAttribute<ArgumentAttribute>();
        if (argument == null || argument.Category > category) {
          continue;
        }
        var longName = (string.IsNullOrEmpty(argument.Arg)
          ? member.Name
          : argument.Arg).ToLowerInvariant();
        var flag = memberType == typeof (bool);
        var value = string.IsNullOrEmpty(argument.HelpVar)
          ? DefaultHelpVar(elementType)
          : argument.HelpVar.ToUpperInvariant();

        var names = new List<string>();
        var shortArgument = member.GetCustomAttribute<ShortArgumentAttribute>();
        if (shortArgument != null) {
          names.Add(flag ? $"-{shortArgument.Arg}" : $"-{shortArgument.Arg} {value}");
        }
        names.Add(flag ? $"--{longName}" : $"--{longName}={value}");
        options.Add((
          shortArgument != null ? shortArgument.Arg.ToString() : longName,
          "   " + string.Join(", ", names),
          argument.HelpText));
      }

      var entries = options
        .OrderBy(o => o.SortName, StringComparer.Ordinal)
        .Select(o => (o.ArgText, o.HelpText))
        .ToList();
      if (parameters != null) {
        entries.Add(parameters.Value);
      }
      return entries;
    }

    /// <summary>
    ///   What GetOptNet shows for the value of an option without a HelpVar.
    /// </summary>
    private static string DefaultHelpVar(Type type)
    {
      if (type.IsEnum) {
        var names = Enum.GetNames(type).Select(n => n.ToLowerInvariant());
        return $"<{string.Join(", ", names)}>";
      }
      if (type == typeof (bool)) {
        return "BOOL";
      }
      if (type == typeof (ushort)) {
        return "USHORT";
      }
      if (type == typeof (int)) {
        return "INT";
      }
      if (type == typeof (uint)) {
        return "UINT";
      }
      if (type == typeof (long)) {
        return "LONG";
      }
      if (type == typeof (ulong)) {
        return "ULONG";
      }
      if (type == typeof (DirectoryInfo)) {
        return "DIRECTORY";
      }
      if (type == typeof (FileInfo)) {
        return "FILE";
      }
      if (type == typeof (string)) {
        return "...";
      }
      return type.Name.ToUpperInvariant();
    }
  }
}
