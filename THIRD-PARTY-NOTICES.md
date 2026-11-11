# Third-party notices

Persocord is licensed under the PolyForm Noncommercial License 1.0.0 (see
LICENSE.md). It uses the following software, which keeps its own licence.
Those licences, not Persocord's, apply to these parts.

| Part | What it does in Persocord | Licence | Source |
|---|---|---|---|
| FFmpeg | Recording, saving, compressing, converting | LGPL 2.1 or later; the "essentials" build that the installer downloads is GPL 3 | https://ffmpeg.org (build: https://www.gyan.dev/ffmpeg/builds/) |
| VLC engine (libVLC, VideoLAN.LibVLC.Windows) | The built-in player | LGPL 2.1 or later (some plugins GPL 2 or later) | https://www.videolan.org |
| LibVLCSharp, LibVLCSharp.WPF | Connects the player to Persocord | LGPL 2.1 or later | https://code.videolan.org/videolan/LibVLCSharp |
| NAudio | Sound capture | MIT | https://github.com/naudio/NAudio |
| H.NotifyIcon.Wpf | The tray icon | MIT | https://github.com/HavenDV/H.NotifyIcon |
| RNNoise model (voice.rnnn, rnnoise-nu format) | The microphone's noise suppression, run by FFmpeg | See its source | https://github.com/GregorR/rnnoise-models |
| .NET and WPF | The app's runtime | MIT | https://github.com/dotnet |

FFmpeg and the VLC engine run as separate programs and libraries; Persocord
doesn't change them. Their full source code is available from the links above.
