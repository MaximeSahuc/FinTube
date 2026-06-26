# FinTube

Easily add content from YouTube to your Jellyfin installation

![](https://raw.githubusercontent.com/MaximeSahuc/FinTube/master/Assets/fintube-logo-horizontal.svg)

## Features

- Download YouTube videos as **video** (mp4 / webm) or **audio** (mp3 / opus) straight into a Jellyfin library.
- Pick the target library, audio-only mode, and resolution per download; set a global free-format preference in settings.
- id3v2 tagging (artist, title, album, track) for audio.
- Live download queue with progress and error logs.
- **Automatic dependency management** - yt-dlp and the deno JS runtime are downloaded for you; missing tools can be installed from the FinTube page.
- **Cookie support** for age-restricted, private, and members-only videos, with a companion [browser extension](#browser-extension) to export them in one click.

## Dependencies

FinTube uses [yt-dlp](https://github.com/yt-dlp/yt-dlp) and a JS runtime (deno), both of which it can
download and manage automatically from the FinTube page — no manual setup required.

[id3v2](https://github.com/MaximeSahuc/id3v2) is **optional** and only needed for tagging audio files
(artist, title, album, track):

- **Linux (x86-64 / arm64):** FinTube can install it for you. Open **FinTube → Settings** and click
  **Install id3v2**. It downloads a standalone build (id3lib statically linked; only glibc is required)
  straight into the plugin's managed `bin` folder — no package manager needed.
- **Other platforms / architectures:** no automatic build is available, so install it with your
  package manager and point `exec_ID3` (in **FinTube → Settings**, default `/usr/bin/id3v2`) at it
  (`which id3v2`):
  - Debian/Ubuntu `sudo apt install id3v2`
  - Arch `sudo pacman -S id3v2`
  - macOS (Homebrew) `brew install id3v2`

## Install

### Add my Repository

1. In your Admin Dashboard navigate to "Plugins"
2. Switch to the "Repositories" tab
3. Click "+" and add the Repository `https://raw.githubusercontent.com/MaximeSahuc/FinTube/master/manifest.json`
   Name it "FinTube" - Or whatever helps you remember

### Install and configure the plugin

1. Switch to the "Catalog" tab
2. Search for the "FinTube" plugin and click install
3. Restart the Server and head back to the "Plugins" section
4. Open **FinTube → Settings**. Binaries (yt-dlp, deno) are managed automatically; install any that
   are missing from the FinTube download page.
5. Optionally install id3v2 (Linux: one click in **FinTube → Settings**) to tag music with Artist,
   Title, Album and Track information.

Now you are ready to go, head to the "FinTube" plugin page (at the bottom of your dashboard
navigation), enter information as desired to start importing from YouTube.

## Cookies (age-restricted / private videos)

Some videos won't download because YouTube requires a logged-in session (age-restricted, private, or
members-only content). FinTube can pass your browser cookies to yt-dlp to get past this:

1. Get your cookies in Netscape format (the easiest way is the [browser extension](#browser-extension)
   below).
2. Paste them into **FinTube → Settings → YouTube Cookies** and click **Save**.

FinTube stores the cookies, writes them to a cookie file, and hands it to yt-dlp via `--cookies` for
every download. Cookies expire - re-export and paste a fresh set if logged-in downloads start failing
again.

## Browser extension

FinTube ships with a companion browser extension that exports your YouTube cookies in one click,
ready to paste into the plugin settings. It only copies cookies to your clipboard - it never talks to
your server or uploads anything.

Prebuilt extensions are committed to the repo — download and install, no building required:

| Browser | Download | Setup guide |
|---------|----------|-------------|
| Firefox / LibreWolf / Zen … | [`fintube-extension-firefox.xpi`](./browser-extensions/fintube-extension-firefox.xpi) | [`browser-extensions/firefox/README.md`](./browser-extensions/firefox/README.md) |
| Chrome / Edge / Brave / Vivaldi … | [`fintube-extension-chrome.zip`](./browser-extensions/fintube-extension-chrome.zip) | [`browser-extensions/chrome/README.md`](./browser-extensions/chrome/README.md) |

See [`browser-extensions/README.md`](./browser-extensions/README.md) for an overview.
