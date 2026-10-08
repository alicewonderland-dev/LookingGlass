# Flags and bans: test server checklist

Flagging accounts and addresses that limits refuse again and again, and the
operator's bans (see [Spotting abuse, and banning](../design.md#spotting-abuse-and-banning)
and [Flags and bans](../server.md#flags-and-bans)). Use a test server, never
the public one: these steps flag and ban characters. You need shell access to
the server as its user, and two characters, **A** (yours, on this build of
the plugin) and **B** (on another PC or client), in a channel together, here
`sky` (`/lgc1`).

On the server, set a shorthand (adjust the paths to the install):

```sh
LG="sudo -u lookingglass env LookingGlass__DataDirectory=/var/lib/lookingglass /opt/lookingglass/LookingGlass.Server"
```

For quicker flags, add to the server's settings, and restart:
`LookingGlass__Abuse__FlagAfterMinutesRefused=2` and
`LookingGlass__Limits__LookupBurst=2`,
`LookingGlass__Limits__LookupIntervalSeconds=600`. Take them out again at the
end.

## Starting

- [ ] 1. After the restart, the startup line (`journalctl -u lookingglass -n 20`)
      says `flagged when refused by limits in 2 of 60 minutes, or by 4 limits
      within 10 minutes; automatic blocks off`.
- [ ] 2. `$LG --bans` prints `No bans in force.`, `Nothing flagged in the last
      24 hours.` and `No bans lifted or ended in the last 90 days.`, and exits
      with 0 (`echo $?`). The server keeps running.
- [ ] 3. `$LG --ban` alone prints what it needs and exits with 2;
      `$LG --ban 10.0.0.0/8` says a /16 at the widest and exits with 1.

## Flags

- [ ] 4. As A, invite three different players by name in a row (they needn't
      be registered): the third is refused ("You've looked up a lot of players
      recently"). Wait for the next minute, and do it again.
- [ ] 5. The journal has one Warning `Flagged user <A's ID>: refused by limits
      in 2 of the last 60 minutes, by LookupBurst. ...`, with no character
      names in it. Doing it again in a third minute adds no second line.
- [ ] 6. `$LG --bans` lists `user <A's ID> (A's name@world)` under
      `Flagged in the last 24 hours`, with `limits: LookupBurst`, and the
      address A connects from (or its /64 and /56) flagged too.

## Banning an account

- [ ] 7. `$LG --ban "A's name@A's world" --days 1 --reason "Testing bans"`:
      it says `Banned user <ID> (...) until <tomorrow> UTC (1 day)` and that a
      running server applies it within 30 seconds.
- [ ] 8. Within 30 seconds A is disconnected. A's chat shows a warning, once:
      the server's operator has blocked this character until (tomorrow, in
      local time), with the reason "Testing bans". In simple mode it says "The
      people who run this LookingGlass server have blocked this character".
- [ ] 9. A's main window (`/lg`) shows **Blocked by the server** in the
      header, and in the middle **Blocked by this server** with the same words
      and a **Try again now** button. Typing `/lgc1 hello` says LookingGlass
      isn't connected.
- [ ] 10. Leave it for 2 minutes: A doesn't reconnect (the journal shows no new
      connection from A), and the warning isn't repeated.
- [ ] 11. B sees A offline in `sky`, and A is still listed as a member. B's
      messages to `sky` go through; nothing tells B that A was banned.
- [ ] 12. Press **Try again now**: A connects, is refused again at once (the
      journal: `User <ID> is banned: refused`), and is back in the blocked
      state with no second warning.
- [ ] 13. Turn A's plugin off and on (or restart the game): still blocked, and
      the warning is shown once again (a new session).

## Lifting it

- [ ] 14. `$LG --bans` lists the ban under `Bans in force (1)` with its reason.
- [ ] 15. `$LG --unban "A's name@A's world"`: `Lifted the ban on ...`.
- [ ] 16. On A, press **Try again now** (or wait up to 5 minutes): A connects,
      shows **Connected**, sees `sky`, and B sees A online. A and B chat both
      ways in `sky`.
- [ ] 17. `$LG --bans` shows it under `Lifted or ended in the last 90 days`.

## Registering again

- [ ] 18. `$LG --ban <A's user ID>` (the number from `--bans`). On A (on this
      test server only), choose **Forget account** in `/lgdebug`, then register
      A's character again through the Lodestone: after **Verify** it is refused
      with the block, and A shows **Blocked by the server**.
- [ ] 19. `$LG --unban <A's user ID>`, press **Try again now**, and register
      again: it works, and A has its channels as before.

## Banning an address

- [ ] 20. `$LG --ban <B's public address> --reason "Testing address bans"` (an
      IPv6 address bans its /64). Within 30 seconds B is disconnected and told
      the server's operator blocked their internet address, which others may
      share, with the reason.
- [ ] 21. A (on another address) is unaffected.
- [ ] 22. `$LG --unban <the address, exactly as --bans lists it>`; B connects
      again within 5 minutes, or at once with **Try again now**.

## Automatic blocks (optional)

- [ ] 23. Set `LookingGlass__Abuse__AutoBlockMinutes=5`,
      `LookingGlass__Abuse__AutoBlockAfterRefusals=100` and
      `LookingGlass__Limits__ConnectionsPerMinutePerIp=5`, and restart. From a
      spare machine, open 120 connections in a minute (`for i in $(seq 120);
      do curl -s --http1.1 --max-time 2 -o /dev/null -H "Connection: Upgrade"
      -H "Upgrade: websocket" -H "Sec-WebSocket-Version: 13" -H
      "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==" https://<server>/ws; done`). The journal says `Blocked address ... for 5
      minutes automatically`, and `$LG --bans` lists it, made automatically.
      After 5 minutes it is gone from the bans in force. Put the settings back.

## Afterwards

- [ ] 24. Remove the test settings and restart. `$LG --bans` shows no bans in
      force. Flags clear by themselves after 24 hours.
