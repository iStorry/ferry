// "Download with Ferry" on a link. Nothing is sent until it is clicked, and only the cookies of the link's own site go with it.
const HOST = "com.ferry.browser_link";
const MENU = "download-with-ferry";

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({ id: MENU, title: "Download with Ferry", contexts: ["link"] });
});

chrome.contextMenus.onClicked.addListener(async (info) => {
  const url = info.linkUrl ?? "";
  const isWeb = /^https?:/.test(url);
  if (info.menuItemId !== MENU || (!isWeb && !url.startsWith("magnet:"))) {
    return;
  }
  const cookies = isWeb ? await chrome.cookies.getAll({ url }) : [];
  chrome.runtime.sendNativeMessage(HOST, {
    url,
    referer: info.pageUrl ?? "",
    cookies: cookies.map((cookie) => `${cookie.name}=${cookie.value}`).join("; "),
  });
});
