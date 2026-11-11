# Persocord: the full guide

A fast, dark, lightweight screen-clip recorder for Windows, made for getting
clips into Discord in seconds. Record an area, trim it, share it.

**To install, see [INSTALL.md](../INSTALL.md).**

**First start (and once for everyone updating to 1.5):** a short setup walks you through the important parts (and can start from a friend's settings code): where clips go and how much space they may take, your replay (on or off, length, resolution, frame rate, picture and format, with its size and video memory shown), sound (PC audio and mic, with a live mic level and AI noise suppression), your keys, a quick look (ready-made looks, accent, solid or glass), and finally a real test: press your replay key and see it save (it also warns if a key is taken by another program, such as NVIDIA's overlay, which uses Alt+F10 too). It always shows your current settings, and only what you change there changes. Every page can be skipped; **Re-setup** (Settings → General or About) runs it again. When it's done, a note points out that Persocord keeps running in the tray.

Vibecoded by dodorider + Claude.

---

## Recording

- **Record an area:** press **Ctrl+Shift+P** (or click the taskbar icon) and drag over what you want.
  - **Space** reuses the last area, **Tab** opens your clips, **Esc** cancels.
  - A size pill shows the real pixel size and common shapes (16:9, 9:16, 1:1…).
- **Record GIF** is on the clip list's top bar. The small arrow next to it picks the GIF's width (360 to 800 px, or the area's own size) and frame rate (10 to 30 fps) with one click, and sets its longest length.
- **The control bar** floats beside the area while recording:
  - **Pause / resume** with no gap in the result.
  - **PC audio** and **Mic** on/off at any time. The mic only opens while it's on.
  - **Restart**, **Settings**, **Cancel**, **Done**, and **Upload** (save and get a link), as icons with a tooltip each.
- **The recording frame** sits just outside the area, so it never appears in a clip. By default it's a crisp 1px white line edged in a hairline of shadow, with slim corner marks that pulse slowly (customizable).
- **Click rings** show where you clicked (optional).
- **Quality:** by default the same as the replay's, so there's one place to set both. Switch "Same quality as the replay" off in Settings → Recording to give area recordings their own format, encoder, resolution, frame rate and quality, with a size estimate.
- **Defaults:** 2-minute limit, everything adjustable in Settings → Recording.

### Hotkeys (change them in Settings → Hotkeys)

| Action | Default |
|---|---|
| Select an area and record | Ctrl+Shift+P |
| Save replay | Alt+F10 |
| Pause / resume, finish, cancel, replay on/off | None at first (the control bar does them); set any in Settings → Hotkeys |
| Turn replay on/off | (not set) |

To change one, click the box and press the new combination.

### Speed and quality

- **Capture:** runs on the graphics card, straight into its video encoder (NVIDIA, AMD or Intel), so games keep their frame rate.
- **Sound:** recorded separately and kept in sync, including across pauses.
- **Own sounds:** Persocord's cue sounds are kept out of your recordings.

---

## Replay (the last few seconds)

Persocord keeps recording the whole screen into a short loop in the background.
Something happened? Press **Alt+F10**, and the last 30 seconds become a clip.

- **Always ready:** it's on from the moment Persocord starts. The **Replay on/off** button in the clip list's top bar (also in the tray, Settings, or an optional hotkey) stops or starts it, and your choice is remembered after restarts.
- **Light on the PC:** it records on the graphics card at low priority, into two-second pieces that overwrite themselves. It uses a fixed bit of disk and almost no memory.
- **Instant to save:** the sound and notification come the moment you press the hotkey. The main sound is mixed while recording, so saving is a plain copy, a fraction of a second even for long replays.
- **Laptops with two graphics chips:** when the screen is wired to the built-in chip (Intel or AMD) and the encoder is the separate card's, the replay uses the screen's own chip to encode (on "Automatic"), or copies the frames across to the chosen card. Screens on a second graphics card are captured from that card.
- **Keeps going through full-screen games:** when a game takes the screen over (going full screen, or tabbing back in), Windows cuts screen capture off for a moment. Only the picture restarts: the sound keeps recording untouched, so a save still reaches back past that moment with every track, and the picture just holds for that second.
- **Lighter on the CPU** (Settings → Replay, also offered in the setup): for 6-core PCs and below, or if games stutter. The replay keeps PC audio and the mic but not a track per app, encodes sound with FFmpeg's quicker method, and its recorders let games go first. Off (the default), nothing is held back.
- **Change the length any time:** a longer or shorter replay length applies straight away and keeps what's already recorded, in every video format. Other replay settings restart it, and a save then holds only what came after. The notification always says how long the saved clip really is.
- **Its own format and encoder** (Settings → Replay → Quality), separate from area recordings':
  - **H.264** plays everywhere; **HEVC** is about 30% smaller; **AV1** is smallest (about 40% smaller than H.264) and plays in Discord on desktop and in browsers.
  - **NVIDIA NVENC** barely touches games, and **AMD AMF** does the same on AMD graphics. **CPU** is sharpest per MB but heavy while gaming. Encoders your PC can't use are greyed out.
- **Size by quality or by bitrate:** a quality level (size varies) or a fixed bitrate in Mbps (size predictable); the one not in use is greyed out.
- **Video memory estimate:** Quality shows about how much video memory your choices take, next to your limit and the card's size, plus the same for every resolution so you can compare. It warns when a choice would go over the limit. While the replay runs it shows the real figure, and the estimate learns from it to fit your PC.
- **Sound sync:** moves the sound earlier (or, below 0, later) by up to 10 seconds; 80 ms by default, since sound reaches Persocord a moment after it plays. Picture and sound are recorded by two separate FFmpegs, lined up by the real time each started, so the video is never slowed by the sound and the offset never changes with the clip length or how long the replay has run. You hear the saved sound and get a notification; the clip list doesn't pop up over your game.
- **Its own sound:** the "Replay saved" sound can be changed or switched off in Settings → Sounds.
- **Tagged and named:** saved replays get a **#replay** tag. When a game is detected, they're named after it and go into its folder if you have one.
- **Adjustable** in Settings → Replay: length (15 s to 10 min), screen, resolution, frame rate, quality or a fixed bitrate, cursor, and which sound tracks to keep. It shows how much disk it will use.

### Game detection

Persocord recognises games three ways:
- **Windows' own list** of games you've played.
- **Discord's public game list**, refreshed weekly.
- **A full-screen check**, as a fallback.

It looks only every few seconds, so it costs essentially nothing, and it never touches the game itself.

## Sound tracks

Recordings and replays keep sound as **separate tracks**:

- **All PC audio:** everything the PC plays. It's always complete, so nothing is ever missing.
- **A track per app playing sound:** a game, Discord, Brave, Spotify and so on. Up to your chosen number (1–10, default 4, Settings → Audio); apps beyond that share one **Other apps** track. Tracks are named after the apps, and only apps actually making sound get one.
- **Microphone.**

Players and Discord hear "All PC audio + microphone". In the editor, untick any app or the mic to leave it out. The sound is then rebuilt from the separate tracks, so system sounds not tied to an app drop out.

**Cost:** listening to an app is nearly free. Each track costs its sound encoding, about 1% of one CPU core while recording, and a few MB of memory.

**Browsers:** each browser plays every tab through one process, so one browser is one track. Discord in the app and YouTube in Brave are separate; two Chrome tabs aren't.

**Apps running as administrator** (some games, and mod launchers such as DD2gether that start the game for you): Windows won't let a normal app hear them on their own. When one plays sound, Persocord asks Windows once per session to start a small helper with administrator approval. The helper is Persocord itself in a special mode, with no window and no files. It records only the apps Persocord names, sends the sound straight back over a private connection, and closes with Persocord. The app then gets its own track like any other. Switch this off in Settings → Audio → "Own tracks for apps running as administrator".

**Unknown track:** if a sound still isn't on any track of its own (you said no to Windows, the helper is off, or the app hides from Windows), the editor works it out. It takes All PC audio and subtracts every other track (each app, Other apps, and the mic in case your headset plays it back). Each is lined up to the exact sample, followed through any hiccups, and matched in loudness and tone band by band, so equalisers and "sound effects" on your output don't leave copies behind. What's left becomes an **Unknown (not separated)** track that you can mute, cut and set the volume of like any other. It only appears when there's real sound left over, not just faint traces of the other apps. It's worked out once per clip and kept for next time.

---

## The clip list

- **Cards** show a thumbnail, length, size, date and tags.
- **Main buttons:**
  - **Copy:** copies the file, ready for Ctrl+V in Discord.
  - **Upload ▾:** makes a share link.
  - **Edit:** opens the editor.
- **Compress:** makes a copy that fits any size you pick (see below).
- **⋯ menu** (or right-click): Play, Share as link, Compress Size, Keep, Private, Rename, Tags, Move to, Convert to MP4, Show in folder, Delete.
- **Drag** a clip straight into Discord to attach it.
- **Search** covers titles, apps, notes and tags, across all folders.
- **Filters:** All, Kept, With link, Private, Edited, Imported, plus one per tag.
- **Sort:** newest, oldest, largest, longest, name.
- **Join:** select two or more clips (Ctrl/Shift-click), then **Join**.

### Editor

Open a clip with **Edit**. Video tracks (V2, V1) sit above audio tracks (A1, A2…, one per recorded sound source). Your edit is kept with the clip, so opening it again carries on where you left off.

The layout: **media** on the left (Added: pictures, videos and sounds brought into this edit; My clips: your other clips, with search), the **preview** in the middle, **Properties** on the right (the selected clip's position, size, turn, opacity, crop, volume and fades; with nothing selected, the edit's speed, volume and output crop), and the **timeline** below. Drag the lines between them to resize. Tools (Select, Cut, Snap, Ripple), undo, Import and Reset are at the top left, saving at the top right. It follows your look, glass included.

**Bringing things in:** paste a picture or files with **Ctrl+V**, drop files anywhere (on the timeline they land where you drop them), or use **Import**. They're copied into Persocord, so the edit keeps working if the originals move. A picture goes on the highest video track with room (a new one on top if needed) and can be any length: drag its edges.

**Pictures on the preview:** click one to select it, drag to move (it snaps to the centre and edges; Alt for no snapping), drag a corner to resize, the round handle to turn it (Shift: 15° steps). Double-click to crop it: drag its edges in. Properties has the same as numbers, plus quick layouts (Fill, a small picture in any corner, left or right half). What you see is exactly what's saved.

**Text:** **Text** (or T) adds text at the playhead: type it in Properties and pick the font, size, colour, outline, shadow and a background box. Move, resize and turn it on the preview like a picture.

**Frame:** with nothing selected, Properties sets the frame's shape (the clip's own, 16:9, 9:16 for Shorts and TikTok, 1:1, 4:5) and what fills the space around clips that don't cover it (black, or a blurred copy of the clip). **Fit** and **Fill** in a clip's Properties size it to fit inside or cover the frame; **Fill the frame with every clip** does that for all of them.

