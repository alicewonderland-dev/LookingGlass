# Settings window: in-game checklist

The Settings window's short labels and "?" bubbles (see
[The Settings window](../design.md#the-settings-window)). One character, on
this build, registered and connected. Open `/lg` and click the gear. Do it in
simple mode (**Advanced mode** off), then steps 4 and 6 again in advanced
mode. Do it once at Dalamud's usual font size and once with a bigger one
(Dalamud settings, Look & Feel, Global Font Scale).

## Labels and order

- [ ] 1. The sections are, in order: **Server**, **Chat**, **Local chat**,
      **Chat history**, **Your identity**, **Blocked users**. No dimmed
      paragraphs under the settings.
- [ ] 2. The labels read: **Server address**, **Connect automatically**
      (with **Connect now** or **Disconnect** at the right of the same line),
      **Show messages in**, **Colour the whole line**, **Use nicknames in
      tags**, **Say when I start or stop talking in a channel**, **Show
      LookingGlass only in windows**, and under it, indented, **New channels
      open** with **As a tab** and **In a new window** (greyed out while the
      setting is off); **Colour** and **Privacy notice accepted** (or **not
      accepted yet**) with **Read it** and **Withdraw**; **Keep chat history
      on this computer** and, while it's on, **Size limit**; **Advanced
      mode**, **Reset my identity...**.
- [ ] 3. Each setting does what it did before the redesign: tick and untick
      each box, pick another chat channel, pick a local chat colour, and check
      that it takes effect as before and is kept after reloading the plugin.

## The "?" bubbles

- [ ] 4. A small round **?** follows exactly these: **Server address**,
      **Show messages in**, **Show LookingGlass only in windows**, local
      chat's **Colour**, **Privacy notice accepted**, **Keep chat history on
      this computer**, **Advanced mode** and **Reset my identity...**. No
      other setting has one. Click each: a small bubble opens right beside
      it (to its left if the window is near the right edge of the screen),
      with a few sentences in everyday words, a few lines long and about as
      wide as the Settings window at its usual size. In simple mode, only Advanced
      mode's mentions anything technical (encryption, fingerprints).
- [ ] 5. Hovering a **?** shows no tooltip; it lights up and the pointer
      becomes a hand. A bubble closes on a click anywhere else, on a click on
      its **?** again, and on **Escape**. Escape closes only the bubble: the
      Settings window stays open. Clicking another **?** while one is open
      opens the other one instead.
- [ ] 6. Turn on **Advanced mode**: the **?** of **Keep chat history on this
      computer** now says it's stored encrypted and names how (such as Windows
      DPAPI). The others say the same as in simple mode.
- [ ] 7. Move the Settings window to the bottom right corner of the screen and
      open each **?** near the bottom: every bubble stays on the screen.
- [ ] 8. **Reset my identity...**'s **?** works while the button is greyed
      out (for example while something is in progress).

## Narrow window

- [ ] 9. Make the Settings window as narrow as it goes. **Disconnect** (or
      **Connect now**) moves under **Connect automatically**; **Read it** and
      **Withdraw** move under the privacy line; **In a new window** may move
      under **As a tab**. Nothing runs past the right edge.

## What to send back

For each step, pass or fail; for a failure, a screenshot of the window (with
the bubble open, if it's about one) and what you expected.
