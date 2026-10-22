<#
.SYNOPSIS
    Uploads a local folder over FTP and verifies every file. Connection and options come from ftp-deploy.settings.psd1;
    any parameter given here overrides the file.
.EXAMPLE
    .\Upload-Ftp.ps1 -LocalPath .\publish
.EXAMPLE
    .\Upload-Ftp.ps1 -LocalPath .\publish -RemotePath site -PruneRemoteRootFiles -DryRun
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$LocalPath,

    [string]$Server,
    [string]$Username,
    [string]$Password,
    [string]$RemotePath,
    [string[]]$ExcludePaths,

    # JSON file of {relativePath: sha256} for files already verified on the server (see README).
    [string]$ManifestPath,

    # Remove files directly in the remote root that are not in the local root (folders are never touched).
    [switch]$PruneRemoteRootFiles,

    [switch]$DryRun,

    # Settings file to use instead of ftp-deploy.settings.psd1.
    [string]$SettingsPath
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "FtpDeploy") -Force

$override = @{}
foreach ($name in 'Server', 'Username', 'Password') {
    if ($PSBoundParameters.ContainsKey($name)) { $override[$name] = $PSBoundParameters[$name] }
}

$settings = Get-FtpSettings -Path $SettingsPath -Override $override

$sync = @{ Settings = $settings; LocalPath = $LocalPath; DryRun = $DryRun; PruneRemoteRootFiles = $PruneRemoteRootFiles }
foreach ($name in 'RemotePath', 'ExcludePaths', 'ManifestPath') {
    if ($PSBoundParameters.ContainsKey($name)) { $sync[$name] = $PSBoundParameters[$name] }
}

Invoke-FtpSync @sync | Out-Null
