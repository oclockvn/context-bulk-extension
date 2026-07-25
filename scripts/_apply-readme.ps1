$src = 'd:\src\context-bulk-extension\README.md.new'
$dst = 'd:\src\context-bulk-extension\README.md'
$body = [IO.File]::ReadAllText($src)
for ($i = 0; $i -lt 120; $i++) {
    try {
        [IO.File]::WriteAllText($dst, $body)
        Remove-Item $src -Force
        Remove-Item $PSCommandPath -Force -ErrorAction SilentlyContinue
        Write-Host "README.md updated"
        exit 0
    }
    catch {
        Start-Sleep -Seconds 1
    }
}
Write-Error 'timed out waiting for README.md unlock'
exit 1
