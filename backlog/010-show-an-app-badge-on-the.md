# 010 Show an app badge on the home-screen icon from a push payload

As someone who runs a RoomKit app from the iPhone home screen, I want the app icon to show a number when jobs need me, so that I see it without opening the notification center.

Category: feature

## What

In wwwroot/sw.js, read an optional integer field `badge` from the push payload (0 to 999; anything else is ignored). When it is above 0, call `self.registration.setAppBadge`/`navigator.setAppBadge(badge)`; when it is 0, clear the badge. Do this before or alongside `showNotification`, and fail safely (no error, notification still shown) where the Badging API is missing. Add a small page-side helper in RoomKit (for example in counts.js or a new badge.js) that a host app can call with a number to set or clear the badge while the app is open. Document the `badge` payload field and the helper in docs/roomkit.md, and update the payload comment in sw.js. Cover both with tests in the existing style.

## Why

iOS 16.4+ shows badges on home-screen web apps, which gives a glanceable signal for blocked jobs. iOS has no silent pushes, so the service worker must set the badge when a push arrives, and the page must update it when the app is opened. Both live in RoomKit, so this should ship in 0.2.0 before ControlRoom uses it.

## Notes

The existing `count` field already means the new-item count for approval pushes, so the badge needs its own field. The payload stays free of anything but these fixed fields.
