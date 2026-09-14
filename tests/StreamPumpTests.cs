using System;
using System.IO;
using System.Threading;
using NMaier.SimpleDlna.Utilities;
using Xunit;

namespace NMaier.SimpleDlna.Tests
{
  public sealed class StreamPumpTests
  {
    /// <summary>
    ///   Regression: completion callbacks were dispatched with delegate
    ///   BeginInvoke, which throws PlatformNotSupportedException on .NET. The
    ///   HTTP server finishes every response through this callback.
    /// </summary>
    [Fact]
    public void CopiesEverythingAndInvokesTheCompletionCallback()
    {
      var data = new byte[1 << 20];
      new Random(42).NextBytes(data);
      var output = new MemoryStream();
      var done = new ManualResetEventSlim();
      var result = StreamPumpResult.Aborted;

      using (var input = new MemoryStream(data)) {
        using (var pump = new StreamPump(input, output, 4096)) {
          pump.Pump((p, r) =>
          {
            result = r;
            done.Set();
          });
          Assert.True(
            done.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
            "the completion callback never ran");
        }
      }

      Assert.Equal(StreamPumpResult.Delivered, result);
      Assert.Equal(data, output.ToArray());
    }

    [Fact]
    public void ThrowingCallbackDoesNotStopThePumpFromFinishing()
    {
      using (var input = new MemoryStream(new byte[100])) {
        using (var pump = new StreamPump(input, new MemoryStream(), 16)) {
          pump.Pump((p, r) => throw new InvalidOperationException("callback failure"));
          Assert.True(pump.Wait(10000));
        }
      }
    }
  }
}
