param([Parameter(Mandatory)][string]$Payload)
$ErrorActionPreference='Stop'
$payloadRoot=(Get-Item -LiteralPath $Payload).FullName
if(-not (Test-Path -LiteralPath $payloadRoot -PathType Container)){throw 'Release payload must be a directory.'}

# Both the portable ZIP and installer must be built from application files only.
# Never let a validation run's runtime choices become another user's defaults.
$forbidden=@(Get-ChildItem -LiteralPath $payloadRoot -Recurse -Force | Where-Object {
    # AI Gym has no WebView: its libraries must never ship.
    $_.Name -like '*WebView2*' -or
    $_.Name -in @('model-library.json','model-library.json.tmp','ai-connections.json','appearance.json','model-download-settings.json','model-downloads.json','crash.txt') -or
    ($_.PSIsContainer -and $_.Parent.FullName -eq $payloadRoot -and $_.Name -in @('data','ui-qa-data','datasets','comparisons','agent-labs','model-servers','runtime-setup')) -or
    (-not $_.PSIsContainer -and ($_.Extension -in @('.gguf','.safetensors') -or $_.Name -match '^(pytorch_model(?:-\d+-of-\d+)?|adapter_model)\.bin$'))
})
if($forbidden.Count){
    $paths=($forbidden | ForEach-Object {$_.FullName.Substring($payloadRoot.TrimEnd([IO.Path]::DirectorySeparatorChar).Length+1)}) -join ', '
    throw "Release payload contains user runtime settings or profile data: $paths"
}
Write-Output 'PASS release payload contains no runtime settings or profile data'
