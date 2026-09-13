# Publishes the local game server (http://localhost:5000) to the internet through a Cloudflare quick tunnel.
# The server also serves the built client (client/dist), so the printed https://<id>.trycloudflare.com
# opens the game directly. A GitHub Pages link with ?server=... is printed too when the repo has a GitHub remote.
# The quick-tunnel address changes on every start.
param([string]$PagesUrl)

if (-not $PagesUrl) {
    $origin = git -C (Split-Path -Parent $PSScriptRoot) remote get-url origin 2>$null
    if ($origin -match 'github\.com[:/]([^/]+)/([^/]+?)(\.git)?$') {
        $PagesUrl = "https://$($Matches[1].ToLower()).github.io/$($Matches[2])/"
    }
}

cloudflared tunnel --no-autoupdate --url http://localhost:5000 2>&1 | ForEach-Object {
    $line = "$_"
    Write-Host $line
    if ($line -match 'https://[a-z0-9-]+\.trycloudflare\.com') {
        $tunnel = $Matches[0]
        Write-Host ""
        Write-Host "Game (served by this PC): $tunnel" -ForegroundColor Green
        if ($PagesUrl) {
            Write-Host "Game (GitHub Pages):      ${PagesUrl}?server=$($tunnel -replace '^https://', '')" -ForegroundColor Green
        }
        Write-Host ""
    }
}
