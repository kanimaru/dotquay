# Upgrade every outdated addon, one at a time

Upgrade this project's addons without silently discarding anyone's work, and without letting one
addon's conflict block the rest.

1. Call `dotquay_outdated` to find every addon with a newer version available.
2. Call `dotquay_status` with `explain: true` so you know, before touching anything, which addons are
   `modified` and whether that modification is safe to discard.
3. For each outdated addon, one at a time — never as a batch:
   - Call `dotquay_update` naming that one addon.
   - If it succeeds, move on to the next addon.
   - If it is refused with `needs_confirmation` / `force_confirmation`, that addon has real,
     uncaptured changes. Do not pass `confirm_discard` on your own judgement — show the user what
     `dotquay_status --explain` reported for that addon and let them choose between discarding it
     (retry with `force: true` and `confirm_discard` naming it) or capturing it first with
     `dotquay_patch_capture`.
   - If it fails because a patch reapplication conflicted, stop upgrading that one addon: point the
     user at `dotquay_patch_resolve` for it, and continue with the next addon rather than leaving the
     whole run stuck on one conflict.
4. When every addon has been attempted, call `dotquay_status` once more and summarize what changed,
   what was skipped, and what still needs a decision.

One addon at a time is deliberate: a conflicted update aborts before writing the lockfile, and a batch
that half-applied is much harder to reason about than several separate ones.
