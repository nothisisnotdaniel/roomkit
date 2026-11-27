# 012 Let a host set the app badge on the pushes the server sends

As the ControlRoom owner, I want RoomKit to let a host put a badge count on its server pushes, so that the iPhone app icon updates even while the app is closed.

Category: feature

## What

Add a public way for a host to set the badge field on the pushes RoomKit sends: for example an optional Badge on PushEvent, or a host callback that FactoryPushSource and PushSender call to get the current count. The internal PushPayload gains the badge field, which sw.js already reads. Cover it with tests and document it in CLAUDE.md, then release it in the next minor version.

## Why

ControlRoom job 476 found that RoomKit 0.2.0 sw.js reads a push badge field, but no public C# API can set it. So ControlRoom can only update the badge while the app is open, and a job that blocks while the app is closed never shows on the icon.
