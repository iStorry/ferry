# Publishing Browser Link to the stores

The package and this listing are ready. Sending them is for you: it takes your own developer accounts, and it is public. Fees and
rules below are from memory, so check them when you sign up.

## Steps

1. Replace `extensions/browser/icons/icon128.png` if Ferry has its own icon by then.
2. Build the zip:

   ```powershell
   tools/package-extension.ps1
   ```

3. Send `artifacts/extension/ferry-browser-link-1.0.0.zip` to each store:
   - **Chrome Web Store:** a developer account at chrome.google.com/webstore/devconsole, with a one-time fee.
   - **Edge Add-ons:** a Partner Center account at partner.microsoft.com/dashboard/microsoftedge, free.
4. Each store gives the extension its own ID. Add both to `StoreExtensionIds` in
   `src/Ferry.Plugins.BrowserLink/BrowserLinkPlugin.cs`, and release Ferry, or the browser won't be allowed to reach the app.

## The risk to know about

Chrome Web Store policy bans extensions that download from YouTube. Browser Link has nothing for YouTube, and only sends a link the
user chose to the app. But a review could still object, since Ferry's Media Sites plugin can do it through yt-dlp, so don't mention
that in the listing, and be ready to answer if asked.

## Listing

**Name:** Ferry Browser Link

**Short description:** Send a link to the Ferry download manager with a right-click.

**Description:** Right-click any link and choose Download with Ferry. It goes to the Ferry app on your PC, with this browser's cookies
for that link's site, so a file behind your login downloads too. Needs the Ferry app, with Browser Link turned on in its Plugins.

**Single purpose:** Send a link the user chooses to the Ferry download manager on the same PC.

**Why it asks for each permission:**

| Permission | Why |
|---|---|
| `contextMenus` | Adds "Download with Ferry" when you right-click a link |
| `cookies` | Reads the cookies of the site of the link you chose, so a file behind your login can download |
| `nativeMessaging` | Hands the link to the Ferry app on your PC |
| Access to all sites | The link you right-click can be on any site, and its cookies are only read when you choose it |

**Data use, for the privacy form:** The link, the page it was on and that site's cookies go to the Ferry app on the same PC, and only
when you click. Nothing goes to the developer or anyone else. The app keeps cookies encrypted until the file is done, and sends
them only to the link's own site.

**Privacy policy:** Chrome asks for an address for one, since the extension handles cookies. The paragraph above can be the page.
