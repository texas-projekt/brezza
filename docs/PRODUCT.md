# Product Definition

## Purpose

Brezza is a lightweight Windows browser that makes search the fastest path into the web. It keeps the browser surface focused on one search/address field, tabs, and the current page.

## Primary Workflows

1. Enter a URL or search query and navigate in the current or a new tab.
2. Switch between open tabs without losing their page state.
3. Close the browser and restore the normal browsing session on the next launch.
4. Open a private tab whose browsing data is removed when the last private tab closes.
5. Open the new-tab, settings, history, or bookmarks pages as browser-owned internal pages.

## Product Principles

- Search is an explicit user action. Keystrokes are not sent to a search provider by default.
- Browser chrome is local HTML/CSS/JS inside a trusted WebView2 surface; remote content runs in a separate WebView2 profile without a host message bridge.
- Local browser data stays on the device unless the user explicitly navigates to a remote destination.
- Private sessions are isolated from normal history and session restoration.
- Browser chrome follows the Windows UI language by default and supports English and Simplified Chinese.
- Features earn their place through a user workflow and measurable value.

## Initial Scope

The first usable release covers URL/search input, navigation, tabs, local history, bookmarks, normal session restore, and isolated private tabs. Extensions, synchronization, accounts, telemetry, built-in ad filtering, and a custom rendering engine are out of scope until separately justified.
