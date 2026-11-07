param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('android-arm64', 'android-x64', 'android-arm')][string[]]$Runtime = @('android-arm64'),
    [string]$AndroidSdk = $(if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android/Sdk' }),
    [string]$JavaSdk = $(if ($env:JAVA_HOME) { $env:JAVA_HOME } else { Join-Path $env:LOCALAPPDATA 'Microsoft/jdk-21' }),
    [string]$Version = '',
    [ValidateRange(1, 2100000000)][int]$VersionCode = 1,
    [string]$CommitSha = '',
    [switch]$DevelopmentSigning,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$environmentNames = @('ANDROID_SIGNING_KEYSTORE', 'ANDROID_KEY_ALIAS', 'ANDROID_KEYSTORE_PASSWORD', 'ANDROID_KEY_PASSWORD')
$previousEnvironment = @{}
foreach ($name in $environmentNames) { $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
Push-Location $projectRoot
try {
    if (!$Version) {
        [xml]$versionProperties = Get-Content Version.props -Raw
        $Version = $versionProperties.Project.PropertyGroup.SteamphoneVersionMajor + '.' + $versionProperties.Project.PropertyGroup.SteamphoneVersionMinor + '.0'
    }
    if ($Version -notmatch '^\d+\.\d+\.\d+(?:-pr\.\d+)?$') { throw 'Version must be major.minor.patch, optionally followed by -pr.NUMBER.' }
    if (!(Test-Path -LiteralPath (Join-Path $JavaSdk 'bin/java.exe')) -and !(Test-Path -LiteralPath (Join-Path $JavaSdk 'bin/java'))) {
        throw 'JDK 21 is missing. Run scripts/setup-android.ps1 or pass -JavaSdk.'
    }
    $production = $Configuration -eq 'Release' -and !$DevelopmentSigning
    $signingArguments = @()
    if ($production) {
        if (!$env:ANDROID_SIGNING_KEYSTORE) {
            $localConfigPath = Join-Path $projectRoot 'signing.local.json'
            if (!(Test-Path -LiteralPath $localConfigPath)) { throw 'Production signing files are missing. Contributors: pass -DevelopmentSigning. Maintainer: restore the original keystore and signing.local.json.' }
            & node scripts/signing.cjs verify
            if ($LASTEXITCODE -ne 0) { throw 'Signing identity verification failed.' }
            $signing = Get-Content -LiteralPath $localConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $env:ANDROID_SIGNING_KEYSTORE = Join-Path $projectRoot $signing.keystore
            $env:ANDROID_KEY_ALIAS = $signing.alias
            $env:ANDROID_KEYSTORE_PASSWORD = $signing.storePassword
            $env:ANDROID_KEY_PASSWORD = $signing.keyPassword
        }
        foreach ($name in $environmentNames) {
            if (![Environment]::GetEnvironmentVariable($name, 'Process')) { throw "Missing signing environment variable: $name" }
        }
        $signingArguments = @('-p:AndroidKeyStore=true', "-p:AndroidSigningKeyStore=$env:ANDROID_SIGNING_KEYSTORE",
            "-p:AndroidSigningKeyAlias=$env:ANDROID_KEY_ALIAS", '-p:AndroidSigningStorePass=env:ANDROID_KEYSTORE_PASSWORD',
            '-p:AndroidSigningKeyPass=env:ANDROID_KEY_PASSWORD')
    }
    if (!$SkipTests) {
        & dotnet test tests/Steamphone.Core.Tests/Steamphone.Core.Tests.csproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
        & node --test scripts/release.test.cjs scripts/signing.test.cjs
        if ($LASTEXITCODE -ne 0) { throw 'Release automation tests failed.' }
    }
    $releaseDirectory = Join-Path $projectRoot "artifacts/release/$Version"
    New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
    $assets = @()
    foreach ($targetRuntime in $Runtime) {
        $outputDirectory = Join-Path $projectRoot "artifacts/$targetRuntime"
        $arguments = @('publish', 'src/Steamphone/Steamphone.csproj', '-c', $Configuration, '-r', $targetRuntime,
            '-o', $outputDirectory, "-p:AndroidSdkDirectory=$AndroidSdk", "-p:JavaSdkDirectory=$JavaSdk",
            "-p:ApplicationDisplayVersion=$Version", "-p:ApplicationVersion=$VersionCode", '--nologo') + $signingArguments
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) { throw "Android build failed for $targetRuntime." }
        $builtApk = Join-Path $outputDirectory 'com.steamphone.app-Signed.apk'
        if (!(Test-Path -LiteralPath $builtApk)) { throw "Expected signed APK was not produced: $builtApk" }
        if ($production) {
            $javaExecutable = Join-Path $JavaSdk 'bin/java'
            if (Test-Path -LiteralPath ($javaExecutable + '.exe')) { $javaExecutable += '.exe' }
            $signerJar = Join-Path $AndroidSdk 'build-tools/36.0.0/lib/apksigner.jar'
            $verification = & $javaExecutable -jar $signerJar verify --verbose --print-certs $builtApk
            if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed.' }
            $expectedCertificate = (Get-Content signing-certificate.json -Raw | ConvertFrom-Json).sha256
            $certificateLine = $verification | Where-Object { $_ -match 'certificate SHA-256 digest:\s*([a-fA-F0-9]+)' } | Select-Object -First 1
            if (!$certificateLine -or $certificateLine -notmatch [regex]::Escape($expectedCertificate)) { throw 'The APK was signed with an unexpected certificate.' }
        }
        $architecture = $targetRuntime.Substring('android-'.Length)
        $suffix = $(if ($production) { '' } else { '-development' })
        $assetName = "Steamphone-v$Version-$architecture$suffix.apk"
        Copy-Item -LiteralPath $builtApk -Destination (Join-Path $releaseDirectory $assetName) -Force
        $assets += [ordered]@{ name = $assetName; architecture = $architecture; bytes = (Get-Item $builtApk).Length;
            sha256 = (Get-FileHash -LiteralPath $builtApk -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $manifest = [ordered]@{ app = 'Steamphone'; version = $Version; versionCode = $VersionCode;
        commit = $CommitSha; productionSigned = [bool]$production; assets = @($assets) }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $releaseDirectory 'manifest.json'), ($manifest | ConvertTo-Json -Depth 6) + "`n", $utf8)
    $checksumLines = @($assets | ForEach-Object { $_.sha256 + '  ' + $_.name })
    $checksumLines += (Get-FileHash -LiteralPath (Join-Path $releaseDirectory 'manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant() + '  manifest.json'
    [IO.File]::WriteAllText((Join-Path $releaseDirectory 'SHA256SUMS.txt'), ($checksumLines -join "`n") + "`n", $utf8)
    Write-Output "Built Steamphone v$Version (Android versionCode $VersionCode)."
    Get-ChildItem -LiteralPath $releaseDirectory -Filter '*.apk' | Select-Object FullName, Length
}
finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
    Pop-Location
}
