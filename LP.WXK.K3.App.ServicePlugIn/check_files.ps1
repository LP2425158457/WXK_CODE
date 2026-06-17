$files = @(
    'c:\Code\WXK\LP.WXK.K3.App.ServicePlugIn\OASyncService.cs',
    'c:\Code\WXK\LP.WXK.K3.App.ServicePlugIn\RecDetailService.cs'
)
foreach ($f in $files) {
    $item = Get-Item $f -ErrorAction SilentlyContinue
    if ($item) {
        Write-Output ("Path: " + $item.FullName + " | IsReadOnly: " + $item.IsReadOnly + " | Mode: " + $item.Mode)
    } else {
        Write-Output ("Not found: " + $f)
    }
}

Write-Output "---- Checking file locks ----"
$handle = (Get-Process | Where-Object { $_.Path -ne $null }) | ForEach-Object {
    try {
        $_.Modules | Where-Object { $_.FileName -like '*.cs' -and ($_.FileName -like 'OASyncService.cs' -or $_.FileName -like 'RecDetailService.cs') } | Select-Object FileName
    } catch {}
}
$handle | ForEach-Object { Write-Output ("Locked by: " + $_.FileName) }
