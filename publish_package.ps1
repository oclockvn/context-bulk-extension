param(
    [Parameter(Mandatory = $false)]
    [string]$ApiKey
)

$forward = @('-LocalPush')
if ($ApiKey) { $forward += @('-ApiKey', $ApiKey) }

pwsh .\scripts\publish-nuget.ps1 @forward
exit $LASTEXITCODE
