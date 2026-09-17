# Diagnose a modified addon: {{name}}

The addon **{{name}}** in this project shows as `modified` in `dotquay_status`. Work out why, and
recommend exactly one fix — never more than one, and never guess without doing the checks below.

1. Call `dotquay_status` with `explain: true` for this project. Find the entry for `{{name}}` and read
   its explanation: does it list real content changes, or only editor artifacts (`*.uid`, `*.import`,
   files under `.godot/`)? An addon that differs only in artifacts is not a real modification — say so
   and stop; nothing needs fixing.
2. Check whether a patch already exists for `{{name}}` by reading the project's
   `dotquay://{project}/patches/{{name}}` resource. If reading it succeeds, a patch already covers
   part of this addon's differences — take that into account before recommending a fresh capture, and
   if `dotquay_status`'s explanation mentions a conflicted patch resolution, point the user at
   `dotquay_patch_resolve` instead of anything below.
3. Classify the real content changes `dotquay_status --explain` reported:
   - If they look like a deliberate local change worth keeping (a tweak the user made on purpose),
     recommend capturing it: call `dotquay_patch_capture` for `{{name}}`.
   - If the explanation says the change is safe to discard (uid-only rewrites, line endings, or the
     user confirms it was accidental), recommend `dotquay_update` with `force: true` and
     `confirm_discard: ["{{name}}"]` — never pass `force` without `confirm_discard` naming every
     addon `dotquay_status --explain` flagged as not safe to force.
   - If you are unsure which the user wants, show them the explanation from step 1 and ask, rather
     than guessing between capture and discard.
4. State your recommendation as exactly one of: capture as a patch, force-update with
   `confirm_discard`, or leave it alone — and say why, citing what `dotquay_status --explain` reported.
