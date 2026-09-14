using System;
using System.Diagnostics;
using System.IO;
using NMaier.SimpleDlna.Utilities;
using SkiaSharp;

namespace NMaier.SimpleDlna.Tests.Support
{
  internal static class TestMedia
  {
    public static FileInfo WriteJpeg(string path, int width, int height)
    {
      using (var bitmap = new SKBitmap(width, height)) {
        using (var canvas = new SKCanvas(bitmap)) {
          canvas.Clear(SKColors.SeaGreen);
        }
        using (var image = SKImage.FromBitmap(bitmap)) {
          using (var data = image.Encode(SKEncodedImageFormat.Jpeg, 90)) {
            using (var file = File.Create(path)) {
              data.SaveTo(file);
            }
          }
        }
      }
      return new FileInfo(path);
    }

    public static bool IsJpeg(byte[] data)
    {
      return data != null && data.Length > 3 &&
             data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
    }

    public static bool HasFFmpeg => FFmpeg.FFmpegExecutable != null;

    /// <summary>
    ///   Generates a short H.264 video with ffmpeg. Callers must skip when
    ///   <see cref="HasFFmpeg" /> is false.
    /// </summary>
    public static FileInfo WriteVideo(string path, int seconds = 3)
    {
      var start = new ProcessStartInfo(FFmpeg.FFmpegExecutable)
      {
        UseShellExecute = false,
        RedirectStandardError = true,
        RedirectStandardOutput = true
      };
      foreach (var arg in new[]
      {
        "-v", "error", "-y", "-f", "lavfi",
        "-i", $"testsrc=duration={seconds}:size=320x240:rate=25",
        "-c:v", "libx264", "-pix_fmt", "yuv420p", path
      }) {
        start.ArgumentList.Add(arg);
      }
      using (var process = Process.Start(start)) {
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) {
          throw new InvalidOperationException($"ffmpeg failed: {error}");
        }
      }
      return new FileInfo(path);
    }
  }
}
