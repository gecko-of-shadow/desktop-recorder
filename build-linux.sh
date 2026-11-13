#!/bin/sh
# Byte-compile the Linux app. Pass --install to copy it into ~/.local.
set -eu
cd "$(dirname "$0")"
python3 -m compileall -q linux/desktop_recorder
if [ "${1:-}" != "--install" ]; then
  echo "Compiled linux/desktop_recorder. Run $0 --install to install it for this user."
  exit 0
fi

prefix="${PREFIX:-$HOME/.local}"
dest="$prefix/share/desktop-recorder"
mkdir -p "$dest/desktop_recorder" "$prefix/bin" "$prefix/share/applications"
cp -a linux/desktop_recorder/. "$dest/desktop_recorder/"
cat > "$prefix/bin/desktop-recorder" << EOF
#!/bin/sh
export DESKTOP_RECORDER_COMMAND="$prefix/bin/desktop-recorder"
export PYTHONPATH="$dest\${PYTHONPATH:+:\$PYTHONPATH}"
exec python3 -m desktop_recorder "\$@"
EOF
chmod +x "$prefix/bin/desktop-recorder"
cat > "$prefix/share/applications/desktop-recorder.desktop" << EOF
[Desktop Entry]
Type=Application
Name=Desktop Recorder
Comment=Record the desktop to MP4
Exec=$prefix/bin/desktop-recorder
Icon=media-record
Terminal=false
Categories=AudioVideo;Recorder;
StartupNotify=false
EOF
echo "Installed $prefix/bin/desktop-recorder"
echo "If that directory is not on PATH yet, log out and back in, or run:"
echo "  export PATH=\"$prefix/bin:\$PATH\""
