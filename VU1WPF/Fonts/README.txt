Fonts bundled into the app (compiled in as WPF resources).

- Inter (Light, Regular, Medium, SemiBold) - UI text. SIL Open Font License 1.1, see OFL-Inter.txt.

Optional: dial name / percentage font
-------------------------------------
The dial name and percentage use a monospaced "display" font. The app looks for these, in order:
  1. "JetBrains Mono" in this folder (drop JetBrainsMono-*.ttf here and rebuild - free, OFL licence,
     https://www.jetbrains.com/lp/mono/)
  2. "Cascadia Mono" (ships with Windows 11 / Windows Terminal)
  3. "Consolas" (all Windows versions)
To use a different font, change DisplayFontStack in App.xaml.cs.
