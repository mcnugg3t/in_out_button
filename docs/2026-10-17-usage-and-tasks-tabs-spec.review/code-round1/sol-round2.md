# Sol 6.1 diff review, round 2

Same thread as round 1, reasoning effort low, 43 s, on `git diff d192281` for
the code files plus one pass over `web/usage.html`. Both findings fixed before
the post-review commit.

1. P1: the 10 s drain bound logged and continued, so a save slower than 10 s
   could still race git. Fixed: the drain returns false and every workflow
   aborts with a status line instead of running.
2. P2: the default-clone test asserted that `~\Tools\persistent-memory` exists,
   so it failed on a clean profile. Fixed: the test asserts whichever branch the
   machine makes reachable.

Cleared: quoted-key and opaque scrubbing with the prefix cap, store
serialization and drain coverage, the cross-instance lock, the pending
quick-add, task and document extras, the path-source banners, the card
guards, the stale gate, capped and cached discovery, and the usage page's safe
rendering, unknown-message handling, and 60 s stale limit; 63 tests passed.
