# Deploy the D2D gateway to https://gateway.drivetodev.online
# FTP login: ftp-deploy\ftp-deploy.settings.psd1 (git-ignored; template: ftp-deploy\ftp-deploy.settings.example.psd1).
$ErrorActionPreference = "Stop"
& "$PSScriptRoot\deploy-single-host.core.ps1" -StdoutLog @args   # keep ANCM stdout logs; remove once stable
