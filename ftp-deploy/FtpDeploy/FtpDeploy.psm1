# FtpDeploy: verified, resumable FTP uploads for hosts that drop connections, lock files (IIS) or report "ghost" successes.
# Private\ holds the building blocks, Public\ the commands the module exports (one function per file, file name = function name).

$ErrorActionPreference = 'Stop'

$script:ModuleRoot = $PSScriptRoot

foreach ($folder in 'Private', 'Public') {
    Get-ChildItem -Path (Join-Path $PSScriptRoot $folder) -Filter '*.ps1' | Sort-Object Name | ForEach-Object { . $_.FullName }
}

Export-ModuleMember -Function (Get-ChildItem -Path (Join-Path $PSScriptRoot 'Public') -Filter '*.ps1').BaseName
