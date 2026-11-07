param(
    [string]$AndroidSdk = (Join-Path $env:LOCALAPPDATA 'Android/Sdk'),
    [string]$JavaSdk = (Join-Path $env:LOCALAPPDATA 'Microsoft/jdk-21')
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    & dotnet workload install maui-android
    if ($LASTEXITCODE -ne 0) { throw 'Instalarea workload-ului MAUI Android a eșuat.' }
    & dotnet build src/Steamphone/Steamphone.csproj -t:InstallAndroidDependencies -f net10.0-android "-p:AndroidSdkDirectory=$AndroidSdk" "-p:JavaSdkDirectory=$JavaSdk" -p:AcceptAndroidSDKLicenses=True
    if ($LASTEXITCODE -ne 0) { throw 'Instalarea Android SDK/JDK a eșuat.' }
    Write-Output "Android SDK: $AndroidSdk"
    Write-Output "JDK 21: $JavaSdk"
}
finally { Pop-Location }
