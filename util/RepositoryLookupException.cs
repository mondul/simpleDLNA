using System;

namespace NMaier.SimpleDlna.Utilities
{
  [Serializable]
  public sealed class RepositoryLookupException : ArgumentException
  {
    public RepositoryLookupException()
    {
    }

    public RepositoryLookupException(string key)
      : base($"Failed to lookup {key}")
    {
      Key = key;
    }

    public RepositoryLookupException(string message, Exception inner)
      : base(message, inner)
    {
    }

    public string Key { get; private set; }
  }
}
