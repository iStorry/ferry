# Changelog

## 0.0.3 (pre-release)

On the Browser page, with an album open that was opened from its card, Left and Right go to the album before and after
it, in the order the cards show.

- The album after the one you open is read ahead, so going on to it is quick; near the last card, the next page of albums
  is read too.
- A key held down goes one album only, so the site isn't asked for every album passed.
- A control that uses the arrows keeps them: the file grid, the find box, the kinds.

It has been built, and not yet used.

## 0.0.2 (pre-release)

A site's check for a person is shown to the person. Some sites put a check before their pages that only a person in a
browser can pass: a picture of characters to type, say. A plugin can now tell Ferry so, and never answers one itself.

- On the Browser page, the site's own page opens in a window, reached through your server, so the site sees the address
  its pages are then read from. You pass the check; Ferry types nothing. The window closes when the site has let you
  through, and what you asked for is read again.
- One window at a time. A check you close without passing is left alone for half a minute.
- Away from the Browser page (a watched album, a file in the queue) nothing is shown, and the failure says what to do.
- It needs your server's SSH to pass connections on (`AllowTcpForwarding`, on unless a host turned it off) and Microsoft
  Edge WebView2, which comes with Windows 11.
- For plugin writers: `StageException.Check` and `ICheckedPlugin`, in docs/writing-plugins.md. The contract's number
  stays 1, so packages made for 0.0.1 still load.

The window and the tunnel behind it were built and tested in parts, and have not yet been used against a real site.

## 0.0.1 (pre-release)

The first public version of Ferry: a download manager for Windows, with sixteen plugins that come with it, Server Support
for downloading on a Linux server of your own, and plugins people install.