**Speed and effects:** each clip has its own speed in Properties (sound keeps its pitch when saved; what comes after moves along). Right-click a video clip for **Punch in here** (cuts it and zooms the next two seconds in 35%; drag on the preview to zoom on a spot), **Freeze frame here** (holds the picture for two seconds, everything after moves along) and **Crossfade from the clip before**. Video clips show pictures along them; sound clips show their waveform and a volume line to drag up or down.

**Markers and silences:** M drops a marker at the playhead (Ctrl+← / Ctrl+→ jump between them; clips snap to them). **Silences** finds where every sound you hear is quiet, shows how much that is with sliders for how quiet and how long, and cuts those parts out of every track together.

**Saving presets:** beside the save buttons: Original quality, Discord 10 MB or 50 MB (shrunk only if needed), Vertical Shorts (a 9:16 frame for that save), or Separate audio tracks. Only what's between the I / O marks is saved, when they're set.

**The timeline:** click anywhere that isn't a clip to move the playhead (drag to scrub). Drag clips to move them, including onto other clips: the one underneath is covered there (cut back, or split around it). Drag onto the strip above the video tracks for a new track. Drag edges to trim and a clip's top corners inward to fade (over another clip, it crossfades). Sound clips show their waveform. Each track's header has its switches: shown/heard, solo (sound), lock and delete, plus the volume for sound tracks (click to type, drag to change). Drag a header up or down to reorder, double-click it to rename. Right-click anything (a clip, an empty spot, a header, the ruler) for what can be done there, including **Clean up tracks** (removes empty and silent tracks).

