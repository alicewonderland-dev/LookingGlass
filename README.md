# LookingGlass

LookingGlass is a Dalamud plugin for Final Fantasy XIV that adds extra
linkshells, called channels, that work across worlds and data centres. You
talk in them from the game's chat box, and their messages appear in your
normal chat log. Channel names and messages are encrypted on your computer,
so the server that passes them along can't read them.

It is a fresh plugin inspired by ExtraChat, and uses its own servers. It is in
early testing (version 0.2).

## Features

- **Channels across worlds.** Be in up to 50 channels, each with up to 500
  members from any world or data centre.
- **Chat commands.** `/lgc1` to `/lgc50` send to a channel by its number, and
  `/lgc <nickname>` by a nickname you choose.
- **Invites and ranks.** Invite people by character name and home world. Each
  channel has an admin, and can have moderators who help with invites and
  removals.
- **Nicknames and colours.** Give a channel a short nickname and one of the
  game's chat colours. Chat lines are tagged with the nickname, as in `[sky]`,
  or the number, as in `[LGC3]`.
- **Name colours.** Give someone's name a colour of its own, to tell people
  apart at a glance in a busy channel. It shows in every channel, and only you
  see it.
- **Unread counts.** The channel list counts new messages, and the window's
  title shows the total.
- **Channel windows.** Right-click a channel in the list to chat in a window
  of its own, with tabs for more channels (the **+**). What you type there only
  ever goes to that channel, never to game chat, and you can choose which
  channels also show in game chat (the channel's ⋮ menu).
- **Nothing missed.** Messages sent while you were logged out or disconnected
  (for up to a week) show up when you're back, marked with the time they were
  sent.
- **Chat history (optional).** Turn on **Keep a chat history on this
  computer** in Settings (in advanced mode, **Keep a chat log on this
  computer**), and channel windows show older messages when you scroll up,
  even after you log out. It stays on your computer, scrambled so only you
  can read it there, is never uploaded, and you can delete it any time.
- **Who's online.** Each member's icon is green while they're connected.
- **Blocking.** Hide someone's messages and silently decline their invites.
- **Privacy.** Only the members of a channel can read its name and messages.
  The server still sees who is in which channel and when messages are sent,
  and keeps the encrypted messages for a week so members who were away get
  them.

## Commands

| Command | What it does |
| --- | --- |
| `/lookingglass` or `/lg` | Open the main window: register, create and manage channels and invites |
| `/lgc1 <message>` … `/lgc50 <message>` | Send to the channel with that number |
| `/lgc <nickname> <message>` | Send to the channel with that nickname |
| `/lgc3` or `/lgc <nickname>` (no message) | Talk in that channel: what you type in chat goes there, not to game chat, until you switch back with `/s` (or any chat channel) |
| `/lgdebug` | Open the debug window (connection details, for reporting problems) |

`/lgc` on its own explains how to use it. Only `/lgc` is listed in Dalamud's
command help (`/xlhelp`), to keep the list short. While you talk in a channel,
its tag (like `[sky]`) shows where the chat box names its channel and in the
server info bar; click that to stop. Starting and stopping aren't also said in
chat unless you turn on **Verbose channel messages** in Settings (off by
default), but a stop you didn't choose, such as a disconnect, always is. If a
message can't be sent, you're told, and it doesn't go to game chat either.
Item, map flag and status links work as in normal chat, and so do text
commands such as `<t>` and `<me>`, which are sent as the names they stand for.

`/p hi`, `/s hi` and the like still talk in that game channel once, as usual
(a macro's `/p` line too), and tells (`/t`, `/r`) still go as tells. To go
back, type `/s` (or any channel) on its own. With ChatTwo, every ChatTwo chat
box not set to a tell sends to the channel too, and so does the short command
of ChatTwo's own channel (`/p hi` while ChatTwo is on Party): use `/party hi`
for that one (in a ChatTwo pop-out with its own input box, use the long form
for every channel). Its "(Warning: ...)" only names ChatTwo's channel
underneath.
Turn ExtraChat off while you do this.

## Installing

LookingGlass isn't in Dalamud's plugin installer or a custom plugin repository
yet. While it's in testing, it is loaded as a dev plugin:

1. Get a build of `LookingGlass.dll` (see
   [Loading a development build of the plugin](docs/server.md#loading-a-development-build-of-the-plugin)).
2. In Dalamud's settings, open **Experimental**, add the full path to
   `LookingGlass.dll` under **Dev Plugin Locations**, and save.
3. Turn LookingGlass on in the plugin installer, where it is listed with your
   dev plugins.

## Getting started

1. **The server.** LookingGlass connects to its own server whenever you log
   in; there's nothing to set. To use another server, type `/lg`, click the
   gear in the window's title bar to open Settings, enter its address under
   **Server URL** (it looks like `wss://chat.example.com/ws`) and press
   **Apply**. If you used the test server at `alicedev`, switch to the new
   address the same way: the plugin offers to keep your identity, and your
   channels come with you.
2. **Register your character.** The main window walks you through it. Press
   **Get a code**, paste the code (it starts with `LGC-`) anywhere in your
   Lodestone character profile, save the profile, then press **Verify**. The
   Lodestone can take a minute to show changes. Once you're registered, you can
   delete the code from your profile. You do this once per character and
   server.
3. **Create or join a channel.** Press **Create a channel** and name it, or
   accept an invite from the envelope at the top right of the main window. To
   invite someone, select the channel and press **Invite**, or right-click
   them (their name in chat or ChatTwo, the party list, your target or your
   friend list) and choose **Invite to LookingGlass**.

## Using channels

Select a channel in the main window to see its members and settings.

- **Numbers.** Each channel gets a number automatically and keeps it. Click the
  `/lgcN` tag under the channel's name to pick another; if another channel has
  that number, the two swap.
- **Nicknames.** Click **+ nickname** next to the number. A nickname is 1 to 16
  letters, digits, `-` or `_`, and can't be only digits. Upper and lower case
  count as the same. Nicknames stay on your computer.
- **Colours.** Choose **Colour...** in the channel's menu (the ⋮ button), or
  click the coloured dot before its name. **Custom...** there lets you pick
  any colour on a wheel or type its code, like `#3FA7D6`. In Settings you can
  choose whether the whole line or only the tag takes the colour, and whether
  tags show nicknames.
- **Name colours.** Right-click a name in the member list, or a message in a
  channel window, and choose **Name colour...**. That name then shows in the
  colour in every channel: in chat, in channel windows and in member lists.
  **Default** puts it back. Your own name works too.
- **Ranks.** Moderators can invite people and remove members below them. The
  admin can also rename the channel, make or unmake moderators, hand over the
  admin role and disband the channel. The admin can't leave while others
  remain: hand over the admin role, or disband the channel, first.
- **Removing someone.** Hold **Ctrl** while choosing **Remove from channel** in
  their menu.
- **Checking it's really them.** A warning sign before a member's name means
  they set up LookingGlass again (new computer or reset), or someone else may
  be using their name. Ask them over /tell, then click it and press **It's
  really them**.
- **Advanced mode.** Turn it on in Settings, under "Your identity", to see the
  encryption details: fingerprints you can compare over /tell to be sure your
  chats are private. Warnings show either way.

## Playing on a new computer, or lost your settings

LookingGlass keeps your keys protected by Windows on the computer they were
made on, so they don't come with you to a new one. Just register again through
the Lodestone, as the main window shows you. Every channel you were in comes
back, with your rank, and so do your open invites.

The other members are told that you set up LookingGlass again. A channel
starts working again as soon as another member who is online lets you back in,
which happens automatically. If nobody else in a channel can, it comes back as
"Restored channel", and its admin can rename it.

Your old computer can't sign in any more after that.

## Reset my identity

Use **Reset my identity...** (Settings, under "Your identity") only if you
think someone may have copied your LookingGlass files, or your keys were lost.
It shuts out your old keys on that server, then you register again through the
Lodestone and get your channels back as on a new computer. It doesn't affect
other servers.

## Troubleshooting

- **No messages in chat.** Messages go to the game chat channel chosen under
  "Show messages in the chat channel" in Settings. Make sure your chat tab shows
  that channel, or choose another.
- **"This server doesn't recognise your login".** Check the server address in
  Settings first. The plugin tries again every minute or so, and **Retry now**
  tries at once. Register again only if your keys were lost or the server never
  knew you; your channels come along either way.
- **"This server doesn't accept the address you use".** Use one of the
  addresses the plugin lists instead.
- **A server sent "a registration code that doesn't belong to it".** Don't put
  that code in your profile, and don't register with that server.
- **A channel shows "From your old setup"** ("Your old key's place" in advanced
  mode). It's left over from an older version and can't be used. Choose
  **Remove from my list...** in its menu.
- **The server moved to a new address.** Change it in Settings. If both
  addresses belong to the same server, the plugin offers to keep your identity;
  otherwise you register again there.

## More

- [How it works](docs/design.md): the design, encryption and security model.
- [Running a server](docs/server.md): setting up, configuring and deploying a
  server, and building the plugin.
- License: [GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0-only).
  If you run a modified version of the server for other people, you must offer
  them its source code (section 13).
