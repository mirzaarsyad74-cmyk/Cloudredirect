# Agent Guidelines for CloudRedirect

## Post-Task Build Directive
**MANDATORY**: Whenever you make any changes or complete any task in this codebase, you **MUST** automatically build and publish the Windows executable (`.exe`) before ending your turn.

### Build / Publish Commands

To publish the release executable:
```powershell
dotnet publish ui/CloudRedirect.csproj -c Release -r win-x64 --self-contained false -o ui/bin/publish
```

The published executable will be located at:
`ui/bin/publish/CloudRedirect.exe`

To run a fast incremental build check:
```powershell
dotnet build ui/CloudRedirect.csproj -c Release
```

## Project Overview
- **Target Platform**: Windows x64 only.
- **Tech Stack**:
  - Companion Application: C# / WPF on .NET 8 (`ui/CloudRedirect.csproj`).
  - Native Redirection Core: C++20 (`src/`).
  - UI Library: Lepoco `WPF-UI` (4.2.0) with an authentic **Steam Theme** defined in `ui/Themes/SteamTheme.xaml`.
- **Styling Guidelines**:
  - The UI is styled to match the modern Steam desktop client aesthetic (dark navy/charcoal backgrounds, Steam cyan/blue highlights, and iconic Steam "Play" green action buttons).
  - Do not introduce OS Light Mode watchers; the app should consistently maintain its Steam dark theme.
