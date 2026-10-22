function Remove-FtpRemoteFile {
    <#
    .SYNOPSIS
        Deletes one file on the FTP server (path relative to the login root). Throws when the server refuses.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [hashtable]$Settings,
        [Parameter(Mandatory)] [string]$Path
    )

    $ErrorActionPreference = 'Stop'
    Assert-FtpConnection $Settings
    Start-FtpSession -Settings $Settings

    $relative = $Path.Replace('\', '/').TrimStart('/')
    if (-not (Remove-RemoteFile -Uri (Get-FtpUri $relative))) {
        throw "Could not delete ftp://$($Settings.Server)/$relative"
    }
}
