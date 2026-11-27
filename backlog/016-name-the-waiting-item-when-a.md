# 016 Name the waiting item when a drop is refused

As the backlog owner, I want the refused-drop message to name the item that waits on the one I tried to drop, so that I know which line to change first.

Category: ux

## What

When BacklogEditor.Drop or the backlog change endpoint refuses a drop because another open line waits for the item (after: #N), include the waiting item number(s) in the error and show them in the edit.js notice, instead of the fixed text that only says to change or drop that item first.

## Why

Job 493 review-ux: the fixed waited_on text tells the user another item waits but not which one, so they have to search backlog.md by hand to find it.

## Notes

Proposed by job 493-drop-approved-items (review-ux nit 1).
