# WoW Dual Box Assist

A small Windows helper for dual boxing on live World of Warcraft without breaking the rules.
It gives you one dedicated **swap key** (a key or a mouse button) that jumps between your two
game windows, and nothing else. Unlike Alt-Tab, it ignores YouTube, Discord and every other
window, so you always land in a game.

## Staying inside the rules

Blizzard allows multiboxing but bans input broadcasting: one key press must cause one action in
one game. This tool is built around that:

* It **never sends keys, clicks or any other input to a game**. It only changes which window is
  in front, the same thing clicking a taskbar button does.
* Every action in a game comes from a key you press yourself, in that game.
* It never opens the game process, not even read-only, and does not touch game memory, files or
  network traffic. It only reads window titles and the system process list, like the taskbar and
  Task Manager.

Things to stay away from, with this tool or any other:

* Anything where one press acts in a game you didn't press it in: "swap, cast, swap back"
  macros, key broadcasting, round-robin key senders.
* Swapping on a timer or automatically, or anything else that plays without you.

Blizzard has the final say on enforcement, so no tool can promise you won't be banned. What this
one does is no different from Alt-Tab.

(Earlier versions of this repo were a key broadcaster for private servers. That code is gone; it
is still in git history.)

## Using it

1. Start both WoW clients in **borderless windowed** mode. They show up under **Games** on their
   own. The one found first is the **Main**, the next one the **Alt**. Use **Flash** to see
   which window is which, and **Swap main/alt** or the pickers to change them.

   The app only looks for games (once a second) until it has both. After that nothing runs on a
   timer: it just checks that its two windows still exist when focus changes or you press the
   swap key, and starts looking again if one was closed. **Refresh now** does a full look on demand.
2. Click **Set key…**, then press the key or mouse button you want to swap with. Hold Ctrl, Alt
   or Shift with it for a combo. Esc cancels.
3. Press it in either game to jump to the other one.

The swap key never reaches the game, so pick something you don't use in WoW. Good choices are a
mouse side button, `F13`-`F24` if your keyboard or mouse software has them, or a combo like
`Ctrl + ``. If you pick a plain typing key such as `` ` `` or `Q`, the app warns you that it will
stop typing that character, in chat too.

### Window layouts

Pick a **Layout** under Games. The main always fills the main monitor; the alt goes where the
layout says:

* **Both on the main monitor** (default): both fill it, on top of each other.
* **Alt fills the left / right monitor.**
* **Alt in a quarter of the left / right monitor**, in the bottom corner nearest the main
  monitor, so it is easy to click.
* **Don't move the windows.**

Left and right are worked out from where the monitors are, not Windows' monitor numbers. Only
layouts your connected monitors allow are listed. The layout is applied when you pick it, when you
click **Apply**, when you change main/alt, and a few seconds after a game shows up. It does what
you would do by hand: a full-monitor spot is reached by moving the window to that monitor and
maximizing it, a quarter by un-maximizing and resizing. A window that is already maximized and
covering the right monitor is not touched. No input ever reaches the game, but Windows always
brings a window to the front when it is maximized.

**Going from a quarter back to full:** WoW keeps the quarter's resolution and doesn't return to
*Windowed (Fullscreen)* on its own. Make this macro in the alt and press it after switching back
(the app reminds you):

```
/console gxMaximize 1
/console gxRestart
```

It sets *Windowed (Fullscreen)* and makes WoW re-apply its display settings, the same as toggling
Display Mode in the settings. It's your own key press in that game, using WoW's own commands.

**Also works from other windows** (on by default): pressing the swap key while YouTube or anything
else is in front brings back the game you were in last. Turn it off if you want the key to keep its
normal job outside the games, e.g. the browser back button on mouse button 4.

### Clients running as administrator

Windows hides your key presses in an elevated window from normal programs and won't let them bring
it to the front. If the swap key does nothing inside the games, or the app says Windows would not
switch, your launcher may be running WoW elevated. After a failed swap the app shows a **Restart as
administrator** button; use it.

Settings live in `%AppData%\WowDualBoxAssist\settings.json`. Only one copy of the app runs at a
time, because two copies would both swap on the same press and cancel each other out.

## How it works

* Global low-level keyboard and mouse hooks (`WH_KEYBOARD_LL`, `WH_MOUSE_LL`) watch for the swap
  key and swallow it. The mouse hook is only installed while a mouse button is the swap key.
* A `WinEvent` foreground hook remembers which game you were in last.
* Switching is `SetForegroundWindow`. When Windows refuses it to a background program, the app
  briefly attaches to the foreground window's input queue (`AttachThreadInput`) and tries again.
  That is still a focus change; no input is generated. The status line counts how often this
  fallback was needed (also saved as `FallbackCount` in the settings file), to decide whether it can go.

## Requirements

* Windows 10/11, x64.
* .NET 10 Desktop Runtime (already present if the .NET 10 SDK is installed).

## Build and run

```
dotnet build WowDualBoxAssist.slnx -c Release
src\WowDualBoxAssist\bin\Release\net10.0-windows\WowDualBoxAssist.exe
```

To install it with a desktop shortcut (run again after code changes, with the app closed):

```
.\install.ps1
```

This publishes to `%LocalAppData%\Programs\WowDualBoxAssist` and creates
**WoW Dual Box Assist** on your desktop.

For a single exe you can copy anywhere that has the runtime:

```
dotnet publish src\WowDualBoxAssist -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

## Tests

```
tests\run-tests.ps1          # logic tests, no real input, safe while you work
tests\run-tests.ps1 -Place   # also moves two fake game windows through every layout and back
tests\run-tests.ps1 -Focus   # also swaps between two fake game windows for real (steals focus ~0.5s)
```

The focus test also checks that the fake games received no key or mouse messages at all.
