/**
 * Background service worker for the FinTube Cookie Exporter (Chromium).
 * Its only job is to light up the toolbar badge when the active tab is a
 * YouTube page, hinting that cookies are ready to export. The actual cookie
 * reading happens in the popup.
 */

const SUPPORTED_DOMAINS = ["youtube.com", "youtu.be"];
const BADGE_COLOR = "#00A4DC";
const BADGE_TEXT = "•";

function isSupportedUrl(url) {
  if (!url) return false;
  try {
    const host = new URL(url).hostname;
    return SUPPORTED_DOMAINS.some((d) => host === d || host.endsWith("." + d));
  } catch {
    return false;
  }
}

async function updateBadge(tabId, url) {
  const supported = isSupportedUrl(url);
  try {
    await chrome.action.setBadgeBackgroundColor({ color: BADGE_COLOR, tabId });
    await chrome.action.setBadgeText({ text: supported ? BADGE_TEXT : "", tabId });
  } catch {
    // tab might be gone; ignore
  }
}

chrome.tabs.onActivated.addListener(async ({ tabId }) => {
  try {
    const tab = await chrome.tabs.get(tabId);
    updateBadge(tabId, tab.url);
  } catch {}
});

chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if (changeInfo.url || changeInfo.status === "complete") {
    updateBadge(tabId, tab.url);
  }
});
