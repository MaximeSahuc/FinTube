# FinTube Cookie Exporter - Firefox

A companion extension for [FinTube](../../README.md). It reads your YouTube cookies and lets you copy
them in one click, ready to paste into FinTube's plugin settings in Jellyfin - so yt-dlp can download
age-restricted, private, or members-only videos.

Works in Firefox and other Gecko-based browsers (LibreWolf, Waterfox, Floorp, Zen, Mullvad Browser, …).

## Quick install

1. Use **Firefox Developer Edition, Nightly, or ESR** (regular release Firefox can't install unsigned
   add-ons permanently - for it, see [Temporary install](#temporary-install-any-firefox)).
2. Type `about:config` in the address bar, press Enter, and set `xpinstall.signatures.required` to
   `false`.
3. Download the prebuilt add-on:
   [**fintube-extension-firefox.xpi**](https://github.com/MaximeSahuc/FinTube/raw/master/browser-extensions/fintube-extension-firefox.xpi).
4. Type `about:addons` in the address bar → gear icon → **Install Add-on From File…** → pick the
   `.xpi`.

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

Firefox only loads extensions that carry an add-on **ID**. This one does
(`fintube@maximesahuc.github.io`, set under `browser_specific_settings.gecko` in
[`manifest.json`](./manifest.json)), so it installs cleanly.

### Temporary install (any Firefox)

Regular release Firefox enforces signing, but you can still load the add-on without changing config -
it's **cleared every time you restart Firefox**:

1. Open `about:debugging#/runtime/this-firefox`.
2. Click **Load Temporary Add-on…** and select the **`manifest.json`** inside this folder.

### Usage

1. Log in to [youtube.com](https://www.youtube.com) in Firefox.
2. Click the FinTube icon in the toolbar → **Copy cookies**.
3. In Jellyfin: **Dashboard → Plugins → FinTube → Settings → YouTube Cookies**, paste, and **Save**.
4. Download from the FinTube page as usual.

> [!TIP]
> Cookies expire. If logged-in downloads start failing again, re-export and paste a fresh set.

### Privacy

The extension reads cookies only when you open the popup, and only for YouTube / Google. They are
copied to your clipboard when you click **Copy cookies** and are **never sent anywhere** by the
extension.

### Differences from the Chrome build

- Uses `browser_specific_settings.gecko.id` to give Firefox a stable add-on ID (required to install).
- Uses a non-persistent background **event page** (`background.scripts`) instead of a service worker.
- Talks to the promise-based `browser.*` API (falling back to `chrome.*`), so the popup logic is
  identical to the Chromium build.
