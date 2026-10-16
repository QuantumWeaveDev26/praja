$ErrorActionPreference = 'Stop'
$taskDotnet = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$taskAssembly = Join-Path $PSScriptRoot 'src\Praja.Web\bin\Debug\net10.0\Praja.Web.dll'
if (!(Test-Path -LiteralPath $taskAssembly)) { throw 'Build the app first with test.ps1, then retry.' }
Write-Host 'Local Praja password recovery. Your account and cases will be preserved.'
Write-Host 'Choose a new password of 14–128 characters. Input stays hidden.'
$taskSecret = Read-Host 'New password' -AsSecureString
$taskConfirmation = Read-Host 'Confirm new password' -AsSecureString
$taskSecretPointer = [IntPtr]::Zero
$taskConfirmationPointer = [IntPtr]::Zero
$taskPassword = $null
$taskRepeat = $null
$taskProcess = $null
$taskInputBytes = $null
try {
    $taskSecretPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($taskSecret)
    $taskConfirmationPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($taskConfirmation)
    $taskPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($taskSecretPointer)
    $taskRepeat = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($taskConfirmationPointer)
    if ($taskPassword -cne $taskRepeat) { throw 'Passwords do not match. No account was changed.' }
    if ($taskPassword.Length -lt 14 -or $taskPassword.Length -gt 128 -or $taskPassword -match '[\x00-\x1F\x7F]') { throw 'Choose a password of 14–128 characters without control characters.' }
    $taskStart = New-Object System.Diagnostics.ProcessStartInfo
    $taskStart.FileName = $taskDotnet
    $taskStart.Arguments = '"' + $taskAssembly + '" --reset-officer-password'
    $taskStart.WorkingDirectory = Join-Path $PSScriptRoot 'src\Praja.Web'
    $taskStart.UseShellExecute = $false
    $taskStart.CreateNoWindow = $true
    $taskStart.RedirectStandardInput = $true
    $taskStart.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Development'
    $taskStart.EnvironmentVariables['DOTNET_CLI_HOME'] = Join-Path $PSScriptRoot '.local\dotnet-home'
    $taskStart.EnvironmentVariables['NUGET_PACKAGES'] = Join-Path $PSScriptRoot '.local\nuget'
    $taskStart.EnvironmentVariables['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    # The password travels over stdin, never through arguments, environment or a file.
    $taskProcess = [System.Diagnostics.Process]::Start($taskStart)
    $taskInputBytes = [System.Text.Encoding]::UTF8.GetBytes($taskPassword + [Environment]::NewLine)
    $taskProcess.StandardInput.BaseStream.Write($taskInputBytes, 0, $taskInputBytes.Length)
    $taskProcess.StandardInput.BaseStream.Flush()
    $taskProcess.StandardInput.Close()
    $taskProcess.WaitForExit()
    if ($taskProcess.ExitCode -ne 0) { throw 'Recovery failed. No success was confirmed; see the error above.' }
} finally {
    $taskPassword = $null; $taskRepeat = $null
    if ($null -ne $taskInputBytes) { [Array]::Clear($taskInputBytes, 0, $taskInputBytes.Length) }
    if ($taskSecretPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($taskSecretPointer) }
    if ($taskConfirmationPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($taskConfirmationPointer) }
    $taskSecret.Dispose(); $taskConfirmation.Dispose()
    if ($null -ne $taskProcess) { $taskProcess.Dispose() }
}
