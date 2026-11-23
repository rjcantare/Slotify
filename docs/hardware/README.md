# Hardware build notes

Engineering notes from building and bench-testing the coin box. For a
step-by-step build, start with [../OWNER-GUIDE.md](../OWNER-GUIDE.md), section 2.

| File | What it covers |
|------|----------------|
| [production-wiring.md](production-wiring.md) | **Current** in-box perfboard layout, parts list, LM2596 setup, validation tests |
| [esp32-wiring.md](esp32-wiring.md) | Original breadboard design, pin choices, why each part exists |
| [coin-acceptor-spec.md](coin-acceptor-spec.md) | Allan 1239A settings, firmware timing constants, anti-phantom defense layers |
| [perfboard-build-task.md](perfboard-build-task.md) | Row/column perfboard walkthrough |
| [denz-subvendo-board.md](denz-subvendo-board.md) | **Untested:** using Denz Customized's ready-made sub vendo board instead of building one |

Notes:
- Where these files disagree, **`production-wiring.md` is the current design**.
  Older sections of `coin-acceptor-spec.md` describe GPIO 23 floating
  (high-Z) as the "accept" state; that was replaced by the R8 pull-down plus
  GPIO 23 driven HIGH during a claim.
- References to `C:\Projects\...` and session codes (S9, S11, ...) are from
  the original development log. Paths in this repo are `windows/`,
  `android/` and `firmware/`.
- Photos: [../images/](../images/).
