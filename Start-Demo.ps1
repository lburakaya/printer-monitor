$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
Write-Host 'Printer Monitor demo: http://localhost:5080' -ForegroundColor Green
Write-Host 'Durdurmak icin Ctrl+C. Gercek yazicilara istek gonderilmez.'
dotnet run --project src/PrinterMonitor -- --Monitor:DemoMode=true --urls http://localhost:5080
exit $LASTEXITCODE