| Key | Does |
|---|---|
| V / C | Select tool / Cut tool (C again goes back to Select) |
| N | Snapping on or off |
| I / O | Mark what gets saved (Esc clears) |
| Delete | Remove, leaving a gap (with **Ripple** on, closing it); select a gap and press Delete to close it |
| Shift + Delete | The other one |
| Ctrl + V / Ctrl + D | Paste / duplicate |
| T | Add text at the playhead |
| M, Ctrl + ← / → | Add or remove a marker, jump between markers |
| Ctrl + Z / Ctrl + Shift + Z | Undo / Redo |
| Ctrl or Alt + scroll, or + / − | Zoom the timeline (never further out than the whole edit) |
| \\ or **Fit** | The whole timeline in view |
| Ctrl + G / Ctrl + Shift + G | Group / ungroup the selected clips |
| Space, ← → | Play, step |
| ? | Every key |

The preview is live: every edit shows instantly. **Render exact** plays a precise render of what will be saved, until your next edit.

Dragging the playhead or a clip past either edge scrolls the timeline gently, faster the further past you go, and stops at the start and end.

**Saving is instant for trims and cuts**, like LosslessCut: the picture is copied, not re-encoded, so a 5-minute clip saves in a second or two with exactly the original's quality and size. Each part starts on the keyframe at or just before your cut (recordings have one every 2 seconds), so a part can begin up to 2 s early. Tick **Exact cuts** beside the save buttons for frame-exact cuts; those re-encode, which takes longer. Switching tracks off or changing a track's volume stays instant too: only the sound is redone. Pictures, anything moved, resized or cropped, clip volumes, speed changes, fades and layers always re-encode. While saving, background work pauses so the save gets the whole PC, and **Cancel** stops it at once. If another program has the original open when you **Replace original**, the edit is kept as a copy beside it instead of being lost. **Reset** undoes every change (with a confirmation), and undo history is kept with the clip.

