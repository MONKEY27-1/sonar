# Building the Soundboard installer

Two tools, two steps. Neither is included here — both are free downloads.

## 1. Publish the app

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) installed.
From `src\Soundboard`, run:

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
```

This bundles the .NET runtime into the output, so people running the installer
don't need anything pre-installed on their machine — no ".NET Desktop Runtime
not found" errors. It deliberately skips single-file publishing, which has
known rough edges with WPF apps specifically; the plain multi-file output gets
packaged up as a whole by the installer anyway, so there's no real downside.

Output lands at:
```
src\Soundboard\bin\Release\net8.0-windows\win-x64\publish\
```

## 2. Compile the installer

Install [Inno Setup](https://jrsoftware.org/isdl.php) (free), then either:

- Open `Soundboard.iss` in the Inno Setup IDE and click **Compile**, or
- Run from the command line: `ISCC.exe Soundboard.iss`

The finished installer lands at `installer\Output\SonarSetup.exe`.

## 3. Publish a release (for the in-app auto-updater to find)

The app checks GitHub Releases on launch and offers to self-update. To ship a
new version so that check finds it:

1. Bump `<Version>` in `Soundboard.csproj` — this is now the **only** place
   the version is set. `Soundboard.iss` reads it straight off the compiled
   exe's version resource (`GetVersionNumbersString()`), so there's no second
   number to keep in sync anymore.
2. Publish, then compile the installer (steps 1–2 above).
3. On GitHub, create a new Release tagged `vX.Y.Z` (matching the version you
   just bumped to) and upload `SonarSetup.exe` as its asset.

The updater picks the first `.exe` asset on the latest non-draft,
non-prerelease release, so the filename doesn't need to match anything in
particular — just make sure exactly one `.exe` is attached.

## Notes

- **Per-user install, no admin required.** Installs to `%LocalAppData%\Programs\Soundboard`
  by default, matching how most modern desktop apps (VS Code, Discord, etc.)
  install themselves. No UAC prompt for a normal install.
- **Uninstalling never touches your data.** Settings, your sound library, and
  imported files live in `%LocalAppData%\Soundboard` — separate from the app
  itself — and are left alone on uninstall. Only the program files are removed.
- **`AppId` in the .iss file must never change** between releases. It's how
  Windows recognizes "this is an upgrade of the same app" rather than a
  separate side-by-side install. Already generated and set — just leave it.
