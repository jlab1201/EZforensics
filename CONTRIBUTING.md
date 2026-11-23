# Contributing

## Build and run

- Install the .NET 8 SDK on Windows.
- Build: `dotnet build EzForensics.sln`
- Run: `dotnet run --project src/EzForensics.App`

## Test

- `dotnet test tests/EzForensics.Core.Tests`
- `dotnet test tests/EzForensics.App.Tests`
- Run the tests before you open a pull request.

## Changes

- Create a branch from `main`.
- Keep each change small and focused.
- Open a pull request and say what changed and why.
- Add or update tests for logic changes.

## Docs

- Keep docs short, plain and direct.
- Update the README or CHANGELOG when behaviour changes.

## Screenshots

README images are rendered from the demo case by an opt-in test:

```powershell
$env:EZFORENSICS_README_SHOTS='1'; dotnet test tests/EzForensics.App.Tests --filter "FullyQualifiedName~ReadmeScreenshotTests"
```

Output goes to `docs/images`. The AI page graph (a WebView2 page) is drawn from made-up notes with headless Edge: `powershell -File build\make-graph-shot.ps1`.

## Security

- Do not report security issues in public issues or pull requests.
- Follow [SECURITY.md](SECURITY.md).

## License

By contributing you agree your work is released under the MIT license.
