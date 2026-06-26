# FinTube Cookie Exporter - Chrome

A companion extension for [FinTube](../../README.md). It reads your YouTube cookies and lets you copy
them in one click, ready to paste into FinTube's plugin settings in Jellyfin - so yt-dlp can download
age-restricted, private, or members-only videos.

Works in Chrome, Edge, Brave, Vivaldi, and other Chromium-based browsers.

## Quick install

1. Use Chrome, Edge, Brave, Vivaldi, or another Chromium-based browser.
2. Download the prebuilt extension:
   [**fintube-extension-chrome.zip**](https://github.com/MaximeSahuc/FinTube/raw/master/browser-extensions/fintube-extension-chrome.zip)
   and unzip it.
3. Type `chrome://extensions` in the address bar (or `edge://extensions`, …), press Enter, then turn
   on **Developer mode** (top-right).
4. Click **Load unpacked** and select the unzipped folder.

Then open YouTube (logged in), click the icon → **Copy cookies**, and paste into
**Jellyfin → Dashboard → Plugins → FinTube → Settings**.

---

## More details

### What it does

- **One-click cookie export** - reads your `youtube.com` (and `google.com` consent) cookies and
  formats them as a Netscape cookie file, exactly what yt-dlp expects.
- **Copy to clipboard** - no manual exporting, no files on disk.
- **Smart badge** - the icon shows a dot when you're on a YouTube tab.
- **Auto theme** - the popup follows your system's light / dark mode.

### How it works

Unlike a desktop app, FinTube runs inside Jellyfin, so there's nothing to deep-link into. The
extension simply reads cookies locally and puts a Netscape cookie file on your clipboard. You paste
it into FinTube's settings; FinTube writes it to a cookie file and passes it to yt-dlp via
`--cookies`.

### Usage

1. Log in to [youtube.com](https://www.youtube.com) in your browser.
2. Click the FinTube icon in the toolbar → **Copy cookies**.
3. In Jellyfin: **Dashboard → Plugins → FinTube → Settings → YouTube Cookies**, paste, and **Save**.
4. Download from the FinTube page as usual.

> [!TIP]
> Cookies expire. If logged-in downloads start failing again, re-export and paste a fresh set.

### Privacy

The extension reads cookies only when you open the popup, and only for YouTube / Google. They are
copied to your clipboard when you click **Copy cookies** and are **never sent anywhere** by the
extension.
