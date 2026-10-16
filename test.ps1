$ErrorActionPreference = 'Stop'
$taskDotnet = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.local\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.local\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $taskDotnet test (Join-Path $PSScriptRoot 'Praja.sln') --verbosity minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$taskNode = (Get-Command node -ErrorAction Stop).Source
$taskBrowserTests = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests\browser') -Filter '*.test.mjs' | ForEach-Object { $_.FullName })
Push-Location $PSScriptRoot
try { & $taskNode --test @taskBrowserTests; $taskTestExit = $LASTEXITCODE }
finally { Pop-Location }
exit $taskTestExit
