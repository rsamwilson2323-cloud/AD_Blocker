<# Builds everything into .\bin. Requires the .NET 8 SDK (https://dotnet.microsoft.com/download). Node.js is optional (regenerates extension rules). #>
[CmdletBinding()]
param([switch]$Test, [switch]$SelfContained)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 8 SDK not found. Install it, then re-run.' }
if (Get-Command node -ErrorAction SilentlyContinue) { node tools\build-rules.js }
if ($Test) {
  dotnet test tests\AdBlocker.Tests -c Release; if ($LASTEXITCODE) { throw 'C# tests failed' }
  if (Get-Command node -ErrorAction SilentlyContinue) { node --test extension-tests\extension.test.js; if ($LASTEXITCODE) { throw 'Extension tests failed' } }
}
$sc = if ($SelfContained) { @('-r', 'win-x64', '--self-contained', 'true') } else { @('--self-contained', 'false') }
dotnet publish src\AdBlocker.Service -c Release -o bin @sc; if ($LASTEXITCODE) { throw 'Service build failed' }
dotnet publish src\AdBlocker.App -c Release -o bin @sc;     if ($LASTEXITCODE) { throw 'App build failed' }
New-Item -ItemType Directory -Force bin\rules, bin\config, bin\scripts | Out-Null
Copy-Item rules\*.txt bin\rules -Force
Copy-Item config\*.json bin\config -Force
Copy-Item src\AdBlocker.Service\scripts\*.ps1 bin\scripts -Force
Write-Host "Build complete. Next: .\Install-Service.ps1 (as Administrator), then Run.bat."
