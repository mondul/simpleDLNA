using System;
using System.Reflection;

namespace NMaier.SimpleDlna.Tests.Support
{
  /// <summary>
  ///   Sets private fields. Media files normally fill their metadata from
  ///   taglib or ffmpeg; tests set it directly so they need neither real media
  ///   nor external tools.
  /// </summary>
  internal static class Fields
  {
    private const BindingFlags ALL =
      BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static T Set<T>(this T target, string field, object value)
    {
      for (var type = target.GetType(); type != null; type = type.BaseType) {
        var info = type.GetField(field, ALL);
        if (info != null) {
          info.SetValue(target, value);
          return target;
        }
      }
      throw new MissingFieldException(target.GetType().Name, field);
    }
  }
}