---

## Editor

Click **Edit** on any video clip; everything it does is under **The clip list → Editor** above.

---

## Sharing

### As a file
- **Copy** after recording, then Ctrl+V in Discord.
- **Compress Size** makes a copy that fits exactly the size you want: pick a quick size (10, 25, 50, 100, 200, 500 MB, editable in Settings → Sharing) or type any number, like 205 or 315. It runs on your graphics card (NVIDIA, AMD or Intel), keeps the original by default, and lowers the resolution only when the size is very tight.

### As a link
**Upload** makes a link and copies it. **▾** lets you choose how long it lasts and where it's hosted.

| Host | How long | Largest | Notes |
|---|---|---|---|
| Uguu | 3 hours | 128 MB | Fastest, most private |
| Litterbox | 1h / 12h / 24h / 3 days | 1 GB | Plays in Discord via x266.mov (logs links 30 days) |
| Oshi | 1h – 30 days | ~1 GB | Links can be killed early; blocked in some countries |
| Catbox | Long-term* | 200 MB | **Very slow**; removed if unopened for 2 years |

- **Hosts:** if one fails, the next suitable one is tried automatically.
- **Big clips:** Discord only plays linked videos under about 80 MB inline. Bigger clips can be shrunk for the link, sent full size through x266.mov, or sent as a plain link.
- **Private clips** can never be uploaded.
- **Links are public** to anyone who has them. Don't upload anything sensitive.

