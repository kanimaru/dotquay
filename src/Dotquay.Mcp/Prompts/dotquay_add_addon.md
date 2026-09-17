# Add a new addon: {{git_url}}

Add the git addon at `{{git_url}}` the way a careful person would at the console: preview first, get
the user's informed yes, then add.

1. Call `dotquay_add_preview` with `git: "{{git_url}}"` (plus any `version`/`branch`/`rev`/`path` the
   user already specified). Do not call `dotquay_add` first — the preview is how you find out what a
   plain add would do before anything is written.
2. Read the preview's result: the transitive addon graph it would pull in, any NuGet packages, and —
   the important part — the feeds it lists as needing approval, i.e. any NuGet feeds this addon would
   ask the project to trust that it does not already trust.
3. Show the user the graph and, if that list is non-empty, the feeds needing approval and any
   source-generator packages involved. Get an explicit yes before writing anything — this is the one
   step no tool call can skip for you.
4. Once the user agrees, call `dotquay_add` with the same arguments as the preview, plus
   `approved_source_urls` listing every feed the user approved in step 3 (and `keep_generator_private`
   if the user asked for that). If the user only approved some of the feeds, do not add — go back and
   ask about the rest, or drop the addon.
5. Call `dotquay_doctor` afterward and report anything it flags for the newly-added addon (a folder
   casing mismatch, a fragile uid reference) so the user isn't surprised by it later.
