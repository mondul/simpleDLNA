using Xunit;

namespace NMaier.SimpleDlna.Tests.Support
{
  /// <summary>
  ///   Tests that change process-wide state, such as the console writers or
  ///   the HOME environment variable. They run one at a time, after the
  ///   parallel tests.
  /// </summary>
  [CollectionDefinition(NAME, DisableParallelization = true)]
  public sealed class ProcessStateCollection
  {
    public const string NAME = "Process-wide state";
  }

  /// <summary>
  ///   Tests that start a real HTTP server. Each binds its own port, but the
  ///   SSDP responder shares UDP 1900, so they run one at a time.
  /// </summary>
  [CollectionDefinition(NAME, DisableParallelization = true)]
  public sealed class HttpServerCollection
  {
    public const string NAME = "HTTP server";
  }
}