---

## Customization

In **Settings → Customization**. Every change shows straight away across the whole app; **Save changes** keeps it, **Cancel** puts the saved look back. Everything is a switch or a slider, so anything that doesn't suit you (or your PC) can be turned off. Each section shows the main choices; the very specific ones sit under its **Advanced** fold.

The clip list, Settings, the editor and the setup always use Persocord's own title bar (icon, name, minimise, maximise and close) in the window's own colours, solid or glass.

- **Colours:** dark or **light mode**. Dark backgrounds: Graphite, OLED black, Slate, Midnight, Forest, Plum, or **Custom** (any colour). Light: Paper, Snow, Sand, or **Custom**. A custom colour makes the whole palette (panels, menus, lines), and text turns light or dark to read on it. Accent-coloured text (links, labels, tags) stays readable whatever the accent: a dark accent on a dark background is lightened just enough for text (Advanced: exactly the accent, or your own colour). An accent (12 presets or any colour), and optionally main buttons that fade into a second colour.
- **Glass and see-through:** one switch puts Windows 11 glass on every window, in a style (Liquid: milky frosted glass; Smoky; Tinted with your accent; Clear) and a strength, plus **See-through** (0–100%): 0% is solid (or your glass as it is); higher shows what's behind through a blur, a deep frosted one up to 35%, a light one from 40%, with less colour over it as it goes, so at 100% you clearly see what's behind with just a soft blur (works from Windows 10, April 2018). **✨ Liquid glass** sets it all up in one click: milky frosted glass that runs right up to the top edge under Persocord's own title bar (with its own minimise, maximise and close buttons), bright cut-glass edges and a highlight on cards, an optional fine grain (Advanced), a faint prism sheen, and text that picks dark or light by itself to stay readable. Under Advanced: each window on its own (glass, material, tint, strength, see-through, background), how solid panels stay, each effect on or off, and text colour (automatic, light, dark or your own). The clip list's see-through goes back to 0% while a clip plays. Glass needs Windows 11 22H2 or later.
- **Background:** plain, a two-colour gradient (any angle), or your own picture (fill, fit, stretch or tile, dimmed and blurred to taste), plus an optional accent glow in a corner. One background for all windows; each window can switch it off. How solid panels and cards stay over glass or a picture is adjustable.
- **Text and shape:** any installed font, text size, UI scale (80–150%), corners (sharp, subtle, rounded, round), spacing (compact, normal, comfy), glass buttons with a soft shine, outlined buttons, notification size.
- **Clip list:** filled, outlined, glass or flat cards; thumbnail size (small to huge); show or hide the length, size, date, folder and tags; cards that rise under the pointer; an accent stripe on the selected clip.
- **While recording:** the frame (clean, brushed metal or solid; tint, thickness, corner marks, heartbeat), click ring colour, the control bar (its colour and the Done button's, words next to the icons, each button on or off, how solid, size, shadow, progress line) and the countdown (a ring that runs down, a big number or a small pill; size; colour; tint). The picker, control bar, frame and countdown share the player's clean look: thin white lines, dark chips and icons.
- **Area picker:** highlight colour, how dark the rest of the screen goes (lightly, 15%, by default), crosshair lines, size readout, key hints, rule-of-thirds lines.
- **Player and editor:** the player's controls (minimal, or a glass bar), the seek bar's colour (white, your accent kept light enough to read, or your own) and thickness, the soft shade along the top and bottom (how dark, or off) and how long the controls stay up after a key; the editor timeline's background, video, sound and playhead colours.

**The look is separate from your settings.** A look code can only ever change the look: it's plain data (never run), read only into the look's own known values, each checked (colours must be colours, choices must exist, numbers in range), with its size capped; settings, folders and files are out of its reach. It lives in its own file, with its own **look code** (starts with `LOOK1-`), save/load file and reset. Sharing a look never changes anyone's settings, and sharing settings (About → Your settings) never changes a look. Your background picture stays on your PC; a friend picks their own. The colours you'd chosen before 1.4 carry over automatically.

---

## Notifications

Persocord shows its own notifications: a small dark card that glides in and fades away. They never take focus from a game, and a click dismisses one. In **Settings → General** you can:
- choose where they appear (any corner, or top or bottom centre),
- choose what clicking one about a clip does: show it in the clip list (selected and briefly highlighted), open it in the editor, play it, or just close,
- set how long each shows at most (for example 1 second),
- switch back to Windows' own notifications, and
- try a test notification.

Their size (75% to 150%) is part of the look, under Customization → Text and shape. Their sound is on the Sounds page (off by default).

---

## Storage

- **Where clips go:** `Videos\Persocord` on your PC by default (never OneDrive). Change it in Settings → Library; you're asked whether your clips and folders move with it.
- **Size limit:** a reminder only. Near it (90%) the gauge turns red, and past it you get a notice. **Nothing is ever deleted automatically** and recording never stops because of it.
- **Deleting:** deleted clips go to the Recycle Bin (can be switched off). If a file is briefly in use, Persocord keeps trying and deletes it as soon as it's free; it never reappears in the list meanwhile.

---

## Settings at a glance

Pages keep their main switches in view; detailed options sit in fold-out sections (click the ▸ heading to open one).

| Page | What's there |
|---|---|
| General | Start with Windows, notifications |
| Customization | Light/dark, colours, glass and see-through per window, backgrounds, font and size, corners, spacing, clip cards, recording overlays, player and editor colours, look codes |
| Library | Clips folder, folders and auto-sort, size limit, Recycle Bin |
| Sharing | Copy to clipboard (recordings and GIFs, and replays, separately), Compress Size quick sizes and options |
| Links | Default link length and hosts |
| Sounds | Cue sounds (including replay and notifications) and volume |
| Audio | PC audio, microphone, devices, levels, app tracks; Advanced: track limit, sound bitrate, rescan devices |
| Recording | What opens on launch, Quality (same as the replay, or its own: format, encoder, resolution, frame rate, quality or bitrate), cursor, click rings, control bar, outline, countdown, remembered area, clip list after recording, length limit, clip names; Advanced: capture method, NVENC preset |
| Replay | On/off, length, screen, Quality (format, encoder, resolution, frame rate, quality or bitrate, disk and video memory estimates), sound tracks, safety limits; Advanced: sound sync, full-range colour |
| Hotkeys | All shortcuts |
| About | Version, credits, run the setup again, export, import and reset settings |

---

## Safety limits

The replay and area recordings run under limits (Settings → Replay → Safety limits), so they can never take over the PC:
- **CPU:** recording yields to games and other programs whenever the CPU is busy, and is never paused. A strict CPU cap is available but not recommended: Windows enforces it by pausing the recorder in bursts, which makes replays choppy.
- **Memory:** capped by Windows itself (1024 MB by default).
- **Graphics card:** recording runs at normal priority, and steps behind games only while the card's video memory is nearly full (95% by default). It returns to normal once memory frees up.
- **Video memory:** watched. Past its limit (512 MB by default) the replay restarts to free it. Settings shows what your quality settings need, so the limit can be set with room to spare.
- **If Persocord closes unexpectedly,** Windows closes its recorder too.

Typical use with a graphics-card encoder is 2–5% CPU, 150–300 MB of memory and 100–250 MB of video memory. The replay keeps frames on the graphics card from start to finish, resizing them with the card's video processor (the part that plays videos, not the 3D engine games use). With a graphics-card encoder it never falls back to the CPU. It also never switches itself off: if the screen can't be captured (Windows starting up, the PC locked, a game switching modes) or the card refuses, it keeps trying by itself. Meanwhile the clip list shows **Replay paused** with a red dot, and the tray icon gets a red dot with the reason in its tooltip. Short blips stay quiet; if it lasts over a minute you get one notification, and another when it's capturing again. Pressing save while it isn't capturing says so straight away.

---

## Updates

Every time Persocord starts, it asks GitHub whether a newer version is out. Only the version number is compared; nothing about your PC is sent. When there is one, a bar at the top of the clip list says so. It never shows over a game or anywhere else.

- **Update** downloads it, closes Persocord, installs it and opens it again with the clip list. It takes a few minutes; a window shows the progress. If anything goes wrong, your current version opens again unchanged and the window says why.
- **What's new** opens the release page with the patch notes.
- **X** hides the bar until Persocord starts again.

If Persocord is busy (recording, saving a replay, the editor open), Update asks you to finish that first. In **Settings → About** you can make Update open the download page instead, to install it yourself.

---

## Backing up your settings

Settings → About → **Your settings**:
- **Copy settings code** / **Import a settings code…**: every setting in one code to paste in chat (starts with `PERSO1-`). Folders, devices and the look are never in it.
- **Export settings…** saves every option to a file.
- **Import settings…** brings them back, on this PC or another.
- **Reset all settings…** puts every option back to how Persocord ships, after two confirmations. The look has its own reset under Customization.

None of these ever touch your clips or the clips folder.

---

## If something goes wrong

- **Diagnostics report:** Settings → About → **Create diagnostics report**. It checks FFmpeg, every encoder, screens and graphics cards, sound devices, app sound capture and the player, adds the log lines that matter, and copies the whole report to paste into a chat (it's also saved as a file). No need to open the log.

- **Log file:** Settings → Library → **Open log file**. The last lines usually say what happened.
- **"Windows protected your PC"** on first run: click **More info → Run anyway** (Persocord isn't code-signed).
- **Clips folder moved or missing:** Persocord finds it again if you moved it; otherwise it asks you to pick it.
- **A link won't open:** some hosts are blocked in some countries or on some networks. Pick another host from **▾**.

---

## What lives where

| What | Where |
|---|---|
| The app | `%LOCALAPPDATA%\Programs\Persocord` |
| Settings, look and log | `%APPDATA%\Persocord` (`settings.json`, `looks.json`, `persocord.log`) |
| Clips | Your clips folder (default `Videos\Persocord`) |

Updating never touches your settings or clips.

---

## Building from source

- **Build and install on this PC:** double-click **Install Persocord.bat**. It installs .NET 8 if it's missing and downloads FFmpeg (the first build takes a few minutes).

**Sharing it:** give people this same zip. They extract it and double-click **Install Persocord.bat**.

**Everyone runs the same versions.** The installer downloads exactly FFmpeg 8.0.1 and checks its fingerprint, so a different file is never used, and the player's VLC engine and every other package are pinned to exact versions, so Persocord behaves the same on every PC. FFmpeg is only ever changed if it really has to be, and that's announced in the update notes. 8.0.1 works with NVIDIA drivers from 570 up, plus Intel, AMD and CPU encoding.

---

## License

Persocord is © 2026 **dodorider** (Discord: **ruined**), under the **PolyForm Noncommercial License 1.0.0** (full text in [LICENSE.md](../LICENSE.md)). This covers every version of Persocord, earlier ones included.

- **Free for anything non-commercial:** use it, change it and share it (yourself, friends, communities, schools, charities).
- **Credit stays with it:** whoever shares Persocord, or a changed version of it, must pass on LICENSE.md, including its `Required Notice` line.
- **No selling without permission:** commercial use, including selling Persocord or a changed version, isn't allowed under this license. For commercial use, contact dodorider on Discord (**ruined**); it's agreed case by case.
- **The name is dodorider's:** "Persocord" and its logo belong to dodorider and aren't covered by the license. A changed version may say it's "based on Persocord by dodorider", but must use its own name and must not look like the official Persocord.
- **Contributions:** anyone who sends code or other changes to be included in Persocord agrees that dodorider may license them under any terms, commercial ones included.

FFmpeg, the VLC engine and the other parts Persocord uses keep their own licenses: see [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

---

Credits: vibecoded by **dodorider + Claude**. Video by FFmpeg, playback by VLC, sound by NAudio.
