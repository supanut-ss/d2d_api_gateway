function Test-FtpRemoteFolder {
    <#
    .SYNOPSIS
        Whether a folder exists directly under the FTP login root. A cheap guard against deploying to a wrong or misspelled path.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [hashtable]$Settings,
        [Parameter(Mandatory)] [string]$Name
    )

    $ErrorActionPreference = 'Stop'
    Assert-FtpConnection $Settings
    Start-FtpSession -Settings $Settings

    try { $names = @(Get-RemoteEntryNames) }
    catch { throw "Cannot list ftp://$($Settings.Server)/ : $($_.Exception.Message)" }

    return $names -contains $Name
}
