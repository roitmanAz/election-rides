# Runs the whole system locally: database (Docker), API (port 5194) and web (port 4200).
# Usage: powershell -ExecutionPolicy Bypass -File .\dev.ps1
$root = $PSScriptRoot

Write-Host "Starting database..."
docker compose -f "$root\docker-compose.yml" up -d --wait
if (-not $?) { Write-Host "Docker is not running. Start Docker Desktop and try again."; exit 1 }

Write-Host "Applying database migrations..."
dotnet ef database update -p "$root\api\src\ElectionRides.Api" -s "$root\api\src\ElectionRides.Api"

Write-Host "Starting API on http://localhost:5194 ..."
Start-Process powershell -ArgumentList "-NoExit", "-Command", "dotnet run --project '$root\api\src\ElectionRides.Api'"

Write-Host "Starting web on http://localhost:4200 ..."
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$root\web'; npm start"

Write-Host "Open http://localhost:4200"
