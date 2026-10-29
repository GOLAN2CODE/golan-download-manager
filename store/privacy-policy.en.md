# Privacy Policy — Golan Download Manager Browser Extension

**Effective Date:** October 18, 2026

## 1. Overview
The **Golan Download Manager** browser extension ("the Extension") is a local companion component for the Golan Download Manager desktop application on Windows. We are committed to complete user privacy: **all data processed by the Extension stays strictly on your local device.**

## 2. No Remote Servers, Analytics, or Telemetry
- The Extension does **not** transmit any data to external servers, cloud services, or third parties.
- The Extension contains **no** analytics, tracking pixels, telemetry, crash reporters, or advertising SDKs.
- The Extension makes **zero** outbound network requests (`fetch` / `XMLHttpRequest`) to any remote server.

## 3. Data Processed Locally
When you initiate a download, click an extension context-menu action, or choose to download a detected media stream, the Extension sends a one-shot message to the locally installed **Golan Download Manager** desktop application via the browser's Native Messaging API (`com.golan.gdm`). This local message contains only the information needed by the desktop application to perform the download on your behalf:
- **Download / Media URL and Referrer URL**: To fetch the requested file or video stream.
- **File Name, MIME Type, and Size**: To pre-fill the download dialog and select the appropriate category folder.
- **User-Agent and Site Cookies for the Target URL**: Necessary when downloading files from authenticated sessions (such as private repositories or logged-in portals) so the local desktop downloader can authenticate the same request. Cookies are encrypted on disk by the local Golan desktop application using Windows DPAPI (`CurrentUser` scope), are sent only to the exact originating host without HTTPS-to-HTTP downgrade, and are cleared immediately upon download completion.

## 4. Local Browser Storage
The Extension uses `chrome.storage.local` and `chrome.storage.session` solely on your device to cache:
- Whether download interception is currently enabled or paused by the user.
- The list of file extensions and site exceptions configured in the local Golan desktop application.
- Temporary per-tab detected media URLs (cleared automatically when the tab navigates or closes).

## 5. User Control
You can disable download capture at any time from the Extension popup, add site exceptions in the Golan desktop application, or uninstall the Extension to remove all cached local state immediately.
