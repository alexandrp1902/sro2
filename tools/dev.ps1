# Starts the game server (port 5000, dotnet watch) and the Vite dev server (port 5173, HMR)
# in separate windows, then prints the URLs to open on the PC and on the iPhone.
$root = Split-Path -Parent $PSScriptRoot

Start-Process powershell -ArgumentList '-NoExit', '-Command', "dotnet watch run --project '$root\server\Sro.Server'"
Start-Process powershell -ArgumentList '-NoExit', '-Command', "Set-Location '$root\client'; npm run dev"

# The LAN address is the one on the interface that owns the default route (skips VPN / ZeroTier / WSL adapters).
$ip = Get-NetIPConfiguration |
    Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' -and $_.InterfaceAlias -notmatch 'VPN|ZeroTier|vEthernet' } |
    Select-Object -First 1 -ExpandProperty IPv4Address |
    Select-Object -First 1 -ExpandProperty IPAddress

Write-Host ""
Write-Host "PC:     http://localhost:5173"
Write-Host "iPhone: http://${ip}:5173   (same Wi-Fi)"
