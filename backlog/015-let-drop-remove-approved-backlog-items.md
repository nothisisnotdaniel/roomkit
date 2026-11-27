# 015 Let Drop remove approved backlog items too

As the backlog owner, I want to drop an approved item as well as a proposal, so that I can remove a story I no longer want without editing backlog.md by hand.

Category: feature

## What

Widen BacklogEditor.Drop and the drop action of the backlog change endpoint from `- [?]` lines to `- [?]` and `- [ ]` lines; `- [x]`, `- [h]`, the stop line and prose stay refused. The line and its story file go in one commit, as for a proposal today. In edit.js show the Drop button on open items too, with its own confirm text for an approved item ("Drop this approved item? Its line and story file are removed in one commit."). Refuse the drop, with a clear message, when another open line waits for the item (`after: #N`), so no line is left waiting on a missing story. Tests for each allowed and refused mark and for the after: refusal.

## Why

Today only proposals can be dropped from ControlRoom; an approved story that is no longer wanted needs a hand edit of backlog.md and a git rm, which is exactly what the app is meant to save. Without the after: check a drop would leave other items waiting forever.
