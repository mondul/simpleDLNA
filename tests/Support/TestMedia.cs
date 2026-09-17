using System;
using System.Diagnostics;
using System.IO;
using NMaier.SimpleDlna.Utilities;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace NMaier.SimpleDlna.Tests.Support
{
  internal static class TestMedia
  {
    public static readonly Rgb24 SeaGreen = new Rgb24(46, 139, 87);

    public static FileInfo WriteJpeg(string path, int width, int height)
    {
      using (var image = new Image<Rgb24>(width, height, SeaGreen)) {
        image.SaveAsJpeg(path, new JpegEncoder {Quality = 90});
      }
      return new FileInfo(path);
    }

    /// <summary>
    ///   A PNG whose left half is opaque white and right half fully
    ///   transparent (but white underneath).
    /// </summary>
    public static FileInfo WriteHalfTransparentPng(string path, int width, int height)
    {
      using (var image = new Image<Rgba32>(width, height, new Rgba32(255, 255, 255, 255))) {
        image.ProcessPixelRows(rows =>
        {
          for (var y = 0; y < rows.Height; y++) {
            var row = rows.GetRowSpan(y);
            for (var x = width / 2; x < width; x++) {
              row[x] = new Rgba32(255, 255, 255, 0);
            }
          }
        });
        image.SaveAsPng(path);
      }
      return new FileInfo(path);
    }

    /// <summary>
    ///   An animated GIF: one frame of each colour, in order.
    /// </summary>
    public static FileInfo WriteAnimatedGif(string path, int width, int height,
      params Rgba32[] colours)
    {
      using (var image = new Image<Rgba32>(width, height, colours[0])) {
        foreach (var colour in colours.AsSpan(1)) {
          using (var frame = new Image<Rgba32>(width, height, colour)) {
            image.Frames.AddFrame(frame.Frames.RootFrame);
          }
        }
        image.SaveAsGif(path, new GifEncoder());
      }
      return new FileInfo(path);
    }

    public static Image<Rgb24> Decode(byte[] data)
    {
      return Image.Load<Rgb24>(data);
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
