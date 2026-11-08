## Family Time Guard – Phase 2: phone remote

**Download `FamilyTimeGuard.zip`, extract it, and run `Install.cmd` again on each computer (settings are kept).
Then follow `PHONE-GUIDE.txt` to connect your phone.**

New in Phase 2 (Telegram, one private bot per computer, only you can control it):
- `/status`, `/today`, `/rules` – time left, what was played and watched, current limits.
- `/bonus games 30`, `/bonus youtube 15` – extra time today.
- `/set school games 60`, `/bedtime school 21:30`, `/wakeup weekend 07:30` – change the rules from anywhere.
- `/pause 2h`, `/resume`, `/lock`, `/lock 1h`, `/unlock`.
- Kids can tap "Ask for more time" on the clock icon; you get Approve/No buttons.
- Alerts (time used up, bedtime log-offs, night-time sign-ins, helper stopped, wrong PIN) and an evening summary.

Also fixed:
- YouTube is no longer blocked for a parent while a child's account is only signed in in the background ("Switch user").
- Upgrading no longer fails when a child is signed in: the installer waits for the old tray helper to close, and if it
  still can't replace the program it starts the previous version again instead of leaving the computer unprotected.

From Phase 1:
- Bedtime lock, daily game and YouTube time, school-day/weekend rules, warnings, parent PIN, activity log.
