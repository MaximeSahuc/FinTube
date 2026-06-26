// Firefox exposes the promise-based `browser` namespace. Fall back to `chrome`
// so the rest of this file mirrors the Chromium build one-to-one.
const browser = globalThis.browser ?? globalThis.chrome;

/**
 * Background event page for the FinTube Cookie Exporter (Firefox).
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
    await browser.action.setBadgeBackgroundColor({ color: BADGE_COLOR, tabId });
    await browser.action.setBadgeText({ text: supported ? BADGE_TEXT : "", tabId });
  } catch {
    // tab might be gone; ignore
  }
}

browser.tabs.onActivated.addListener(async ({ tabId }) => {
  try {
    const tab = await browser.tabs.get(tabId);
    updateBadge(tabId, tab.url);
  } catch {}
});

browser.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if (changeInfo.url || changeInfo.status === "complete") {
    updateBadge(tabId, tab.url);
  }
});
