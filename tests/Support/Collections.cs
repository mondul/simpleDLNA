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
  ///   Tests that talk to the real HTTP server. They share one server (see
  ///   DlnaTestServer) and run one at a time, so no test sees another's mounts.
  /// </summary>
  [CollectionDefinition(NAME, DisableParallelization = true)]
  public sealed class HttpServerCollection : ICollectionFixture<DlnaTestServer>
  {
    public const string NAME = "HTTP server";
  }
}
