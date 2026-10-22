function Get-FtpSettings {
    <#
    .SYNOPSIS
        Loads the FTP settings: built-in defaults < ftp-deploy.settings.psd1 < FTP_DEPLOY_* environment variables < -Override.
    .DESCRIPTION
        The settings file is looked up at -Path, then $env:FTP_DEPLOY_SETTINGS, then ftp-deploy.settings.psd1 next to the
        FtpDeploy folder. A missing file is fine when the connection comes from the environment or -Override.
        A relative ManifestPath is resolved against the settings file's folder.
        Environment variables: FTP_DEPLOY_SERVER, FTP_DEPLOY_USERNAME, FTP_DEPLOY_PASSWORD (handy for CI).
    #>
    [CmdletBinding()]
    param(
        [string]$Path,
        [hashtable]$Override = @{}
    )

    $ErrorActionPreference = 'Stop'

    $settings = @{
        Server                 = ''
        Username               = ''
        Password               = ''
        RemotePath             = ''
        ManifestPath           = ''
        ExcludePaths           = @()
        PruneRemoteRootFiles   = $false
        # Files with these extensions are always re-uploaded (no SIZE pre-check: for a .NET publish it is wasted traffic).
        AlwaysUploadExtensions = @('.html', '.htm', '.dll', '.exe', '.json')
        # Relative paths that are never overwritten in place: staged, SHA-256 verified, then renamed (a DLL IIS holds open).
        VerifiedStagingFiles   = @('Microsoft.AspNetCore.Server.IIS.dll')
        # Never pruned from the remote root.
        ProtectedRootFiles     = @('app_offline.htm')
    }

    if (-not $Path) { $Path = $env:FTP_DEPLOY_SETTINGS }
    if (-not $Path) { $Path = Join-Path (Split-Path $script:ModuleRoot -Parent) 'ftp-deploy.settings.psd1' }

    $settingsDir = $null
    if (Test-Path -LiteralPath $Path) {
        $Path = (Resolve-Path -LiteralPath $Path).Path
        $settingsDir = Split-Path $Path -Parent
        $fromFile = Import-PowerShellDataFile -LiteralPath $Path
        foreach ($key in $fromFile.Keys) {
            if (-not $settings.ContainsKey($key)) {
                throw "Unknown setting '$key' in $Path. Valid settings: $(($settings.Keys | Sort-Object) -join ', ')."
            }
            $settings[$key] = $fromFile[$key]
        }
    }

    if ($env:FTP_DEPLOY_SERVER) { $settings.Server = $env:FTP_DEPLOY_SERVER }
    if ($env:FTP_DEPLOY_USERNAME) { $settings.Username = $env:FTP_DEPLOY_USERNAME }
    if ($env:FTP_DEPLOY_PASSWORD) { $settings.Password = $env:FTP_DEPLOY_PASSWORD }

    foreach ($key in $Override.Keys) {
        if (-not $settings.ContainsKey($key)) { throw "Unknown setting '$key'. Valid settings: $(($settings.Keys | Sort-Object) -join ', ')." }
        $settings[$key] = $Override[$key]
    }

    # "ftp://host/" and "host" mean the same.
    $settings.Server = ([string]$settings.Server) -replace '^ftp://', '' -replace '/+$', ''
    if ($settings.ManifestPath -and $settingsDir -and -not [System.IO.Path]::IsPathRooted($settings.ManifestPath)) {
        $settings.ManifestPath = Join-Path $settingsDir $settings.ManifestPath
    }

    return $settings
}
