# Flags and bans: test server checklist

Flagging accounts and addresses that limits refuse again and again, and the
operator's bans (see [Spotting abuse, and banning](../design.md#spotting-abuse-and-banning)
and [Flags and bans](../server.md#flags-and-bans)). Use a test server, never
the public one: these steps flag and ban characters. You need shell access to
the server as its user, and two characters, **A** (yours, on this build of
the plugin) and **B** (on another PC or client, on another internet
connection), in a channel together, here `sky` (`/lgc1`).

On the server, define a shorthand that runs the commands as the service, with
the service's own settings (adjust the paths to the install):

```sh
LG() {
    local settings
    mapfile -t settings < <(systemctl show lookingglass -p Environment --value | tr ' ' '\n' |
        grep -E '^LookingGlass__(DataDirectory|Abuse__|TrustedProxies__|Operators__)')
    sudo -u lookingglass env "${settings[@]}" /opt/lookingglass/LookingGlass.Server "$@"
}
```

For quicker flags, and to see each connection's address, add to the server's
settings (`sudo systemctl edit lookingglass`), and restart:

```ini
[Service]
Environment=LookingGlass__Abuse__FlagAfterMinutesRefused=2
Environment=LookingGlass__Limits__LookupBurst=2
Environment=LookingGlass__Limits__LookupIntervalSeconds=600
Environment=Logging__LogLevel__LookingGlass=Debug
```

Take them out again at the end.

## Starting

- [ ] 1. **The server sees real addresses.** Connect A, then
      `journalctl -u lookingglass --since "5 min ago" | grep "Connection from"`.
      The address is A's public address (as a "what is my IP" site shows it; an
      IPv6 one as its /64), not 127.0.0.1, ::1, the server's own address, or a
      100.x Tailscale address. If it isn't, the proxy isn't passing the client's
      address on (see [Behind a reverse proxy](../server.md#behind-a-reverse-proxy)):
      fix that first, and ban no address until it is.
- [ ] 2. After the restart, the startup line (`journalctl -u lookingglass -n 20`)
      says `flagged when refused by limits in 2 of 60 minutes, or by 4 limits
      within 10 minutes; addresses blocked automatically for 15 minutes after
      1000 refusals`.
- [ ] 3. `LG --bans` prints `No bans in force.`, `Nothing flagged in the last
      24 hours.` and `No bans lifted or ended in the last 90 days.`, and exits
      with 0 (`echo $?`). The server keeps running.
- [ ] 4. `LG --ban` alone prints what it needs and exits with 2;
      `LG --ban 10.0.0.0/8` says a /16 at the widest and exits with 1.
- [ ] 5. `LG --ban 127.0.0.1` and `LG --ban ::1` say it is the server's own or
      its proxy's address, which would shut out every player, ban nothing, and
      exit with 1 (they mention `--force`; don't use it).

## Flags

- [ ] 6. As A, invite three different players by name in a row (they needn't
      be registered): the third is refused ("You've looked up a lot of players
      recently"). Wait for the next minute, and do it again.
- [ ] 7. The journal has one Warning `Flagged user <A's ID>: refused by limits
      in 2 of the last 60 minutes, by LookupBurst. ...`, with no character
      names in it. Doing it again in a third minute adds no second line.
- [ ] 8. `LG --bans` lists `user <A's ID> (A's name@world)` under
      `Flagged in the last 24 hours`, with `limits: LookupBurst`, and A's
      public address (the one from step 1; an IPv6 one as its /64 and /56)
      flagged too. Never 127.0.0.1, ::1, the server's own address or a 100.x
      address: if one of those shows, stop, and fix the proxy (step 1).

## Banning an account

- [ ] 9. `LG --ban "A's name@A's world" --days 1 --reason "Testing bans"`:
      it says `Banned user <ID> (...) until <tomorrow> UTC (1 day)` and that a
      running server applies it within 30 seconds.
- [ ] 10. Within 30 seconds A is disconnected. A's chat shows a warning, once:
      the server's operator has blocked this character until (tomorrow, in
      local time), with the reason "Testing bans", and that "Try again now"
      tries at once. In simple mode it says "The people who run this
      LookingGlass server have blocked this character".
- [ ] 11. A's main window (`/lg`) shows **Blocked by the server** in the
      header, and in the middle **Blocked by this server** with the same words
      and a **Try again now** button. Typing `/lgc1 hello` says LookingGlass
      isn't connected.
- [ ] 12. Leave it for 2 minutes: A doesn't reconnect (the journal shows no new
      `Connection from` A's address), and the warning isn't repeated.
- [ ] 13. B sees A offline in `sky`, and A is still listed as a member. B's
      messages to `sky` go through; nothing tells B that A was banned.
- [ ] 14. Press **Try again now**: A connects, is refused again at once (the
      journal: `User <ID> is banned: refused`), and is back in the blocked
      state with no second warning.
- [ ] 15. Turn A's plugin off and on (or restart the game): still blocked, and
      the warning is shown once again (a new session).

## Lifting it

- [ ] 16. `LG --bans` lists the ban under `Bans in force (1)` with its reason,
      and `made on the command line by <you>` (the login you ran sudo from).
- [ ] 17. `LG --unban "A's name@A's world"`: `Lifted the ban on ...`.
- [ ] 18. On A, press **Try again now** (or wait up to 5 minutes): A connects,
      shows **Connected**, sees `sky`, and B sees A online. A and B chat both
      ways in `sky`.
- [ ] 19. `LG --bans` shows it under `Lifted or ended in the last 90 days`,
      `lifted ... on the command line by <you>`.

## Registering again

- [ ] 20. `LG --ban <A's user ID>` (the number from `--bans`). On A (on this
      test server only), choose **Forget account** in `/lgdebug`, then register
      A's character again through the Lodestone: after **Verify** it is refused
      with the block, and A shows **Blocked by the server**.
- [ ] 21. `LG --unban <A's user ID>`, press **Try again now**, and register
      again: it works, and A has its channels as before.

## Banning an address

- [ ] 22. Find B's public address the way step 1 found A's (the `Connection
      from` line when B connects): not 127.0.0.1, ::1, the server's own address
      or a 100.x Tailscale address, and not A's. Then
      `LG --ban <B's public address> --reason "Testing address bans"` (an IPv6
      address bans its /64). Within 30 seconds B is disconnected and told the
      server's operator blocked their internet address, which others may share,
      with the reason.
- [ ] 23. A (on another address) is unaffected.
- [ ] 24. `LG --unban <the address, exactly as --bans lists it>`; B connects
      again within 5 minutes, or at once with **Try again now**.

## Automatic blocks (on by default; this lowers the threshold to test it)

- [ ] 25. Add `LookingGlass__Abuse__AutoBlockMinutes=5`,
      `LookingGlass__Abuse__AutoBlockAfterRefusals=100` and
      `LookingGlass__Limits__ConnectionsPerMinutePerIp=5`, and restart. From a
      spare machine (not A's or B's connection), open 120 connections in a
      minute:

      ```sh
      for i in $(seq 120); do curl -s --http1.1 --max-time 2 -o /dev/null -w "%{http_code}\n" \
          -H "Connection: Upgrade" -H "Upgrade: websocket" -H "Sec-WebSocket-Version: 13" \
          -H "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==" https://<server>/ws; done | sort | uniq -c
      ```

      The journal says `Blocked address <the spare machine's address> for 5
      minutes automatically`, and `LG --bans` lists it, made automatically.
      Every connection from there is now answered at once, with no
      `Connection from` line. (The 429 itself proves nothing: the connection
      limit answers 429 too, and looks the same. What shows the block is that
      there are no `Connection from` lines for that address, and the
      `LG --bans` listing.) After 5 minutes it is gone from the bans in
      force. Put the settings back.

## The audit trail

- [ ] 26. `LG --audit` lists, newest first, every ban and lift above, each as
      `command line  <you>: banned ...` or `lifted the ban on ...`, and the
      refused attempts with why: `LG --ban 10.0.0.0/8` (step 4) and
      `LG --ban 127.0.0.1` and `::1` (step 5) as `refused to ban ...`. (`LG
      --ban` alone, a wrong command line, isn't there.) `LG --audit --days 1`
      lists the same; `LG --audit --days 0` says what it needs and exits with 2.

## Operator characters (the plugin's panel comes in a later update)

- [ ] 27. `LG --operators` says no characters are listed. Add
      `Environment=LookingGlass__Operators__Characters__0=<A's user ID>` to the
      drop-in and restart: the startup log (`journalctl -u lookingglass -n
      30`) has `Operators: user <A's ID> (A's name@world), keys not pinned yet`,
      and `LG --operators` lists A as `not an operator yet`, with a `Now:`
      fingerprint.
- [ ] 28. On A, open LookingGlass's Settings with Advanced mode on: the
      fingerprint under **Your identity** is the `Now:` one.
- [ ] 29. `LG --operator-pin <A's ID> --fingerprint "00000 00000 00000 00000
      00000"` says the fingerprint isn't A's keys' now, pins nothing, and exits
      with 1. `LG --operator-pin <B's ID> --fingerprint "<anything of 25
      digits>"` says B isn't listed. Then `LG --operator-pin "<A's
      name@world>" --fingerprint "<A's fingerprint from step 28>"`: `Pinned the
      keys of ...`, exit 0; `LG --operators` says A is `an operator`, pinned on
      the command line by you.
- [ ] 30. `LG --ban "<A's name@world>"` says A is listed as an operator, bans
      nothing, and exits with 1 (don't add `--force`).
- [ ] 31. On A (this test server only), **Forget account** in `/lgdebug`, and
      register A again through the Lodestone (it gets new keys). `LG
      --operators` says A is `NOT an operator now: its keys changed at <time>`,
      with `Pinned:` and `Now:` differing, and `Now:` is the new fingerprint in
      A's Settings. Pin it again with the new fingerprint: `Restored operator
      status ...`.
- [ ] 32. Take the `Operators` line out of the drop-in and restart: the log
      warns `Deleted the operator pin of user <A's ID>`, and `LG --operators`
      says no characters are listed. `LG --audit` shows each pin of steps 29
      and 31, refused ones too.

## Discord alerts (a private test channel)

Needs the server with Discord alerts, a Discord server of your own, and a
private channel in it that only you can see. Set it up as in
[Alerts to Discord](../server.md#alerts-to-discord). Times in the messages
show in your own time zone.

- [ ] 33. **Setting up.** Make a webhook for the test channel (**Copy Webhook
      URL**) and a role given only to you (copy its ID). Put the webhook in
      `/etc/lookingglass/alerts.env` (root-only, mode 600) and add
      `EnvironmentFile=/etc/lookingglass/alerts.env` and
      `Environment=LookingGlass__Alerts__PingRoleId=<the role's ID>` to the
      drop-in; restart. The startup line (`journalctl -u lookingglass -n 20`)
      says `Discord alerts on`, and `journalctl -u lookingglass | grep -c
      api/webhooks` prints 0. The channel gets "**Server started:**
      LookingGlass 0.2.x, in Production. It stopped cleanly at (the time of the
      restart)", with no ping. `ls -l /etc/lookingglass/alerts.env` shows
      `-rw------- root root`.
- [ ] 34. **A flag.** As B, do what step 6 did (A's flag from step 7 lasts
      24 hours, and one still in force isn't posted again): within about a
      minute and a half, "**Flagged:** (B's name@world) (user (B's ID)): refused
      by limits in 2 of the last 60 minutes, by LookupBurst.", with B's
      Lodestone link (and no preview card under it), `LG --bans`, the `LG --ban (ID) --days 7 --reason
      "..."` command, and "Nothing was banned". No ping. B's public address
      flag, if posted, shows it shortened (`203.0.113.x`, or an IPv6 /40).
- [ ] 35. **Bans posted by the server, once.** `LG --ban "<B's name@world>"
      --days 1 --reason "Testing alerts @everyone **bold** https://example.com"`.
      Within about a minute and a half (the server picks it up within 30
      seconds; alerts within a minute go as one message): "**Ban made** on the
      command line: (B) (user (ID)), for 1 day, until (tomorrow). Reason, which
      the player is shown:" and the reason shown exactly as typed: not bold,
      `@everyone` not highlighted and nobody pinged, the address not a link.
      Your Unix user isn't in it, and nothing else pings.
- [ ] 36. `LG --ban "<B's name@world>" --days 2`: one message, "**Ban made**
      ... for 2 days ..., replacing the ban made (today) on the command line
      (for 1 day)", and no "Ban lifted" for the old one. Then `LG --unban
      "<B's name@world>"`: "**Ban lifted** on the command line: (B). It had been
      made on the command line, (today), for 2 days."
- [ ] 37. `sudo systemctl restart lookingglass`: "**Server started:** ... It
      stopped cleanly at ...", and none of the ban messages again (wait a
      couple of minutes).
- [ ] 38. **An address.** `LG --ban <B's public address> --days 1`, then
      `LG --unban` it: both messages show the address shortened (`203.0.113.x
      (shortened)`, or an IPv6 /40); B's full address appears nowhere in the
      channel.
- [ ] 39. **Urgent: an automatic block.** With step 25's settings, do step 25
      again: at once (not a minute later), "@(your role) **Urgent: blocked
      automatically:** address (the spare machine's, shortened), refused 100
      times within 60 minutes. Blocked for 5 minutes, until (time)." You get a
      ping notification (without **Allow anyone to @mention this role** on the
      role: webhooks shouldn't need it). Once the block ends, do it again
      within ten minutes of the first: it is posted with the minute's message
      (about a minute later), without the ping. Put the settings back.
- [ ] 40. **Urgent: operator keys.** With A listed in `Operators:Characters`
      (step 27): `LG --operator-pin` A (step 29) → within 30 seconds "@(role)
      **Operator keys pinned** on the command line at (the time): (A) (user (ID)), pinned to
      keys (A's fingerprint)", with a ping. On A, **Forget account** in
      `/lgdebug` and register again (as step 31): when A signs in, "@(role)
      **Urgent: an operator character signed in with other keys:** (A) ...,
      keys (the new fingerprint, as in A's Settings), not the pinned (the old
      one)". Reconnect A (`/lgdebug`, or turn the plugin off and on): no second
      message. Pin the new keys (step 31): "@(role) **Operator status
      restored** ... (was (the old fingerprint))".
- [ ] 41. **A crash.** `sudo systemctl kill -s KILL lookingglass` (systemd
      starts it again): "**Server started:** ... It didn't stop cleanly: it had
      been running since (the last start) ...".
- [ ] 42. **The daily summary.** Add
      `Environment=LookingGlass__Alerts__DailySummaryHourUtc=<the next hour,
      in UTC>` to the drop-in and restart. Nothing until that hour; then, within
      a minute, "**Daily summary**, the 24 hours to (that hour)" with the
      flags, blocks and bans of the steps above, bans in force, the most
      players online at once (counted since the restart), characters
      registered for the first time, uptime, the database's size, and "Alerts
      not delivered: none". Restart before the next hour: no second summary.
      Take the line out again.
- [ ] 43. **When posting fails.** In `alerts.env`, change one letter of the
      webhook's token and restart: the server runs, no message arrives, and the
      journal has one `Couldn't post an alert to Discord: Discord answered 401
      ...` (or 404) line saying the webhook may have been deleted, without the
      webhook in it; a flag or ban within the next ten minutes adds no second
      line. Then change `https://` to `http://` and restart: the service doesn't
      start, and the journal says `LookingGlass:Alerts:DiscordWebhookUrl isn't a
      Discord webhook ... it doesn't start with https://`, without the value.
- [ ] 44. **Replacing a leaked webhook.** In Discord, delete the webhook and
      make a new one; put the new one in `alerts.env` and restart: "**Server
      started:**" arrives through it. Posting with the old one fails (`curl -X
      POST -H "Content-Type: application/json" -d '{"content":"x"}' <old
      webhook>` answers 404).

## The operator web page (Tailscale only)

Needs the server with the operator web page (on Linux, with the unit that has
`RuntimeDirectory=lookingglass`: the installer brings it), Tailscale 1.94 or
later on the server, a phone signed in to the tailnet as your own account, and
for step 49 a second Tailscale account (a friend's, shared the machine, or a
second login of yours). Set it up as in
[The operator web page](../server.md#the-operator-web-page). Keep Discord
alerts on from the steps above, and A listed in `Operators:Characters`.

- [ ] 45. **Setting up.** `systemctl cat lookingglass | grep Runtime` shows
      `RuntimeDirectory=lookingglass` and `RuntimeDirectoryMode=0700`. Add the
      page's lines to the drop-in (`Admin__Listen`, `Admin__Url`,
      `Admin__TailscaleLogins__0` with your login as the Tailscale admin
      console's **Users** page shows it, `Admin__TimeZone` with your zone) and
      restart. The startup line says `operator web page at
      https://<machine>.<tailnet>.ts.net:8444/ (Unix socket; 1 login let in;
      times in <your zone>)`, and the journal has `The operator web page
      listens on unix:/run/lookingglass/admin.sock`. `sudo ls -la
      /run/lookingglass` shows the folder as `drwx------ lookingglass` and the
      socket as `srw------- lookingglass lookingglass admin.sock`.
- [ ] 46. **Serving it on the tailnet.** `tailscale version` is 1.94 or later.
      Check the syntax on this version with `tailscale serve --help`, then
      `sudo tailscale serve --bg --https=8444 unix:/run/lookingglass/admin.sock`.
      `tailscale serve status` shows `:8444` as tailnet only, and `tailscale
      funnel status` lists only the public server's port (443). Note any
      difference in the commands for server.md.
- [ ] 47. **On your phone** (on the tailnet, as the listed account), open
      `https://<machine>.<tailnet>.ts.net:8444/`: the page, **Signed in:** your
      login, in one column with big buttons, light or dark as the phone is set.
      **Lifted or ended** and **What operators did** are folded and open with
      a tap; **Refresh** reloads. Times end in your zone's abbreviation (`BST`,
      say) and the last line says `Times are <your zone> (<abbreviation> now)`.
- [ ] 48. **On a computer** on the tailnet: the same page, wider, the lists in
      rows; in a recent Chrome, Edge or Safari the folded sections show open.
- [ ] 49. **Another account.** From a device signed in to the tailnet as the
      second account: only "**Not allowed.** This page is only for this server's
      operators, ..." and nothing else. The journal has one `Refused a request
      to the operator web page (GET "/" from 100.x.y.z): its Tailscale login,
      "<their login>", isn't listed ...`; reloading within the minute adds no
      second line.
- [ ] 50. **Not from the server itself.** On the server, as an ordinary user
      (not root, not `lookingglass`): `curl -s
      https://<machine>.<tailnet>.ts.net:8444/` answers "Not allowed.", and the
      journal says `it came from this machine itself (100.x.y.z)` (Tailscale
      may name such a request by your own login, the reason it is refused
      whatever its login). Wait a minute, then `sudo curl -s --unix-socket
      /run/lookingglass/admin.sock http://x/`: "Not allowed." too (`it has no
      X-Forwarded-For address`). Optionally tag the server's node (see
      server.md) and check steps 47 and 50 again.
- [ ] 50a. **When the page can't start, chat goes on.** Take a port with
      another program (`nc -l 127.0.0.1 5181 &`, or any listener), change the
      drop-in's `Admin__Listen` to `http://127.0.0.1:5181` for the moment, and
      restart: the service runs, A connects and chats, the journal has an
      Error `The operator web page didn't start (...); the chat server runs on
      without it`, and Discord gets "@(role) **Urgent: the operator web page
      didn't start:** ...". Put `Admin__Listen` back, stop the listener
      (`kill %1`), and restart: the page is back.
- [ ] 51. **Outside the tailnet.** From a device not on the tailnet (a phone on
      mobile data with Tailscale off, say): `curl -v --max-time 10
      https://<machine>.<tailnet>.ts.net:8444/` gets no answer (the name doesn't
      resolve, or the connection times out). The public server still answers
      there: `curl -s https://<public address>/health`.
- [ ] 52. **Not on the public server.** `curl -s -o /dev/null -w "%{http_code}\n"
      https://<public address>/` and the same for `/ban`, `/lift?ban=1` and
      `/style.css`, with and without `-H "Tailscale-User-Login: <your login>"
      -H "X-Forwarded-Host: <machine>.<tailnet>.ts.net:8444"`: always 404.
- [ ] 53. **A ban.** On the page, **Ban someone**: B's name@world, 1 day,
      reason "Testing the page", **Continue...**: a confirmation page ("Ban
      (B)?", the character with its Lodestone ID and link, until (tomorrow, in
      your zone with its abbreviation), the reason, what a ban does). Nothing
      is banned yet (`LG --bans`). **Ban (B)**: back on the page with "Banned
      (B) for 1 day." at the top; B is disconnected at once (not 30 seconds
      later). `LG --bans` says `made on the web page by <your login>`; Discord
      gets "**Ban made** on the web page: ..." with "Lift:
      https://<machine>...:8444/ or `LG --unban ...`", and your Tailscale
      login nowhere in it.
- [ ] 54. **Lifting it.** **Lift...** on B's ban: "Lift the ban on (B)? Made
      (time, your zone) on the web page by (you), ...". **Lift the ban**:
      "Lifted the ban on (B)."; B gets back in within 5 minutes (or at once
      with **Try again now**). `LG --audit` lists both as `web page  <your
      login> from 100.x.y.z: ...`.
- [ ] 55. **Refused by the rules.** **Ban someone** `127.0.0.1`: back on the
      form, filled in as typed, saying it is the server's own address, that
      nothing was banned, and to tick the box if meant (don't). `LG --audit`
      doesn't list it (only a confirmed ban is an attempt to change
      something).
- [ ] 56. **Forms work once, and not after a restart.** Ban B again from the
      page; after it is done, go **Back** (if the browser asks to send the form
      again, let it) and press the confirmation's button again, if it is shown:
      "That form was sent already (each works once)." Nothing more is banned,
      and `LG --audit` has one ban, not two. Open **Lift...** on that ban, `sudo systemctl
      restart lookingglass`, then press **Lift the ban**: "This page is out of
      date (the server restarted). Check and confirm again." The ban is still
      in force; lift it from a fresh page.
- [ ] 57. **Flags, in full.** With B's flag from step 34 still in force (or
      flag B again as step 6 did), **Flagged** lists B and B's address in full
      (not shortened), with **Ban...**, which fills the form in with 7 days and
      bans nothing by itself.
- [ ] 58. **Restoring operator status.** Pin A on the command line (step 29),
      then on A **Forget account** in `/lgdebug` and register again (step 31).
      Refresh the page: **Operators** is open by itself, "· 1 to restore", A
      `NOT an operator now` with the pinned and current fingerprints.
      **Restore operator status...**: both fingerprints, when the keys
      changed, and the advice to compare; check `Keys now` against A's
      Settings (Advanced mode, **Your identity**), then **Restore operator
      status**: "(A) is an operator again, with keys (the new fingerprint).";
      `LG --operators` says pinned on the web page by you; Discord "@(role)
      **Operator status restored** on the web page ..." at once, with the ping.
- [ ] 59. **Send a test alert**: "Sent a test alert to the Discord channel." at
      the top, and "**Test alert** from LookingGlass 0.2.x: alerts reach this
      channel." in Discord, without a ping.
- [ ] 60. **Funnel never leads to the page** (optional, test server only, and
      turn it off straight after). `sudo tailscale funnel --bg --https=10000
      unix:/run/lookingglass/admin.sock`, then from outside the tailnet open
      `https://<machine>.<tailnet>.ts.net:10000/`: "Not allowed." (even with
      your own phone off the tailnet), and Discord gets at once "@(role)
      **Urgent: the web page was asked for through Tailscale Funnel** ...".
      Reload a few times: no second alert within ten minutes. Then `sudo
      tailscale funnel --https=10000 off`, and `tailscale funnel status` lists
      only 443 again. (This checks that Tailscale marks Funnel requests, which
      the page relies on.)

## Afterwards

- [ ] 61. Remove the test settings (the drop-in from the start, step 25's, and
      the alerts lines, then the test webhook in Discord) and restart. `LG --bans` shows no bans in force. Flags clear by
      themselves after 24 hours. Keep the page's lines and `tailscale serve`
      if you want the page on the test server; otherwise take them out and
      `sudo tailscale serve --https=8444 off`.
