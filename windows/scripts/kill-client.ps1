Stop-Process -Name RentalClient.Windows -Force -ErrorAction SilentlyContinue
Stop-Process -Name RentalClient -Force -ErrorAction SilentlyContinue
Write-Host "RentalClient closed."
Start-Sleep -Seconds 1
