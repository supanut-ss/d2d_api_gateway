# Whether a relative path (forward slashes) equals an excluded entry or lives below one.
function Test-Excluded {
    param($Excluded, [string]$RelativePath)

    foreach ($entry in $Excluded) {
        if ($RelativePath.Equals($entry, [System.StringComparison]::OrdinalIgnoreCase) -or
            $RelativePath.StartsWith("$entry/", [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}
