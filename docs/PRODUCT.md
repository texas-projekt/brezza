# Product Definition

## Purpose

Brezza is a lightweight Windows browser that makes search the fastest path into the web. It keeps the browser surface focused on one search/address field, tabs, and the current page.

## Primary Workflows

1. Enter a URL or search query and navigate in the current or a new tab.
2. Switch between open tabs without losing their page state.
3. Close the browser and, when the previous-session startup option is on, continue the normal browsing session on the next launch.
4. Open a private tab whose browsing data is removed when the last private tab closes.
5. Open the settings, history, bookmarks, cookies, or browsing-data panels as browser-owned toolbar views.
6. Download files and open supported popup destinations as tabs, with visible operation results. A page may run at most three downloads at once, and a download whose size is known before it starts may not exceed 8 GiB; a refused download is reported without leaving a partial file.
7. Manage cookies and browsing data from the toolbar: the cookies panel lists every cookie with its domain, expiry, and flags and deletes them one by one or all at once; the browsing-data panel lists the data categories and clears one or all of them. Both ask before anything is cleared and report the outcome.
8. Manage downloads from the toolbar: the downloads panel lists every download with where it came from, how it ended, and when; a completed file is opened from its row, its folder from the row's own button, an entry is removed without asking, and the whole list is cleared behind a confirmation that leaves the files in place.
9. Watch and control a running download: the panel shows what has arrived against the total, a running download is paused and resumed from its own row, and cancelling one asks first and drops the part that was already downloaded.

## Product Principles

- Search is an explicit user action. Keystrokes are not sent to a search provider by default.
- A signed-in Windows user has one browser window. Launching Brezza again restores and activates that window instead of creating another profile tree.
- Browser chrome is native Win32: real Windows controls for the window, tab strip, address/search field, navigation buttons, notices, and the settings/history/bookmark/cookie/browsing-data/downloads panels. WebView2 renders website content in a separate surface; remote content runs without a host message bridge.
- Local browser data stays on the device unless the user explicitly navigates to a remote destination.
- Private sessions are isolated from normal history and session restoration.
- Startup opens the new-tab page by default; opening the pages from the previous session is a stored setting the user turns on.
- Private cleanup reports pending or failed removal and retries after private tabs close.
- Clearing cookies or browsing data is an explicit, confirmed user action that reports its outcome; a single cookie or entry is removed without asking. The tools act on the normal profile only, because private-session data is already removed when the last private tab closes.
- The download list is local browser data: finished downloads are recorded on the device and survive a restart, while a running or paused download is not written while it is still live. If its tab or the browser closes, the resulting interruption is recorded as an interrupted entry. A private download's record is listed only while its private session is open and is never written to disk; the downloaded file itself is written to the normal Downloads folder and remains there like any other file. Managing the list never touches the downloaded files: clearing it removes records, and the files stay where they were written. Cancelling a download is the one action that destroys bytes, so it asks first.
- The downloads folder is the user's choice, set in settings; until it is changed, downloads go to the user's own Downloads folder.
- Browser-owned views are toolbar views, not pages: no browser view has a URL, so nothing a site can link to, bookmark, or reload renders browser chrome.
- Website permission requests are denied with a notice until a browser-owned permission prompt is implemented.
- Browser chrome follows the Windows UI language by default and supports English, Simplified Chinese (`zh-Hans`), and Traditional Chinese (`zh-Hant`). Traditional Windows cultures such as `zh-TW` and `zh-HK` select the Traditional pack; a stored language value is normalized to a script tag.
- Browser chrome follows the Windows app theme by default and lets the user override it with Light or Dark; the choice is stored in `%LOCALAPPDATA%\Brezza`.
- Features earn their place through a user workflow and measurable value.

## Initial Scope

The first usable release covers URL/search input, navigation, tabs, local history, bookmarks, opt-in normal session restore, isolated private tabs, downloads with a managed list, visible progress, and pause, resume, and cancel, popup-to-tab handling, native theme selection, a configurable downloads folder, English/Simplified/Traditional Chinese chrome, and toolbar tools that manage cookies, browsing data, and downloads. Permission approval UI is not implemented. Extensions, synchronization, accounts, telemetry, built-in ad filtering, and a custom rendering engine are out of scope until separately justified.
