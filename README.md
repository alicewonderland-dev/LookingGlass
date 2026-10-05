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
- **Unread counts.** The channel list counts new messages, and the window's
  title shows the total.
- **Who's online.** Each member's icon is green while they're connected.
- **Blocking.** Hide someone's messages and silently decline their invites.
- **Privacy.** Only the members of a channel can read its name and messages.
  The server still sees who is in which channel and when messages are sent.

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
server info bar; click that to stop. If a message can't be sent, you're told,
and it doesn't go to game chat either.

With ChatTwo, every ChatTwo chat box and pop-out sends to the channel too,
whatever channel it shows, and so do short commands with a message (`/p hi`).
To say something in a game channel just once, use the long command
(`/party hi`, `/say hi`), or switch channel first. ChatTwo shows the tag in its
input, with "(Warning: ...)" naming the game's channel underneath; your
messages still go to the LookingGlass channel, except from a ChatTwo tab or
pop-out set to a tell, which ChatTwo sends itself. Turn ExtraChat off while you
do this: it watches the same chat box.

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

1. **Set the server address.** Type `/lg`, then click the gear in the window's
   title bar to open Settings. Under **Server URL**, enter the address the
   server's operator gave you (it looks like `wss://chat.example.com/ws`) and
   press **Apply**. The plugin connects whenever you log in.
2. **Register your character.** The main window walks you through it. Press
   **Get a code**, paste the code (it starts with `LGC-`) anywhere in your
   Lodestone character profile, save the profile, then press **Verify**. The
   Lodestone can take a minute to show changes. Once you're registered, you can
   delete the code from your profile. You do this once per character and
   server.
3. **Create or join a channel.** Press **Create a channel** and name it, or
   accept an invite from the envelope at the top right of the main window. To
   invite someone, select the channel and press **Invite**.

## Using channels

Select a channel in the main window to see its members and settings.

- **Numbers.** Each channel gets a number automatically and keeps it. Click the
  `/lgcN` tag under the channel's name to pick another; if another channel has
  that number, the two swap.
- **Nicknames.** Click **+ nickname** next to the number. A nickname is 1 to 16
  letters, digits, `-` or `_`, and can't be only digits. Upper and lower case
  count as the same. Nicknames stay on your computer.
- **Colours.** Choose **Colour...** in the channel's menu (the ⋮ button), or
  click the coloured dot before its name. In Settings you can choose whether
  the whole line or only the tag takes the colour, and whether tags show
  nicknames.
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
