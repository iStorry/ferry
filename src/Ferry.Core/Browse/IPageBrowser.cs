using Ferry.Core;
namespace Ferry.Core.Browse;

/// <summary>
/// Reads an album or file page and says what is on it, for the Browser page: a plugin does on the server, and the PC asks
/// the server.
/// </summary>
public interface IPageBrowser
{
    /// <exception cref="StageException">the page could not be read; <see cref="StageException.Stage"/> says where</exception>
    Task<BrowsedPage> OpenAsync(PageLink link, CancellationToken cancel);
}
