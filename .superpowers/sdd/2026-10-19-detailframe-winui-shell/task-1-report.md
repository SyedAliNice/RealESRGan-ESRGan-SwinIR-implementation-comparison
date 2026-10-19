# Task 1 report

Status: DONE.

## Evidence
- NuGet versions verified via api.nuget.org flat-container index and successful restore: Microsoft.WindowsAppSDK 2.5.1 (resolved in project.assets.json), Microsoft.Windows.SDK.BuildTools 10.0.28000.2705, Microsoft.NET.Test.Sdk 18.11.0, xunit 2.9.3, xunit.runner.visualstudio 3.1.5 (restored OK).
- `dotnet test DetailFrame.sln`: Passed! Failed: 0, Passed: 3, Total: 3 (net10.0).
- `dotnet build DetailFrame.sln`: Build succeeded, 0 warnings, 0 errors; app output bin\x86\Debug\net10.0-windows10.0.19041.0\DetailFrame.App.dll (default solution platform resolves to x86; x64/ARM64 platforms declared).
- TDD red step not captured separately: the first `dotnet test` failed on restore (NU1301, machine NuGet config has a non-existent local source `C:\packages`), then passed once WorkflowState existed.

## Deviations
- Added root `nuget.config` (clears sources, nuget.org only) because of the broken machine-level source.
- `WorkflowState` lives in `src/DetailFrame.App/Models/WorkflowState.cs` (record, `Empty`, CanEnhance requires input, model, scale>0, device, no active job). The test project compiles that file via link instead of referencing the WinUI project.
- Added `app.manifest` (referenced by csproj). `WindowsPackageType=None` (unpackaged).
- .gitignore: bin/, obj/, *.user, .vs/.

## Concerns
- WinAppSDK 2.5.1 chosen as latest stable listed; 2.x not runtime-launched here (build only).
- Test and app compile WorkflowState separately; no launch test of the UI.

## Fix round 1
Finding: solution mapped Any CPU to x86 for the app. Fix: DetailFrame.sln now maps Debug/Release|Any CPU to x64 for DetailFrame.App (x64 was already x64; x86 retained as explicit opt-in since the brief does not forbid it). Verified: dotnet build DetailFrame.sln outputs src\DetailFrame.App\bin\x64\Debug\net10.0-windows10.0.19041.0\DetailFrame.App.dll; dotnet test DetailFrame.sln: Passed! Failed: 0, Passed: 3, Total: 3.
