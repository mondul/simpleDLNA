using System;
using System.Globalization;
using Xunit;

namespace NMaier.SimpleDlna.Tests.Support
{
  /// <summary>
  ///   Switches to a culture that writes decimals with a comma and switches
  ///   back when disposed, so culture-sensitive formatting fails on every
  ///   machine rather than only on those set up that way.
  /// </summary>
  internal sealed class CultureScope : IDisposable
  {
    private readonly CultureInfo previous;

    private readonly CultureInfo previousDefault;

    private readonly bool processWide;

    private CultureScope(CultureInfo culture, bool processWide)
    {
      this.processWide = processWide;
      previous = CultureInfo.CurrentCulture;
      previousDefault = CultureInfo.DefaultThreadCurrentCulture;
      CultureInfo.CurrentCulture = culture;
      if (processWide) {
        CultureInfo.DefaultThreadCurrentCulture = culture;
      }
    }

    public void Dispose()
    {
      CultureInfo.CurrentCulture = previous;
      if (processWide) {
        CultureInfo.DefaultThreadCurrentCulture = previousDefault;
      }
    }

    /// <summary>
    ///   Switches the current culture to es-CO, whose decimal separator is a
    ///   comma. Skips the test when that culture's data is unavailable.
    /// </summary>
    /// <param name="processWide">
    ///   Also make es-CO the culture of every thread that hasn't chosen its
    ///   own, such as the HTTP server's. Only for tests in a collection that
    ///   runs alone.
    /// </param>
    public static CultureScope CommaDecimals(bool processWide = false)
    {
      CultureInfo culture = null;
      try {
        culture = CultureInfo.GetCultureInfo("es-CO");
      }
      catch (CultureNotFoundException) {
      }
      // Under invariant globalization (common in containers) the culture
      // either doesn't exist or formats like the invariant culture, and the
      // test would pass without proving anything.
      Assert.SkipWhen(
        culture?.NumberFormat.NumberDecimalSeparator != ",",
        "es-CO culture data is unavailable (invariant globalization?)");
      return new CultureScope(culture, processWide);
    }
  }
}
