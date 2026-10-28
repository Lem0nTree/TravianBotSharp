param([string]$AppDirectory, [switch]$Disable)

$ErrorActionPreference = 'Stop'
if (-not $AppDirectory) { $AppDirectory = Split-Path -Parent $PSScriptRoot }
$AppDirectory = (Resolve-Path -LiteralPath $AppDirectory).Path
$settingsPath = Join-Path $AppDirectory 'twilio-alerts.local.json'
$settings = if (Test-Path -LiteralPath $settingsPath) {
    Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
} else { [pscustomobject]@{ Enabled = $false; ToNumber = ''; AccountSid = ''; FromNumber = '' } }

if ($Disable) {
    $settings.Enabled = $false
    $settings | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8
    Write-Output 'Attack calls disabled. Restart the bot to apply.'
    return
}

$accountSid = Read-Host 'Twilio Account SID (AC...)'
$fromNumber = Read-Host 'Twilio Voice caller number, including +country code'
$toNumber = Read-Host 'Verified destination number, including +country code'
if ($accountSid -notmatch '^AC[0-9a-fA-F]{32}$') { throw 'Invalid Account SID.' }
foreach ($number in @($fromNumber, $toNumber)) {
    if ($number -notmatch '^\+[1-9][0-9]{7,14}$') { throw 'Use full international phone numbers with +country code.' }
}

$secureToken = Read-Host 'Twilio Auth Token (hidden)' -AsSecureString
$pointer = [IntPtr]::Zero
$plaintext = $null
$temporaryToken = Join-Path $AppDirectory ('twilio-auth.local.dpapi.' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
    $plaintext = [Text.Encoding]::UTF8.GetBytes([Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer))
    if ([Text.Encoding]::UTF8.GetString($plaintext) -notmatch '^[a-fA-F0-9]{32}$') { throw 'Invalid Auth Token.' }
    $encrypted = [Security.Cryptography.ProtectedData]::Protect($plaintext,
        [Text.Encoding]::UTF8.GetBytes('TravianBotSharp-Twilio-v1'), [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.File]::WriteAllBytes($temporaryToken, $encrypted)
    Move-Item -LiteralPath $temporaryToken -Destination (Join-Path $AppDirectory 'twilio-auth.local.dpapi') -Force
    @{ Enabled = $true; AccountSid = $accountSid; FromNumber = $fromNumber; ToNumber = $toNumber } |
        ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8
    Write-Output 'Attack alerts configured. The token is encrypted for this Windows user. Restart the bot to apply. No call was made.'
}
finally {
    if ($null -ne $plaintext) { [Array]::Clear($plaintext, 0, $plaintext.Length) }
    if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    $secureToken.Dispose()
    Remove-Item -LiteralPath $temporaryToken -ErrorAction SilentlyContinue
}
