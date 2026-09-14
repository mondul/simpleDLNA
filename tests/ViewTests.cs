using System;
using System.IO;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Server.Metadata;
using NMaier.SimpleDlna.Server.Views;
using NMaier.SimpleDlna.Utilities;
using Xunit;

namespace NMaier.SimpleDlna.Tests
{
  /// <summary>
  ///   The filtering views and their options, as documented in the readme and
  ///   by --list-views.
  /// </summary>
  public sealed class ViewTests
  {
    private sealed class Item : IMediaResource, IMetaResolution, IMetaInfo
    {
      public Item(string title, string path = null, int? width = null,
        int? height = null, long? size = null, DateTime? date = null)
      {
        Title = title;
        Path = path ?? "/media/" + title;
        MetaWidth = width;
        MetaHeight = height;
        InfoSize = size;
        InfoDate = date ?? DateTime.UtcNow;
      }

      public DateTime InfoDate { get; }

      public long? InfoSize { get; }

      public int? MetaHeight { get; }

      public int? MetaWidth { get; }

      public string Id { get; set; }

      public string Path { get; }

      public IHeaders Properties => new RawHeaders();

      public string Title { get; }

      public IMediaCoverResource Cover => null;

      public DlnaMediaTypes MediaType => DlnaMediaTypes.Image;

      public string PN => "JPEG_LRG";

      public DlnaMime Type => DlnaMime.ImageJPEG;

      public int CompareTo(IMediaItem other) => string.CompareOrdinal(Title, other?.Title);

      public Stream CreateContentStream() => Stream.Null;

      public bool Equals(IMediaItem other) => Title == other?.Title;

      public string ToComparableTitle() => Title;
    }

    private static IFilteredView Filter(string view)
    {
      return Assert.IsAssignableFrom<IFilteredView>(ViewRepository.Lookup(view));
    }

    [Fact]
    public void FilterMatchesAnyWordAnywhereInTitleOrPathIgnoringCase()
    {
      var view = Filter("filter:beach,holiday");

      Assert.True(view.Allowed(new Item("Sunny BEACH day")));
      Assert.True(view.Allowed(new Item("trip", "/photos/holiday/trip.jpg")));
      Assert.False(view.Allowed(new Item("mountains", "/photos/alps/mountains.jpg")));
    }

    [Fact]
    public void FilterWordWithWildcardMustMatchTheWholeValue()
    {
      var view = Filter("filter:bea*");

      Assert.True(view.Allowed(new Item("beach", "/x")));
      Assert.False(view.Allowed(new Item("the beach", "/x")));
    }

    [Fact]
    public void DimensionMinIsTheShorterSideAndMaxTheLongerSide()
    {
      var landscape = new Item("big", width: 800, height: 600);
      var small = new Item("small", width: 640, height: 480);

      Assert.True(Filter("dimension:min=600").Allowed(landscape));
      Assert.False(Filter("dimension:min=600").Allowed(small));

      Assert.True(Filter("dimension:max=700").Allowed(small));
      Assert.False(Filter("dimension:max=700").Allowed(landscape));
    }

    [Fact]
    public void DimensionHidesItemsWithoutAKnownSize()
    {
      Assert.False(Filter("dimension:min=1").Allowed(new Item("song")));
    }

    [Fact]
    public void LargeTakesItsSizeInMegabytes()
    {
      const long megabyte = 1024 * 1024;
      var view = Filter("large:size=10");

      Assert.True(view.Allowed(new Item("big", size: 10 * megabyte)));
      Assert.False(view.Allowed(new Item("small", size: 10 * megabyte - 1)));
    }

    [Fact]
    public void NewUsesTheGivenEarliestDate()
    {
      var view = Filter("new:date=2026-01-31");

      Assert.True(view.Allowed(new Item("recent", date: new DateTime(2026, 2, 1))));
      Assert.False(view.Allowed(new Item("old", date: new DateTime(2025, 12, 1))));
    }

    [Theory]
    [InlineData("bytitle")]
    [InlineData("flatten")]
    [InlineData("music")]
    [InlineData("plain")]
    [InlineData("series:no-cascade")]
    [InlineData("sites")]
    public void DocumentedViewsExist(string view)
    {
      Assert.NotNull(ViewRepository.Lookup(view));
    }

    [Fact]
    public void UnknownViewIsReported()
    {
      Assert.Throws<RepositoryLookupException>(() => ViewRepository.Lookup("musik"));
    }
  }
}
