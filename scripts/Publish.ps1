param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,[switch]$Installer,[string]$InnoCompiler='')
$ErrorActionPreference='Stop'
$appRoot=Split-Path $PSScriptRoot -Parent
$payload=Join-Path $appRoot "artifacts/AiGym-$Version-win-x64"
if(Test-Path -LiteralPath $payload){throw "Release already exists: $payload"}
dotnet publish (Join-Path $appRoot 'src/AiGym/AiGym.csproj') -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:UseSharedCompilation=false -p:NuGetAudit=false -p:Version=$Version -o $payload -m:1
if($LASTEXITCODE -ne 0){throw 'App publish failed'}
# Debug symbols stay with the build output; the release payload ships without them.
Remove-Item -LiteralPath (Join-Path $payload 'AiGym.pdb') -ErrorAction SilentlyContinue
& (Join-Path $PSScriptRoot 'Assert-ReleasePayload.ps1') -Payload $payload
Copy-Item -LiteralPath (Join-Path $appRoot 'README.md') -Destination $payload
$notices=Join-Path $appRoot 'THIRD-PARTY-NOTICES.md'
if(Test-Path -LiteralPath $notices){Copy-Item -LiteralPath $notices -Destination $payload}
New-Item -ItemType Directory -Path (Join-Path $payload 'licenses') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $appRoot 'brand/logo-kit/fonts/LICENSE-Manrope.txt') -Destination (Join-Path $payload 'licenses')
if(-not(Test-Path -LiteralPath (Join-Path $payload 'AiGym.exe')) -or -not(Test-Path -LiteralPath (Join-Path $payload 'AiGym.pri'))){throw 'Executable or WinUI resources missing'}
Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object{[pscustomobject]@{Path=$_.FullName.Substring($payload.Length+1);Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash;Bytes=$_.Length}} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $payload 'payload-manifest.json') -Encoding utf8
Compress-Archive -LiteralPath $payload -DestinationPath "$payload.zip" -CompressionLevel Optimal
if($Installer){
    if(-not(Test-Path -LiteralPath $InnoCompiler -PathType Leaf)){throw 'Provide an existing Inno Setup compiler with -InnoCompiler.'}
    & $InnoCompiler "/DAppVersion=$Version" "/DPayloadDir=$payload" "/DOutputPath=$appRoot/artifacts" (Join-Path $appRoot 'packaging/AiGym.iss')
    if($LASTEXITCODE -ne 0){throw 'Installer compile failed'}
}
Write-Output "Published AI Gym: $payload"
