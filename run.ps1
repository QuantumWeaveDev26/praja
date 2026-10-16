param([int]$Port = 5080)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.local\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.local\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
& $taskDotnet run --project (Join-Path $taskRoot 'src\Praja.Web\Praja.Web.csproj') --urls "http://127.0.0.1:$Port"
exit $LASTEXITCODE
