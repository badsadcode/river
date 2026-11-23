# The dev log

A single static page, `index.html` + `style.css` + `images/`, published to https://badsadcode.github.io/river/ by
`.github/workflows/devlog.yml` on every push that touches `devlog/`. No build step. `.gdignore` keeps Godot out of here.

## Voice (Tom's brief)

* An explorer's journal from the 1890s, kept by the expedition's crew. **No technical talk**: no shaders, solvers,
  LODs, segments, frames per second. Say what a traveller on the river would notice.
* The crew: Mr. Tom (leader, designer), Miss Noor (co-designer), Mr. Sage (engineer, keeps the journal). Light humour.
* Only what's really in the game. Small jokes in the margin notes are fine; invented events are not.

## Adding an entry

1. Copy the last `<article class="entry">` block in `index.html`, give it a new `id`, the next ordinal
   ("Entry the Tenth") and the date, and add a line to the *Index of Entries* (move the "— latest" marker).
2. Pictures: game captures (`tools/capture.sh` or `--shots=…`), saved as JPG, 1280 px wide, quality ~82, into
   `images/`. One plate: `<figure class="plate">`, add `r` to tilt the other way; several side by side:
   wrap them in `<div class="plates">`. Number plates on from the last one (Roman numerals).
3. Update *The Road Ahead* if the plans changed.
