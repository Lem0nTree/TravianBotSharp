param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root 'artifacts/mcp-publish'
New-Item -ItemType Directory -Force $stage | Out-Null
& $Dotnet publish (Join-Path $root 'WPFUI/WPFUI.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $stage 'app')
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
& $Dotnet publish (Join-Path $root 'TravianBotSharp.Mcp/TravianBotSharp.Mcp.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $stage 'mcp')
if ($LASTEXITCODE -ne 0) { throw 'MCP publish failed.' }
foreach ($guide in @('docs/mcp-setup.md', 'docs/mcp-verification.md', 'mcp-config.example.toml', 'mcp-config.example.json')) {
    Copy-Item -LiteralPath (Join-Path $root $guide) -Destination $stage -Force
}
Write-Host "Published to $stage. The existing installation and database were not modified."
