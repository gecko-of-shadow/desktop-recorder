Desktop Recorder 1.0.0 for Ubuntu 24.04 (portable)

Unpack this archive anywhere and run:

    ./desktop-recorder

No root is required to run it. Settings go to
~/.config/DesktopRecorder/settings.txt. The program is the Python files
in this folder; it does not install itself.

These Ubuntu 24.04 packages still have to be installed once. They are
shared libraries and the ffmpeg encoder, not part of this archive:

    sudo apt update
    sudo apt install ffmpeg python3 python3-gi python3-gi-cairo \
      gir1.2-gtk-3.0 gir1.2-ayatanaappindicator3-0.1 gir1.2-notify-0.7 \
      gstreamer1.0-plugins-base gstreamer1.0-pipewire \
      pipewire pipewire-pulse wireplumber pulseaudio-utils \
      xdg-desktop-portal xdg-desktop-portal-gnome xdg-desktop-portal-gtk \
      xclip wl-clipboard

GTK draws the tray and the settings window. GStreamer and PipeWire are
how Wayland screen capture reaches ffmpeg. ffmpeg encodes the MP4.
PulseAudio tools talk to PipeWire for system audio (what you hear).

X11 records the whole display with ffmpeg x11grab. Wayland asks you
which screen to share. Super+Shift+D is the shortcut on X11. On GNOME
Wayland the program adds a custom shortcut with the same keys.

    ./desktop-recorder --help
    ./desktop-recorder --check
    ./desktop-recorder --smoke 2

--smoke records two seconds with X11 and exits.
