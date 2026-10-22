# State of the current sync. The module keeps it here so every helper sees the same connection and options.
$script:Session = $null
$script:Manifest = @{}
$script:EnsuredDirs = $null

function Start-FtpSession {
    param(
        [Parameter(Mandatory)] [hashtable]$Settings,
        [switch]$DryRun,
        [switch]$LoadManifest
    )

    $script:Session = [pscustomobject]@{
        Server                 = $Settings.Server
        Username               = $Settings.Username
        Password               = $Settings.Password
        RemotePath             = [string]$Settings.RemotePath
        ManifestPath           = [string]$Settings.ManifestPath
        AlwaysUploadExtensions = @($Settings.AlwaysUploadExtensions)
        VerifiedStagingFiles   = @($Settings.VerifiedStagingFiles)
        ProtectedRootFiles     = @($Settings.ProtectedRootFiles)
        DryRun                 = $DryRun.IsPresent
    }
    $script:EnsuredDirs = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    $script:Manifest = @{}
    if ($LoadManifest) { Import-FtpManifest }
}

function Get-FtpUri {
    param([string]$Path = '')

    "ftp://$($script:Session.Server)/$Path"
}

function Test-ListContains {
    param([string[]]$List, [string]$Value)

    foreach ($item in $List) {
        if ($Value.Equals($item, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Test-HasExtension {
    param([string[]]$Extensions, [string]$Path)

    foreach ($extension in $Extensions) {
        if ($Path.EndsWith($extension, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

# Fails with a pointer to the fix when the connection is not configured.
function Assert-FtpConnection {
    param([hashtable]$Settings)

    $missing = @('Server', 'Username', 'Password') | Where-Object { [string]::IsNullOrWhiteSpace([string]$Settings[$_]) }
    if ($missing) {
        throw "FTP setting(s) missing: $($missing -join ', '). Copy ftp-deploy.settings.example.psd1 to ftp-deploy.settings.psd1 and fill it in (or set FTP_DEPLOY_SERVER / FTP_DEPLOY_USERNAME / FTP_DEPLOY_PASSWORD)."
    }
}
