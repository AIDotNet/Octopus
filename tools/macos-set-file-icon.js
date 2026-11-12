// Gives a file a custom icon, the way Finder's "Get Info" does (an .icns or any image AppKit reads).
//
//   osascript -l JavaScript tools/macos-set-file-icon.js <image> <file>
//
// Used by the build (src/Octopus.App/Octopus.App.csproj) on the bare executable of a development build: outside an .app
// bundle macOS has no icon for the process and shows the generic "exec" one in Activity Monitor, Force Quit and the
// app switcher's lists. A packaged app takes its icon from the bundle instead.
ObjC.import('AppKit');

function run(argv) {
  if (argv.length !== 2) return 'usage: macos-set-file-icon.js <image> <file>';
  const image = $.NSImage.alloc.initWithContentsOfFile(argv[0]);
  if (!image || image.isNil()) return 'not an image: ' + argv[0];
  return $.NSWorkspace.sharedWorkspace.setIconForFileOptions(image, argv[1], 0) ? 'icon set' : 'icon not set: ' + argv[1];
}
