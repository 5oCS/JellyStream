using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellystream;

// Search finds books in LibriVox first and the Internet Archive second. A hit is kept as a library
// album stub whose external ID is the channel's book folder, where the chapters are listed: making
// a chapter now would cost a chapter-list fetch per result, past LibriVox's rate limit.
public sealed class BookSearch(LibriVoxCatalog books, InternetArchiveCatalog archive, ILibraryManager library, SearchHistory history,
    IHttpContextAccessor contexts, IAuthorizationContext authorization, ILogger<BookSearch> logger) : IInternalSearchProvider
{
    // Jellyfin derives channel item IDs from external ID + channel name + "16".
    public const string FolderKey = "search" + AudiobookChannel.ChannelName + "16";
    public string Name => "Jellystream Audiobooks";
    public MetadataPluginType Type => MetadataPluginType.SearchProvider;
    public int Priority => 50;
    public Guid ChannelId => library.GetNewItemId("Channel " + AudiobookChannel.ChannelName, typeof(Channel));
    public Guid SearchFolderId => library.GetNewItemId(FolderKey, typeof(Folder));
    private static bool Wants(SearchProviderQuery query, BaseItemKind kind) => query.IncludeItemTypes.Length == 0
        ? !query.ExcludeItemTypes.Contains(kind) : query.IncludeItemTypes.Contains(kind);
    public bool CanSearch(SearchProviderQuery query) => query.SearchTerm.Trim().Length is >= 2 and <= 100
        && Wants(query, BaseItemKind.MusicAlbum)
        && (query.MediaTypes.Length == 0 || query.MediaTypes.Contains(MediaType.Audio))
        && (!query.ParentId.HasValue || query.ParentId == Guid.Empty || query.ParentId == ChannelId || query.ParentId == SearchFolderId);

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchProviderQuery query, CancellationToken ct)
    {
        if (!CanSearch(query) || !AudiobookChannel.IsAvailable || contexts.HttpContext is not { } context) return [];
        var auth = await authorization.GetAuthorizationInfo(context);
        if (!auth.IsAuthenticated || !Plugin.Allows(auth.UserId) || (query.UserId is { } user && user != Guid.Empty && user != auth.UserId)) return [];
        try
        {
            var generation = history.Generation;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var limit = Math.Clamp(query.Limit ?? 20, 1, 20);
            var found = new List<AudioBook>(await books.SearchAsync(query.SearchTerm, limit, timeout.Token));
            if (found.Count < limit)
                found.AddRange(await archive.SearchAsync(query.SearchTerm, limit - found.Count, timeout.Token));
            await ChannelNavigation.Gate.WaitAsync(timeout.Token);
            try
            {
                // A search that started before Clear must not refill the list.
                if (generation != history.Generation) return [];
                if (library.GetItemById(ChannelId) is not Channel channel) return [];
                var parent = library.GetItemById(SearchFolderId) as Folder;
                if (parent is null)
                {
                    parent = new Folder { Id = SearchFolderId, Name = "Search results", ExternalId = "search", ChannelId = ChannelId, ParentId = ChannelId, Tags = ["Jellystream"] };
                    parent.OnMetadataChanged();
                    library.CreateItem(parent, channel);
                }
                var results = new List<SearchResult>();
                foreach (var book in found.DistinctBy(hit => (hit.Source, hit.Id)).Take(limit))
                {
                    var info = AudiobookChannel.Book(book);
                    var key = info.Id + AudiobookChannel.ChannelName + "16";
                    var id = library.GetNewItemId(key, typeof(MusicAlbum));
                    var existing = library.GetItemById(id) as MusicAlbum;
                    var item = existing ?? new MusicAlbum { Id = id, DateCreated = DateTime.UtcNow };
                    item.ExternalId = info.Id;
                    item.ChannelId = parent.ChannelId;
                    item.ParentId = parent.Id;
                    item.Name = info.Name;
                    item.Overview = info.Overview;
                    item.ProductionYear = info.ProductionYear;
                    item.Artists = info.Artists;
                    item.AlbumArtists = info.AlbumArtists;
                    item.Tags = item.Tags.Where(tag => tag != SearchHistory.HiddenTag).Append("Jellystream").Distinct().ToArray();
                    item.ProviderIds = info.ProviderIds;
                    // Generic search groups by this key; a shared NULL would collapse unrelated books.
                    item.PresentationUniqueKey = "jellystream-search-" + info.Id;
                    if (info.ImageUrl is not null && !item.HasImage(ImageType.Primary)) item.SetImagePath(ImageType.Primary, info.ImageUrl);
                    item.OnMetadataChanged();
                    if (existing is null) library.CreateItem(item, parent);
                    else await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, timeout.Token);
                    results.Add(new SearchResult(id, 80 - results.Count));
                }
                return results;
            }
            finally { ChannelNavigation.Gate.Release(); }
        }
        // Apps search as you type and cancel the previous search with each letter; that is not a failure.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return []; }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Jellystream audiobook search unavailable ({ErrorType}); local search remains available", ex.GetType().Name);
            return [];
        }
    }
}
