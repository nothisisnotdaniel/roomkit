# 014 Bound the wait on a host badge source before each push

As a RoomKit host, I want a slow or hanging badge source to not hold up pushes, so that notifications still go out on time.

Category: security

## What

PushSender reads IPushBadgeSource.Current with no timeout, so a badge source that hangs delays every push. Add a bounded wait (for example a short timeout); when it runs out, send the push without a badge. Cover it with a test.

## Why

Raised by review-security in job 479: host-supplied code can hang, and today that stalls every push the server sends.

## Notes

Proposed by job 479-push-badge-api.5bba845adb75430bb6b00dceb3c75d05 (`factory/jobs/479-push-badge-api.5bba845adb75430bb6b00dceb3c75d05/`).
