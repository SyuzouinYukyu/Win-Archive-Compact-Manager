param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$icon = Join-Path $PSScriptRoot 'assets\WACM.ico'
if (-not (Test-Path -LiteralPath $icon)) {
    & (Join-Path $PSScriptRoot 'assets\Generate-Icon.ps1')
}
New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'docs') -Force | Out-Null
& (Join-Path $PSScriptRoot 'assets\Generate-Icon.ps1')
function Checked([string]$title,[scriptblock]$action) { & $action 2>&1 | Tee-Object -FilePath (Join-Path $PSScriptRoot "docs\$title.log"); if ($LASTEXITCODE -ne 0) { throw "$title failed: $LASTEXITCODE" } }
Checked 'restore' { dotnet restore WACM.sln }
Checked 'build' { dotnet build WACM.sln -c Release --no-restore -warnaserror }
if (-not $SkipTests) { Checked 'tests-final' { & '.\tests\WACM.Tests\bin\Release\net10.0-windows\win-x64\WACM.Tests.exe' } }
Checked 'publish' { dotnet publish '.\src\WACM\WACM.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o '.\artifacts\publish' }
$published = Join-Path $PSScriptRoot 'artifacts\publish\WACM.exe'
if (-not (Test-Path -LiteralPath $published)) { throw 'WACM.exe missing' }
$entries = @(Get-ChildItem -LiteralPath (Split-Path $published) -File)
if ($entries.Count -ne 1) { throw 'Unexpected publish sidecar files' }
$smoke = Join-Path $PSScriptRoot ('artifacts\smoke-' + [Guid]::NewGuid().ToString('N'))
# Smoke uses isolated data and never registers the service or edits autostart.
$process = Start-Process -FilePath $published -ArgumentList @('--smoke',('"'+$smoke+'"')) -PassThru -WindowStyle Hidden
if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Standalone smoke timed out' }
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $smoke 'smoke.ok'))) { throw 'Standalone smoke failed' }
$exeDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'EXE'
[void][IO.Directory]::CreateDirectory($exeDirectory)
Copy-Item -LiteralPath $published -Destination (Join-Path $exeDirectory 'WACM.exe') -Force
$exe = Get-Item -LiteralPath (Join-Path $exeDirectory 'WACM.exe')
$finalProof = Join-Path $PSScriptRoot ('artifacts\final-exe-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($finalProof)
$info = [Diagnostics.ProcessStartInfo]::new($exe.FullName)
$info.UseShellExecute = $false
$info.WorkingDirectory = $finalProof
$info.ArgumentList.Add('--smoke'); $info.ArgumentList.Add($finalProof)
$info.Environment['DOTNET_ROOT'] = Join-Path $finalProof 'no-installed-runtime'
$info.Environment['DOTNET_ROOT_X64'] = Join-Path $finalProof 'no-installed-runtime'
$info.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$info.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $finalProof 'bundle-cache'
$process = [Diagnostics.Process]::Start($info)
if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Final EXE smoke timed out' }
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $finalProof 'smoke.ok'))) { throw ('Final EXE smoke failed; inspect ' + $finalProof) }
Copy-Item -LiteralPath (Join-Path $finalProof 'smoke.log') -Destination '.\docs\final-exe-smoke.log' -Force
$runtimeProof = Get-Content -LiteralPath '.\docs\final-exe-smoke.log'
if (-not ($runtimeProof | Where-Object { $_ -like 'RUNTIME=.NET 10.*' })) { throw 'Expected .NET 10 runtime was not loaded' }
if ($runtimeProof | Where-Object { $_ -like 'MODULE=*\dotnet\shared\*' }) { throw 'Runtime loaded from an installed shared framework' }
$proofOutput=Join-Path $PSScriptRoot 'docs\gui-proof'
[void][IO.Directory]::CreateDirectory($proofOutput)
Get-ChildItem -LiteralPath (Join-Path $finalProof 'gui-proof') -Filter '*.png' -File | Copy-Item -Destination $proofOutput -Force
$hash = (Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash
@("Path=$($exe.FullName)","Bytes=$($exe.Length)","SHA256=$hash","FileVersion=$($exe.VersionInfo.FileVersion)","ProductVersion=$($exe.VersionInfo.ProductVersion)","Product=$($exe.VersionInfo.ProductName)","SingleFile=True","SelfContained=True","Runtime=win-x64", "Smoke=PASS", "ServiceInstall=NOT_RUN") | Set-Content -LiteralPath '.\docs\RELEASE.txt' -Encoding utf8
Get-Content -LiteralPath '.\docs\RELEASE.txt'
