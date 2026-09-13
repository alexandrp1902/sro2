# Starts the game server (port 5000, dotnet watch) and the Vite dev server (port 5173, HMR)
# in separate windows, then prints the URLs to open on the PC and on the iPhone.
$root = Split-Path -Parent $PSScriptRoot

Start-Process powershell -ArgumentList '-NoExit', '-Command', "dotnet watch run --project '$root\server\Sro.Server'"
Start-Process powershell -ArgumentList '-NoExit', '-Command', "Set-Location '$root\client'; npm run dev"

# Private LAN addresses to open from the phone. VPN / ZeroTier / WSL adapters are skipped
# (a full-tunnel VPN owns the default route, so the route table can't be used); all remaining ones are printed.
$ips = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -match '^(192\.168\.|10\.|172\.(1[6-9]|2\d|3[01])\.)' -and $_.InterfaceAlias -notmatch 'VPN|ZeroTier|vEthernet|Loopback|VirtualBox|VMware' } |
    ForEach-Object { $_.IPAddress }

Write-Host ""
Write-Host "PC:     http://localhost:5173"
foreach ($ip in $ips) { Write-Host "iPhone: http://${ip}:5173   (same Wi-Fi)" }
