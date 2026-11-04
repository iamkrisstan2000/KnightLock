
# Rewrites the Win32 version-resource strings that .NET derives from the assembly name,
# so Explorer's Properties tab shows KnightLock.exe (not KnightLock.dll) as the original
# filename. Purely cosmetic, byte-for-byte same length, and safe to run before signing.
param(
    [Parameter(Mandatory = $true)][string]$Exe
)

$path = (Resolve-Path $Exe).Path
$bytes = [System.IO.File]::ReadAllBytes($path)

$pairs = @(
    @{ From = "KnightLock.dll"; To = "KnightLock.exe" }
)

foreach ($pair in $pairs) {
    if ($pair.From.Length -ne $pair.To.Length) {
        throw "Replacement must preserve length: $($pair.From) -> $($pair.To)"
    }

    $from = [System.Text.Encoding]::Unicode.GetBytes($pair.From)
    $to = [System.Text.Encoding]::Unicode.GetBytes($pair.To)
    $count = 0

    for ($i = 0; $i -le $bytes.Length - $from.Length; $i++) {
        $match = $true
        for ($j = 0; $j -lt $from.Length; $j++) {
            if ($bytes[$i + $j] -ne $from[$j]) { $match = $false; break }
        }
        if ($match) {
            [Array]::Copy($to, 0, $bytes, $i, $to.Length)
            $count++
            $i += $from.Length - 1
        }
    }

    Write-Host ("  {0} -> {1}  ({2} occurrence(s))" -f $pair.From, $pair.To, $count)
}

[System.IO.File]::WriteAllBytes($path, $bytes)
Write-Host "Version resource updated."
